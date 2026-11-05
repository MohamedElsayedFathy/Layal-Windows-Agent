using System;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ReminderTaskLinkPolicyTests
{
    [TestMethod]
    public void DigestResolvesOneTaskWhenAllLinkedRemindersShareTheSameId()
    {
        var reminders = new[]
        {
            new ReminderRecord { RelatedTaskId = "task-exact" },
            new ReminderRecord { RelatedTaskId = null },
            new ReminderRecord { RelatedTaskId = "task-exact" }
        };

        Assert.AreEqual("task-exact", ReminderTaskLinkPolicy.GetUniqueLinkedTaskId(reminders));
    }

    [TestMethod]
    public void DigestDoesNotChooseArbitrarilyWhenDifferentTasksAreLinked()
    {
        var reminders = new[]
        {
            new ReminderRecord { RelatedTaskId = "task-one" },
            new ReminderRecord { RelatedTaskId = "task-two" }
        };

        Assert.IsNull(ReminderTaskLinkPolicy.GetUniqueLinkedTaskId(reminders));
    }

    [TestMethod]
    public void UnlinkedDigestDoesNotCreateATaskTarget()
    {
        var reminders = new[]
        {
            new ReminderRecord(),
            new ReminderRecord { RelatedTaskId = " " }
        };

        Assert.IsNull(ReminderTaskLinkPolicy.GetUniqueLinkedTaskId(reminders));
    }

    [TestMethod]
    public void DigestTreatsTaskIdsAsExactOrdinalIdentifiers()
    {
        var reminders = new[]
        {
            new ReminderRecord { RelatedTaskId = "Task-ABC" },
            new ReminderRecord { RelatedTaskId = "task-abc" }
        };

        Assert.IsNull(ReminderTaskLinkPolicy.GetUniqueLinkedTaskId(reminders));
    }
}
