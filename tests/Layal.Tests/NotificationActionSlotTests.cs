using System;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class NotificationActionSlotTests
{
    [TestMethod]
    public void ActionCanBeTakenOnlyOnceWhileNotificationIsValid()
    {
        var slot = new NotificationActionSlot();
        var now = DateTimeOffset.UtcNow;
        var invocationCount = 0;
        slot.Arm(() => invocationCount++, now, TimeSpan.FromSeconds(7));

        var action = slot.Take(now.AddSeconds(2));
        Assert.IsNotNull(action);
        action!();

        Assert.AreEqual(1, invocationCount);
        Assert.IsNull(slot.Take(now.AddSeconds(3)));
        Assert.AreEqual(1, invocationCount);
    }

    [TestMethod]
    public void ExpiredNotificationDoesNotInvokeItsAction()
    {
        var slot = new NotificationActionSlot();
        var now = DateTimeOffset.UtcNow;
        var invoked = false;
        slot.Arm(() => invoked = true, now, TimeSpan.FromSeconds(7));

        Assert.IsNull(slot.Take(now.AddSeconds(8)));
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void ActionExpiresAtTheExactLifetimeBoundary()
    {
        var slot = new NotificationActionSlot();
        var now = DateTimeOffset.UtcNow;
        var invoked = false;
        slot.Arm(() => invoked = true, now, TimeSpan.FromSeconds(7));

        Assert.IsNull(slot.Take(now.AddSeconds(7)));
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void ClearDiscardsTheActiveNotificationAction()
    {
        var slot = new NotificationActionSlot();
        var invoked = false;
        slot.Arm(() => invoked = true, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(7));

        slot.Clear();

        Assert.IsNull(slot.Take(DateTimeOffset.UtcNow));
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void NewNotificationWithoutActionClearsPreviousCallback()
    {
        var slot = new NotificationActionSlot();
        var now = DateTimeOffset.UtcNow;
        var invoked = false;
        slot.Arm(() => invoked = true, now, TimeSpan.FromSeconds(7));
        slot.Arm(null, now.AddSeconds(1), TimeSpan.FromSeconds(7));

        Assert.IsNull(slot.Take(now.AddSeconds(2)));
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void NonPositiveLifetimeIsRejected()
    {
        var slot = new NotificationActionSlot();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            slot.Arm(() => { }, DateTimeOffset.UtcNow, TimeSpan.Zero));
    }
}
