using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class WorkspaceSnapshotServiceTests
{
    [TestMethod]
    public void ExportIncludesTasksArchivedMemoriesAndFileMetadataButNeverFileContents()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "report.txt"), "PRIVATE_FILE_CONTENT_SENTINEL");

        var tasks = new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi"));
        var memories = new MemoryService(Path.Combine(temp.Path, "memories.dpapi"));
        var task = new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Goal = "Create release report",
            Status = "Completed",
            Result = "Report created",
            Events = new() { "Created report.txt" },
            Artifacts = new() { "report.txt" }
        };
        tasks.Upsert(task);

        var memory = memories.Save("Release notes", "Review the release checklist.", category: "Project");
        Assert.IsTrue(memories.SetArchived(memory.Id, true));

        var service = new WorkspaceSnapshotService(tasks, memories, workspace);
        var json = service.ExportJson();
        var preview = service.PreviewImportJson(json);

        StringAssert.Contains(json, "Create release report");
        StringAssert.Contains(json, "Release notes");
        StringAssert.Contains(json, "Project");
        StringAssert.Contains(json, "report.txt");
        Assert.IsFalse(json.Contains("PRIVATE_FILE_CONTENT_SENTINEL", StringComparison.Ordinal));
        Assert.AreEqual(1, preview.TaskCount);
        Assert.AreEqual(1, preview.MemoryCount);
        Assert.IsTrue(preview.FileManifestCount >= 1);
    }

    [TestMethod]
    public void ImportMergesTasksAndArchivedMemoriesAndIsIdempotent()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var sourceWorkspace = Path.Combine(temp.Path, "source");
        var targetWorkspace = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(sourceWorkspace);
        Directory.CreateDirectory(targetWorkspace);

        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        var sourceMemories = new MemoryService(Path.Combine(temp.Path, "source-memories.dpapi"));
        var task = new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Goal = "Review the source",
            Status = "Completed",
            Result = "Review finished",
            Events = new() { "Step one", "Step two" },
            Artifacts = new() { "reports/review.txt" }
        };
        sourceTasks.Upsert(task);
        var note = sourceMemories.Save("Source memory", "A project detail.", category: "Engineering");
        Assert.IsTrue(sourceMemories.SetArchived(note.Id, true));

        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemories, sourceWorkspace).ExportJson();
        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemories = new MemoryService(Path.Combine(temp.Path, "target-memories.dpapi"));
        var targetService = new WorkspaceSnapshotService(targetTasks, targetMemories, targetWorkspace);

        var result = targetService.ImportJson(backup);

        Assert.AreEqual(1, result.TasksImported);
        Assert.AreEqual(1, result.MemoriesImported);
        Assert.AreEqual(0, targetService.ImportJson(backup).TasksImported);
        Assert.AreEqual(0, targetService.ImportJson(backup).MemoriesImported);

        var importedTask = targetTasks.GetRecent(10).Single();
        Assert.AreEqual(task.Id, importedTask.Id);
        CollectionAssert.AreEqual(new[] { "reports/review.txt" }, importedTask.Artifacts);
        var importedMemory = targetMemories.GetAll(includeArchived: true).Single();
        Assert.AreEqual(note.Id, importedMemory.Id);
        Assert.AreEqual("Engineering", importedMemory.Category);
        Assert.IsTrue(importedMemory.IsArchived);
        Assert.AreEqual(0, targetMemories.GetAll().Count);
        Assert.AreEqual(0, Directory.EnumerateFiles(targetWorkspace, "*", SearchOption.AllDirectories).Count(),
            "A backup manifest must never copy or restore workspace files.");
    }

    [TestMethod]
    public void ImportDoesNotOverwriteExistingRecordsWithMatchingIds()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);

        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        var sourceMemories = new MemoryService(Path.Combine(temp.Path, "source-memories.dpapi"));
        var taskId = Guid.NewGuid().ToString("N");
        var noteId = Guid.NewGuid().ToString("N");
        sourceTasks.Upsert(new AgentTaskRecord
        {
            Id = taskId, Goal = "Backup goal", Status = "Completed", Result = "Backup result"
        });
        _ = sourceMemories.Save("Backup title", "Backup content.", noteId, "Backup");

        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemories, workspace).ExportJson();
        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemories = new MemoryService(Path.Combine(temp.Path, "target-memories.dpapi"));
        targetTasks.Upsert(new AgentTaskRecord
        {
            Id = taskId, Goal = "Local goal", Status = "Running", Result = "Keep local result"
        });
        _ = targetMemories.Save("Local title", "Keep local content.", noteId, "Local");

        var result = new WorkspaceSnapshotService(targetTasks, targetMemories, workspace).ImportJson(backup);

        Assert.AreEqual(0, result.TasksImported);
        Assert.AreEqual(0, result.MemoriesImported);
        var retainedTask = targetTasks.GetRecent(10).Single();
        Assert.AreEqual("Local goal", retainedTask.Goal);
        Assert.AreEqual("Keep local result", retainedTask.Result);
        var retainedMemory = targetMemories.GetAll(includeArchived: true).Single();
        Assert.AreEqual("Local title", retainedMemory.Title);
        Assert.AreEqual("Keep local content.", retainedMemory.Content);
        Assert.AreEqual("Local", retainedMemory.Category);
    }

    [TestMethod]
    public void InvalidManifestPathIsRejectedBeforeAnyRecordIsImported()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "manifest.txt"), "manifest only");
        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        sourceTasks.Upsert(new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"), Goal = "Must not be imported", Status = "Completed"
        });
        var sourceMemory = new MemoryService(Path.Combine(temp.Path, "source-memory.dpapi"));
        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemory, workspace).ExportJson();
        var node = JsonNode.Parse(backup) ?? throw new AssertFailedException("Backup JSON was empty.");
        node["Files"]![0]!["RelativePath"] = "../outside.txt";

        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemory = new MemoryService(Path.Combine(temp.Path, "target-memory.dpapi"));
        var service = new WorkspaceSnapshotService(targetTasks, targetMemory, workspace);

        Assert.ThrowsExactly<ArgumentException>(() => service.ImportJson(node.ToJsonString()));
        Assert.AreEqual(0, targetTasks.GetRecent(10).Count);
        Assert.AreEqual(0, targetMemory.GetAll(includeArchived: true).Count);
    }

    [TestMethod]
    public void CredentialLikeImportedMemoryRejectsWholeBackupBeforeTaskMerge()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        sourceTasks.Upsert(new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"), Goal = "Backup task", Status = "Completed"
        });
        var sourceMemory = new MemoryService(Path.Combine(temp.Path, "source-memory.dpapi"));
        _ = sourceMemory.Save("Ordinary note", "No secret content.");
        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemory, workspace).ExportJson();
        var node = JsonNode.Parse(backup) ?? throw new AssertFailedException("Backup JSON was empty.");
        node["Memories"]![0]!["Content"] = "password=hunter2";

        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemory = new MemoryService(Path.Combine(temp.Path, "target-memory.dpapi"));
        var service = new WorkspaceSnapshotService(targetTasks, targetMemory, workspace);

        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportJson(node.ToJsonString()));
        Assert.AreEqual(0, targetTasks.GetRecent(10).Count,
            "All collections are preflighted before either protected store is changed.");
        Assert.AreEqual(0, targetMemory.GetAll(includeArchived: true).Count);
    }

    [TestMethod]
    public void UnsupportedVersionAndOversizedBackupAreRejected()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var service = new WorkspaceSnapshotService(
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.json")),
            new MemoryService(Path.Combine(temp.Path, "memory.json")),
            workspace);

        Assert.ThrowsExactly<InvalidOperationException>(() => service.PreviewImportJson(
            "{\"SchemaVersion\":99,\"Tasks\":[],\"Memories\":[],\"Files\":[]}"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.PreviewImportJson(new string('x', WorkspaceSnapshotService.MaximumJsonBytes + 1)));
    }
}
