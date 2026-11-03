using System;
using System.IO;
using System.Linq;
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
    public void TaskNameIsStableAndDoesNotIncludeReminderTextOrUserData()
    {
        Assert.AreEqual(@"\LayalReminderDispatcher", ReminderTaskSchedulerService.TaskName);
        Assert.IsFalse(ReminderTaskSchedulerService.TaskName.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ReminderTaskSchedulerService.TaskName.Contains("title", StringComparison.OrdinalIgnoreCase));
    }
}
