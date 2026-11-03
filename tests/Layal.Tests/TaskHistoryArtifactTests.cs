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
