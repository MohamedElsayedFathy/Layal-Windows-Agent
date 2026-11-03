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
        var executable = Path.Combine(Path.GetTempPath(), "Layal Program Files", "Layal.exe");
        var dueAt = new DateTimeOffset(2026, 11, 10, 18, 45, 0, TimeSpan.FromHours(2));
        var xml = ReminderTaskSchedulerService.BuildTaskXml(dueAt, executable, "S-1-5-21-1000-2000-3000-1001");
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.AreEqual("1.4", document.Root!.Attribute("version")!.Value);
        Assert.AreEqual(executable, document.Root.Element(ns + "Actions")!
            .Element(ns + "Exec")!.Element(ns + "Command")!.Value);
        Assert.AreEqual(Path.GetDirectoryName(executable), document.Root.Element(ns + "Actions")!
            .Element(ns + "Exec")!.Element(ns + "WorkingDirectory")!.Value);
        Assert.AreEqual("InteractiveToken", document.Root.Element(ns + "Principals")!
            .Element(ns + "Principal")!.Element(ns + "LogonType")!.Value);
        Assert.AreEqual("LeastPrivilege", document.Root.Element(ns + "Principals")!
            .Element(ns + "Principal")!.Element(ns + "RunLevel")!.Value);
        Assert.AreEqual("true", document.Root.Element(ns + "Settings")!
            .Element(ns + "StartWhenAvailable")!.Value);
        Assert.AreEqual("IgnoreNew", document.Root.Element(ns + "Settings")!
            .Element(ns + "MultipleInstancesPolicy")!.Value);
        Assert.AreEqual("PT0S", document.Root.Element(ns + "Settings")!
            .Element(ns + "ExecutionTimeLimit")!.Value,
            "The scheduled application must not be terminated while running in the tray.");
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
