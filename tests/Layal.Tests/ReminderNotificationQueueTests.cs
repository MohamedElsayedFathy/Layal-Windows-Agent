using System;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ReminderNotificationQueueTests
{
    [TestMethod]
    public void QueuedNotificationsKeepTheirOwnClickActionsInDisplayOrder()
    {
        var queue = new ReminderNotificationQueue();
        var firstClicks = 0;
        var secondClicks = 0;
        var first = new ReminderNotification("First reminder", true, () => firstClicks++);
        var second = new ReminderNotification("Second reminder", false, () => secondClicks++);

        Assert.IsTrue(queue.Enqueue(first));
        Assert.IsTrue(queue.TryBeginNext(out var active));
        Assert.AreSame(first, active);
        Assert.IsFalse(queue.TryBeginNext(out _),
            "A second notification must not replace the action for a balloon that is still visible.");

        Assert.IsTrue(queue.Enqueue(second));
        active!.OnClick?.Invoke();
        Assert.AreEqual(1, firstClicks);
        Assert.AreEqual(0, secondClicks);

        Assert.IsTrue(queue.CompleteActive());
        Assert.IsTrue(queue.TryBeginNext(out active));
        Assert.AreSame(second, active);
        active!.OnClick?.Invoke();
        Assert.AreEqual(1, firstClicks);
        Assert.AreEqual(1, secondClicks);
    }

    [TestMethod]
    public void QueueRequiresAnActiveNotificationToBeCompletedBeforeAdvancing()
    {
        var queue = new ReminderNotificationQueue();
        Assert.IsFalse(queue.CompleteActive());
        Assert.IsFalse(queue.TryBeginNext(out _));
        Assert.IsTrue(queue.Enqueue(new ReminderNotification("Reminder", true, null)));
        Assert.IsTrue(queue.TryBeginNext(out _));
        Assert.IsTrue(queue.CompleteActive());
        Assert.IsFalse(queue.CompleteActive());
    }

    [TestMethod]
    public void QueueIsBoundedAndCanBeClearedOnTrayShutdown()
    {
        var queue = new ReminderNotificationQueue();
        for (var index = 0; index < ReminderNotificationQueue.MaximumPendingNotifications; index++)
        {
            Assert.IsTrue(queue.Enqueue(new ReminderNotification($"Reminder {index}", true, null)));
        }

        Assert.IsFalse(queue.Enqueue(new ReminderNotification("Overflow", true, null)));
        queue.Clear();
        Assert.IsFalse(queue.TryBeginNext(out _));
        Assert.IsFalse(queue.CompleteActive());
    }
}
