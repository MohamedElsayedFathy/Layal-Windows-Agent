using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ReminderServiceTests
{
    [TestMethod]
    public void RemindersPersistAcrossServiceInstancesAndCanBeDeleted()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "reminders.dpapi");
        var service = new ReminderService(path);
        var dueAt = DateTimeOffset.Now.AddMinutes(5);
        var created = service.Create("Pay the bill", dueAt, "Weekly");

        var reloaded = new ReminderService(path);
        var restored = reloaded.GetAll();

        Assert.AreEqual(1, restored.Count);
        Assert.AreEqual(created.Id, restored[0].Id);
        Assert.AreEqual("Pay the bill", restored[0].Title);
        Assert.AreEqual("Weekly", restored[0].Recurrence);
        Assert.AreEqual(dueAt, restored[0].DueAt);

        Assert.IsTrue(reloaded.Delete(created.Id));
        Assert.AreEqual(0, service.GetAll().Count);
    }

    [TestMethod]
    public void OneTimeReminderIsDeliveredOnceAndThenDeactivated()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        var dueAt = DateTimeOffset.Now.AddMinutes(1);
        var created = service.Create("One-time reminder", dueAt);
        var deliveredAt = dueAt.AddSeconds(1);

        var due = service.GetDue(deliveredAt);
        Assert.AreEqual(1, due.Count);
        var delivered = service.MarkDelivered(created.Id, dueAt, deliveredAt);

        Assert.IsNotNull(delivered);
        Assert.AreEqual("One-time reminder", delivered!.Title);
        Assert.AreEqual(0, service.GetDue(deliveredAt.AddMinutes(1)).Count);
        Assert.IsFalse(service.GetAll()[0].IsActive);
        Assert.IsNull(service.MarkDelivered(created.Id, dueAt, deliveredAt.AddMinutes(1)));
    }

    [TestMethod]
    public void RecurringReminderAdvancesAndRejectsTheOldDueInstant()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        var dueAt = DateTimeOffset.Now.AddMinutes(1);
        var created = service.Create("Daily reminder", dueAt, "Daily");
        var deliveredAt = dueAt.AddMinutes(2);

        var delivered = service.MarkDelivered(created.Id, dueAt, deliveredAt);
        var current = service.GetAll()[0];

        Assert.IsNotNull(delivered);
        Assert.IsTrue(current.IsActive);
        Assert.IsTrue(current.DueAt > deliveredAt, "The next daily occurrence should be after the delivered instant.");
        Assert.AreEqual("Daily", current.Recurrence);
        Assert.IsNull(service.MarkDelivered(created.Id, dueAt, deliveredAt.AddMinutes(1)),
            "A stale timer tick must not deliver the occurrence twice.");
    }

    [TestMethod]
    public void UpdatingDeliveredReminderChangesScheduleAndReactivatesIt()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        var originalDue = DateTimeOffset.Now.AddMinutes(1);
        var created = service.Create("Old title", originalDue);
        Assert.IsNotNull(service.MarkDelivered(created.Id, originalDue, originalDue.AddSeconds(1)));
        Assert.IsFalse(service.GetAll()[0].IsActive);

        var nextDue = DateTimeOffset.Now.AddHours(2);
        var updated = service.Update(created.Id, "Updated title", nextDue, "Weekly");

        Assert.IsNotNull(updated);
        Assert.AreEqual("Updated title", updated!.Title);
        Assert.AreEqual(nextDue, updated.DueAt);
        Assert.AreEqual("Weekly", updated.Recurrence);
        Assert.IsTrue(updated.IsActive);
        Assert.IsNull(updated.LastDeliveredAt);

        var persisted = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi")).GetAll()[0];
        Assert.AreEqual("Updated title", persisted.Title);
        Assert.IsTrue(persisted.IsActive);
    }

    [TestMethod]
    public void ReminderUpdateRejectsPastTimesAndUnknownIds()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        var created = service.Create("Update me", DateTimeOffset.Now.AddHours(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            service.Update(created.Id, "Update me", DateTimeOffset.Now.AddMinutes(-2)));
        Assert.IsNull(service.Update("missing", "Update me", DateTimeOffset.Now.AddHours(1)));
    }

    [TestMethod]
    public void ReminderCreationValidatesTitlesAndFutureDueTimes()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.Create("   ", DateTimeOffset.Now.AddMinutes(5)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.Create(new string('x', ReminderService.MaximumTitleCharacters + 1), DateTimeOffset.Now.AddMinutes(5)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            service.Create("Past reminder", DateTimeOffset.Now.AddMinutes(-1)));
    }

    [TestMethod]
    public void UnknownRecurrenceFallsBackToOneTimeInsteadOfRunningUnexpectedSchedules()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        var created = service.Create("Safe fallback", DateTimeOffset.Now.AddMinutes(5), "Run arbitrary code");
        Assert.AreEqual("None", created.Recurrence);
    }
}
