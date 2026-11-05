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
    public void DailyReminderMovesForwardAcrossSpringDaylightSavingGap()
    {
        var zone = CreateDaylightSavingTestZone();
        var previousDue = new DateTimeOffset(2026, 3, 7, 2, 30, 0, TimeSpan.FromHours(-5));
        var deliveredAt = new DateTimeOffset(2026, 3, 7, 2, 35, 0, TimeSpan.FromHours(-5));

        var next = ReminderService.GetNextRecurringDueTime(previousDue, deliveredAt, daily: true, zone);
        var local = TimeZoneInfo.ConvertTime(next, zone);

        Assert.AreEqual(new DateTime(2026, 3, 8, 3, 30, 0), local.DateTime);
        Assert.AreEqual(TimeSpan.FromHours(-4), local.Offset);
        Assert.IsTrue(next > deliveredAt);
    }

    [TestMethod]
    public void DailyReminderChoosesSecondFallBackOccurrenceWhenFirstHasPassed()
    {
        var zone = CreateDaylightSavingTestZone();
        var previousDue = new DateTimeOffset(2026, 10, 31, 1, 30, 0, TimeSpan.FromHours(-4));
        var deliveredAt = new DateTimeOffset(2026, 11, 1, 1, 40, 0, TimeSpan.FromHours(-4));

        var next = ReminderService.GetNextRecurringDueTime(previousDue, deliveredAt, daily: true, zone);
        var local = TimeZoneInfo.ConvertTime(next, zone);

        Assert.AreEqual(new DateTime(2026, 11, 1, 1, 30, 0), local.DateTime);
        Assert.AreEqual(TimeSpan.FromHours(-5), local.Offset);
        Assert.IsTrue(next > deliveredAt);
    }

    [TestMethod]
    public void WeeklyReminderPreservesLocalTimeAcrossTimeZoneOffsetChange()
    {
        var zone = CreateDaylightSavingTestZone();
        var previousDue = new DateTimeOffset(2026, 10, 25, 9, 15, 0, TimeSpan.FromHours(-4));
        var deliveredAt = new DateTimeOffset(2026, 10, 25, 9, 20, 0, TimeSpan.FromHours(-4));

        var next = ReminderService.GetNextRecurringDueTime(previousDue, deliveredAt, daily: false, zone);
        var local = TimeZoneInfo.ConvertTime(next, zone);

        Assert.AreEqual(new DateTime(2026, 11, 1, 9, 15, 0), local.DateTime);
        Assert.AreEqual(TimeSpan.FromHours(-5), local.Offset);
    }

    [TestMethod]
    public void LocalReminderCreationRejectsNonexistentSpringForwardWallClockTime()
    {
        var zone = CreateDaylightSavingTestZone();
        var nonexistent = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ReminderService.CreateLocalDueAt(nonexistent, zone));
    }

    [TestMethod]
    public void LocalReminderCreationSelectsFirstOccurrenceForFallBackOverlap()
    {
        var zone = CreateDaylightSavingTestZone();
        var repeated = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);

        var dueAt = ReminderService.CreateLocalDueAt(repeated, zone);
        var local = TimeZoneInfo.ConvertTime(dueAt, zone);

        Assert.AreEqual(new DateTime(2026, 11, 1, 1, 30, 0), local.DateTime);
        Assert.AreEqual(TimeSpan.FromHours(-4), local.Offset,
            "The first occurrence uses the daylight offset and resolves to the earlier UTC instant.");
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), dueAt);
    }

    [TestMethod]
    public void LocalReminderCreationUsesTheSelectedTimeZonesOffsetForOrdinaryTimes()
    {
        var zone = CreateDaylightSavingTestZone();
        var ordinary = new DateTime(2026, 3, 9, 9, 15, 0, DateTimeKind.Unspecified);

        var dueAt = ReminderService.CreateLocalDueAt(ordinary, zone);
        var local = TimeZoneInfo.ConvertTime(dueAt, zone);

        Assert.AreEqual(new DateTime(2026, 3, 9, 9, 15, 0), local.DateTime);
        Assert.AreEqual(TimeSpan.FromHours(-4), local.Offset);
    }

    private static TimeZoneInfo CreateDaylightSavingTestZone()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Layal-Test-Daylight",
            TimeSpan.FromHours(-5),
            "Layal test daylight zone",
            "Layal standard time",
            "Layal daylight time",
            new[] { rule });
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
    public void ReminderCanBeLinkedToATaskAndKeepsTheLinkAcrossDeliveryAndReload()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "reminders.dpapi");
        var service = new ReminderService(path);
        var dueAt = DateTimeOffset.Now.AddMinutes(2);
        var created = service.Create("Review deployment", dueAt, relatedTaskId: "task-123");
        Assert.AreEqual("task-123", created.RelatedTaskId);

        var delivered = service.MarkDelivered(created.Id, dueAt, dueAt.AddSeconds(1));
        Assert.IsNotNull(delivered);
        Assert.AreEqual("task-123", delivered!.RelatedTaskId);
        Assert.AreEqual("task-123", new ReminderService(path).GetAll().Single().RelatedTaskId);
    }

    [TestMethod]
    public void UpdatingReminderCanChangeOrClearItsLinkedTask()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        var created = service.Create("Review", DateTimeOffset.Now.AddHours(1), relatedTaskId: "task-before");
        var updated = service.Update(created.Id, "Review", DateTimeOffset.Now.AddHours(2), relatedTaskId: "task-after");
        Assert.AreEqual("task-after", updated!.RelatedTaskId);
        var cleared = service.Update(created.Id, "Review", DateTimeOffset.Now.AddHours(3));
        Assert.IsNull(cleared!.RelatedTaskId);
    }

    [TestMethod]
    public void ReminderRejectsUnboundedOrControlCharacterTaskIds()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The reminder store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new ReminderService(Path.Combine(temp.Path, "reminders.dpapi"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.Create("Review", DateTimeOffset.Now.AddHours(1), relatedTaskId: new string('x', 65)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.Create("Review", DateTimeOffset.Now.AddHours(1), relatedTaskId: "task\nunsafe"));
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
