using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Xml.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ReminderTaskSchedulerServiceTests
{
    [TestMethod]
    public void ScheduledTaskRunsAsInteractiveLeastPrivilegeUserAndStartsWhenAvailable()
    {
        var executable = Path.Combine(Path.GetTempPath(), "Layal's Program Files", "Layal.exe");
        var dueAt = new DateTimeOffset(2026, 11, 10, 18, 45, 0, TimeSpan.FromHours(2));
        var xml = ReminderTaskSchedulerService.BuildTaskXml(dueAt, executable, "S-1-5-21-1000-2000-3000-1001");
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var action = document.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.AreEqual("1.4", document.Root.Attribute("version")!.Value);
        Assert.AreEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            action.Element(ns + "Command")!.Value);
        Assert.AreEqual(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            action.Element(ns + "WorkingDirectory")!.Value);
        var arguments = action.Element(ns + "Arguments")!.Value;
        var encodedScript = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encodedScript));
        StringAssert.Contains(script, "Start-Process");
        StringAssert.Contains(script, "-ArgumentList '--scheduled-reminder'");
        StringAssert.Contains(script, "Layal''s Program Files");
        Assert.IsFalse(script.Contains("Layal's Program Files", StringComparison.Ordinal),
            "Executable paths must be escaped as PowerShell single-quoted literals.");
        Assert.AreEqual("InteractiveToken", document.Root.Element(ns + "Principals")!
            .Element(ns + "Principal")!.Element(ns + "LogonType")!.Value);
        Assert.AreEqual("LeastPrivilege", document.Root.Element(ns + "Principals")!
            .Element(ns + "Principal")!.Element(ns + "RunLevel")!.Value);
        Assert.AreEqual("true", document.Root.Element(ns + "Settings")!
            .Element(ns + "StartWhenAvailable")!.Value);
        var trigger = document.Root.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!;
        var startBoundaryText = trigger.Element(ns + "StartBoundary")!.Value;
        var endBoundaryText = trigger.Element(ns + "EndBoundary")!.Value;
        Assert.IsTrue(startBoundaryText.EndsWith("Z", StringComparison.Ordinal),
            "A stored absolute due instant must not be reinterpreted in a different local time zone.");
        Assert.IsTrue(endBoundaryText.EndsWith("Z", StringComparison.Ordinal));
        var parsedStart = DateTimeOffset.Parse(startBoundaryText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var parsedEnd = DateTimeOffset.Parse(endBoundaryText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.AreEqual(dueAt.UtcDateTime, parsedStart.UtcDateTime);
        Assert.IsTrue(parsedEnd > parsedStart,
            "A one-shot trigger needs a future EndBoundary for StartWhenAvailable to recover a missed launch.");
        Assert.IsTrue(parsedEnd > parsedStart.AddYears(5),
            "The recovery window must remain available for a device that was offline for an extended period.");
        Assert.AreEqual("IgnoreNew", document.Root.Element(ns + "Settings")!
            .Element(ns + "MultipleInstancesPolicy")!.Value);
        Assert.AreEqual("PT2M", document.Root.Element(ns + "Settings")!
            .Element(ns + "ExecutionTimeLimit")!.Value,
            "Only the short-lived launcher is managed by Task Scheduler; Layal runs independently.");
        Assert.IsNull(document.Descendants(ns + "WakeToRun").FirstOrDefault(),
            "Layal must not wake the user's computer for a reminder.");
        Assert.IsNull(document.Descendants(ns + "HighestAvailable").FirstOrDefault(),
            "Reminder dispatch must never request elevation.");
        Assert.IsNull(document.Descendants(ns + "DeleteExpiredTaskAfter").FirstOrDefault(),
            "A task definition must remain registered until Layal explicitly reschedules or deletes it.");
        StringAssert.Contains(xml, "S-1-5-21-1000-2000-3000-1001");
    }

    [TestMethod]
    public void SchedulerStoresAnAbsoluteInstantIndependentOfTheComputerTimeZone()
    {
        var first = new DateTimeOffset(2026, 11, 10, 18, 45, 0, TimeSpan.FromHours(2));
        var sameInstantInAnotherZone = new DateTimeOffset(2026, 11, 10, 17, 45, 0, TimeSpan.FromHours(1));
        Assert.AreEqual(first.UtcDateTime, sameInstantInAnotherZone.UtcDateTime);

        var firstXml = XDocument.Parse(ReminderTaskSchedulerService.BuildTaskXml(
            first, Path.Combine(Path.GetTempPath(), "Layal.exe"), "S-1-5-21-1000"));
        var secondXml = XDocument.Parse(ReminderTaskSchedulerService.BuildTaskXml(
            sameInstantInAnotherZone, Path.Combine(Path.GetTempPath(), "Layal.exe"), "S-1-5-21-1000"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.AreEqual(
            firstXml.Root!.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "StartBoundary")!.Value,
            secondXml.Root!.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "StartBoundary")!.Value);
    }

    [TestMethod]
    public void SchedulerChoosesOnlyTheEarliestActiveReminderOrEarlierCheckIn()
    {
        var now = new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.FromHours(2));
        var reminders = new[]
        {
            new Layal.Models.ReminderRecord { Id = "later", DueAt = now.AddHours(4), IsActive = true },
            new Layal.Models.ReminderRecord { Id = "inactive", DueAt = now.AddMinutes(1), IsActive = false },
            new Layal.Models.ReminderRecord { Id = "earliest", DueAt = now.AddHours(2), IsActive = true }
        };

        Assert.AreEqual(
            now.AddHours(2),
            ReminderTaskSchedulerService.GetNextDispatchDueAt(reminders));
        Assert.AreEqual(
            now.AddHours(1),
            ReminderTaskSchedulerService.GetNextDispatchDueAt(reminders, now.AddHours(1)));
        Assert.AreEqual<DateTimeOffset?>(
            null,
            ReminderTaskSchedulerService.GetNextDispatchDueAt(Array.Empty<Layal.Models.ReminderRecord>()));
        Assert.AreEqual<DateTimeOffset?>(
            null,
            ReminderTaskSchedulerService.GetNextDispatchDueAt(
                reminders.Select(reminder => new Layal.Models.ReminderRecord
                {
                    Id = reminder.Id,
                    DueAt = reminder.DueAt,
                    IsActive = false
                })));
    }

    [TestMethod]
    public void TaskNameIsStableAndDoesNotIncludeReminderTextOrUserData()
    {
        Assert.AreEqual(@"\LayalReminderDispatcher", ReminderTaskSchedulerService.TaskName);
        Assert.IsFalse(ReminderTaskSchedulerService.TaskName.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ReminderTaskSchedulerService.TaskName.Contains("title", StringComparison.OrdinalIgnoreCase));
    }
}
