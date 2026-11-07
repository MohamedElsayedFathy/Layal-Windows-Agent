using System;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ProactiveCheckInPolicyTests
{
    private static readonly DateTimeOffset Last = new(2026, 11, 3, 8, 0, 0, TimeSpan.FromHours(2));

    [TestMethod]
    public void NextDueUsesTheSelectedElapsedInterval()
    {
        foreach (var interval in new[] { 15, 30, 60, 120 })
        {
            Assert.AreEqual(
                Last.AddMinutes(interval),
                ProactiveCheckInPolicy.GetNextDueAt(
                    enabled: true,
                    privacyShieldEnabled: false,
                    gameModeActive: false,
                    applicationExiting: false,
                    lastNotifiedAt: Last,
                    intervalMinutes: interval));
        }
    }

    [TestMethod]
    public void NextDueFailsClosedWhenAnySuppressionGateIsActive()
    {
        Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(false, false, false, false, Last, 60));
        Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(true, true, false, false, Last, 60));
        Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(true, false, true, false, Last, 60));
        Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(true, false, false, true, Last, 60));
        Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(true, false, false, false, null, 60));
        Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(
            true, false, false, false, Last, 60, privateChatActive: true));
    }

    [TestMethod]
    public void NextDueRejectsUnsupportedIntervalsAndUnrepresentableTimestamps()
    {
        foreach (var interval in new[] { 0, 1, 14, 16, 180, 1440 })
            Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(true, false, false, false, Last, interval));

        Assert.IsNull(ProactiveCheckInPolicy.GetNextDueAt(
            true, false, false, false, DateTimeOffset.MaxValue, 15));
    }

    [TestMethod]
    public void CheckInBecomesDueAtTheSelectedElapsedIntervalAndAfterSleep()
    {
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(true, false, false, false, Last, Last.AddMinutes(59), 60));
        Assert.IsTrue(ProactiveCheckInPolicy.ShouldNotify(true, false, false, false, Last, Last.AddMinutes(60), 60));
        Assert.IsTrue(ProactiveCheckInPolicy.ShouldNotify(true, false, false, false, Last, Last.AddHours(5), 60),
            "A delayed timer after system sleep should produce one due notification.");
    }

    [TestMethod]
    public void PrivacyShieldGameModeExitAndPrivateChatSuppressCheckIns()
    {
        var now = Last.AddHours(2);
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(false, false, false, false, Last, now, 60));
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(true, true, false, false, Last, now, 60));
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(true, false, true, false, Last, now, 60));
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(true, false, false, true, Last, now, 60));
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(
            true, false, false, false, Last, now, 60, privateChatActive: true));
    }

    [TestMethod]
    public void MissingOrFutureLastNotificationAndUnsupportedIntervalsFailClosed()
    {
        var now = Last.AddHours(3);
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(true, false, false, false, null, now, 60));
        Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(true, false, false, false, now, Last, 60));
        foreach (var interval in new[] { 0, 1, 14, 16, 180, 1440 })
            Assert.IsFalse(ProactiveCheckInPolicy.ShouldNotify(true, false, false, false, Last, now, interval));
    }
}
