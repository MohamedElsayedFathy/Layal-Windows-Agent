using System;
using System.IO;
using System.Linq;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class TaskHistoryArtifactTests
{
    [TestMethod]
    public void SelectedReminderTaskIsIncludedWhenOlderThanTheRecentDisplayWindow()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var history = new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi"));
        var olderTaskId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;
        var records = new[]
        {
            new AgentTaskRecord
            {
                Id = olderTaskId,
                Goal = "The task explicitly linked by the reminder",
                Status = "Completed",
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2)
            }
        }.Concat(Enumerable.Range(0, 35).Select(index => new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Goal = $"Newer task {index}",
            Status = "Completed",
            CreatedAt = now.AddMinutes(index),
            UpdatedAt = now.AddMinutes(index)
        })).ToArray();

        history.ImportManyWithIds(records);

        Assert.IsFalse(history.GetRecent(30).Any(item => item.Id == olderTaskId));
        var withLinkedTask = history.GetRecentWithSelectedTask(olderTaskId, 30);
        Assert.AreEqual(31, withLinkedTask.Count);
        Assert.AreEqual(1, withLinkedTask.Count(item => item.Id == olderTaskId));
        Assert.AreEqual(olderTaskId, withLinkedTask[^1].Id);
        Assert.AreEqual("The task explicitly linked by the reminder", withLinkedTask[^1].Goal);
    }

    [TestMethod]
    public void MissingOrPrunedLinkedTaskDoesNotSelectAReplacementTask()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var history = new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi"));
        var removedTaskId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;
        var records = Enumerable.Range(0, 100).Select(index => new AgentTaskRecord
        {
            Id = index == 0 ? removedTaskId : Guid.NewGuid().ToString("N"),
            Goal = $"Retained task {index}",
            Status = "Completed",
            CreatedAt = index == 0 ? now.AddDays(-2) : now.AddMinutes(index),
            UpdatedAt = index == 0 ? now.AddDays(-2) : now.AddMinutes(index)
        }).ToArray();

        history.ImportManyWithIds(records);
        history.Upsert(new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Goal = "Newest task pushes the oldest record out of the 100-task retention window",
            Status = "Completed"
        });

        var withMissingLink = history.GetRecentWithSelectedTask(removedTaskId, 30);
        Assert.AreEqual(30, withMissingLink.Count);
        Assert.IsFalse(withMissingLink.Any(item => item.Id == removedTaskId));
    }

    [TestMethod]
    public void ArtifactPathNormalizationAcceptsRelativePathsAndUsesForwardSlashes()
    {
        Assert.AreEqual("reports/final.txt",
            TaskHistoryService.NormalizeArtifactPath(@"reports\final.txt"));
        Assert.AreEqual("result.json",
            TaskHistoryService.NormalizeArtifactPath("result.json"));
    }

    [TestMethod]
    public void ArtifactPathNormalizationRejectsAbsoluteTraversalAndInvalidPaths()
    {
        foreach (var path in new[]
        {
            "", "  ", "../outside.txt", "folder/../outside.txt", "./file.txt",
            "/rooted.txt", @"C:\outside.txt", @"\\server\share\file.txt",
            "folder//file.txt", "folder/./file.txt", "folder\\..\\file.txt",
            "bad\nname.txt"
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => TaskHistoryService.NormalizeArtifactPath(path), path);
        }
    }

    [TestMethod]
    public void ArtifactReferencesAreDeduplicatedAndPersistedWithTheirTask()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "tasks.dpapi");
        var history = new TaskHistoryService(path);
        var task = new AgentTaskRecord { Id = "task-1", Goal = "Create a report", Status = "Running" };
        history.Upsert(task);

        Assert.IsTrue(history.AttachArtifact(task, @"reports\summary.txt"));
        Assert.IsFalse(history.AttachArtifact(task, "reports/summary.txt"));

        var reloaded = new TaskHistoryService(path).GetRecent(10).Single();
        CollectionAssert.AreEqual(new[] { "reports/summary.txt" }, reloaded.Artifacts);
        CollectionAssert.AreEqual(new[] { "reports/summary.txt" }, task.Artifacts);
    }

    [TestMethod]
    public void RemovingArtifactUnlinksOnlyTheSelectedPathAndPersistsAnEvent()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "tasks.dpapi");
        var history = new TaskHistoryService(path);
        var task = new AgentTaskRecord
        {
            Id = "task-remove",
            Goal = "Create report",
            Status = "Completed",
            Artifacts = new() { "reports/first.txt", "reports/second.txt" }
        };
        history.Upsert(task);

        Assert.IsTrue(history.RemoveArtifact(task.Id, "reports/first.txt"));
        Assert.IsFalse(history.RemoveArtifact(task.Id, "reports/missing.txt"));

        var reloaded = new TaskHistoryService(path).GetRecent(10).Single();
        CollectionAssert.AreEqual(new[] { "reports/second.txt" }, reloaded.Artifacts);
        Assert.IsTrue(reloaded.Events.Any(item =>
            item.Contains("reports/first.txt", StringComparison.Ordinal) &&
            item.Contains("Recycle Bin", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ArtifactLimitIsEnforced()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var history = new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi"));
        var task = new AgentTaskRecord { Id = "task-limit", Goal = "Create many outputs" };
        history.Upsert(task);
        for (var index = 0; index < TaskHistoryService.MaximumArtifactsPerTask; index++)
            Assert.IsTrue(history.AttachArtifact(task, $"reports/file-{index}.txt"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            history.AttachArtifact(task, "reports/overflow.txt"));
    }
}
