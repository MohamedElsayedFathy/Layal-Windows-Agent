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
        var filePreview = preview.FileEntries!.Single();
        Assert.AreEqual("report.txt", filePreview.RelativePath);
        Assert.AreEqual(new FileInfo(Path.Combine(workspace, "report.txt")).Length, filePreview.SizeBytes);
        Assert.IsFalse(filePreview.IsDirectory);
        Assert.IsFalse(filePreview.HasContent);
        Assert.IsNull(filePreview.ContentOmittedReason);
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
        sourceTasks.Upsert(new AgentTaskRecord
        {
            Id = taskId, Goal = "Backup goal", Status = "Completed", Result = "Backup result"
        });
        var sourceNote = sourceMemories.Save("Backup title", "Backup content.", category: "Backup");

        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemories, workspace).ExportJson();
        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemories = new MemoryService(Path.Combine(temp.Path, "target-memories.dpapi"));
        targetTasks.Upsert(new AgentTaskRecord
        {
            Id = taskId, Goal = "Local goal", Status = "Running", Result = "Keep local result"
        });
        _ = targetMemories.ImportMany(new[] { sourceNote });
        _ = targetMemories.Save("Local title", "Keep local content.", sourceNote.Id, "Local");

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
    public void ManifestRejectsWindowsDeviceNamesAlternateStreamsAndProtectedDirectories()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "ordinary.txt"), "safe");
        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        sourceTasks.Upsert(new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"), Goal = "Must not import", Status = "Completed"
        });
        var sourceMemory = new MemoryService(Path.Combine(temp.Path, "source-memory.dpapi"));
        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemory, workspace)
            .ExportJson(includeWorkspaceFileContents: true);

        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemory = new MemoryService(Path.Combine(temp.Path, "target-memory.dpapi"));
        var target = new WorkspaceSnapshotService(targetTasks, targetMemory, workspace);

        foreach (var unsafePath in new[] { "CON.txt", "report.txt:secret", ".git/config", "folder./file.txt", "NUL" })
        {
            var node = JsonNode.Parse(backup) ?? throw new AssertFailedException("Backup JSON was empty.");
            node["Files"]![0]![nameof(WorkspaceFileManifestEntry.RelativePath)] = unsafePath;
            if (unsafePath.Contains(':'))
            {
                Assert.ThrowsExactly<ArgumentException>(
                    () => target.ImportJson(node.ToJsonString(), restoreWorkspaceFiles: true),
                    "Alternate data streams must be rejected before restoration.");
            }
            else
            {
                Assert.ThrowsExactly<InvalidOperationException>(
                    () => target.ImportJson(node.ToJsonString(), restoreWorkspaceFiles: true),
                    "Unsafe path should be rejected: " + unsafePath);
            }
        }

        Assert.AreEqual(0, targetTasks.GetRecent(10).Count,
            "No task record should be imported when any restored file path is unsafe.");
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

    [TestMethod]
    public void EmbeddedWorkspaceFilesRequireExplicitRestoreAndNeverOverwriteExistingFiles()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var sourceWorkspace = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(sourceWorkspace);
        var sourceFile = Path.Combine(sourceWorkspace, "report.txt");
        File.WriteAllText(sourceFile, "RESTORABLE_WORKSPACE_CONTENT");

        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        var sourceMemory = new MemoryService(Path.Combine(temp.Path, "source-memory.dpapi"));
        var backupService = new WorkspaceSnapshotService(sourceTasks, sourceMemory, sourceWorkspace);
        var backup = backupService.ExportJson(includeWorkspaceFileContents: true);
        var preview = backupService.PreviewImportJson(backup);
        Assert.AreEqual(1, preview.FilesWithContent);
        var embeddedPreview = preview.FileEntries!.Single();
        Assert.AreEqual("report.txt", embeddedPreview.RelativePath);
        Assert.AreEqual("RESTORABLE_WORKSPACE_CONTENT".Length, embeddedPreview.SizeBytes);
        Assert.IsTrue(embeddedPreview.HasContent);
        Assert.IsFalse(embeddedPreview.IsDirectory);

        var metadataOnlyTarget = Path.Combine(temp.Path, "metadata-only");
        Directory.CreateDirectory(metadataOnlyTarget);
        var metadataOnly = new WorkspaceSnapshotService(
            new TaskHistoryService(Path.Combine(temp.Path, "metadata-tasks.dpapi")),
            new MemoryService(Path.Combine(temp.Path, "metadata-memory.dpapi")),
            metadataOnlyTarget);
        var metadataResult = metadataOnly.ImportJson(backup);
        Assert.AreEqual(0, metadataResult.FilesRestored);
        Assert.AreEqual(1, metadataResult.FilesSkipped);
        Assert.IsFalse(File.Exists(Path.Combine(metadataOnlyTarget, "report.txt")),
            "The default import must never restore file contents.");

        var restoreTarget = Path.Combine(temp.Path, "restore");
        Directory.CreateDirectory(restoreTarget);
        var restoreService = new WorkspaceSnapshotService(
            new TaskHistoryService(Path.Combine(temp.Path, "restore-tasks.dpapi")),
            new MemoryService(Path.Combine(temp.Path, "restore-memory.dpapi")),
            restoreTarget);
        var restoreResult = restoreService.ImportJson(backup, restoreWorkspaceFiles: true);
        Assert.AreEqual(1, restoreResult.FilesRestored);
        Assert.AreEqual("RESTORABLE_WORKSPACE_CONTENT", File.ReadAllText(Path.Combine(restoreTarget, "report.txt")));

        var existingTarget = Path.Combine(temp.Path, "existing");
        Directory.CreateDirectory(existingTarget);
        File.WriteAllText(Path.Combine(existingTarget, "report.txt"), "KEEP_LOCAL_FILE");
        var existingService = new WorkspaceSnapshotService(
            new TaskHistoryService(Path.Combine(temp.Path, "existing-tasks.dpapi")),
            new MemoryService(Path.Combine(temp.Path, "existing-memory.dpapi")),
            existingTarget);
        var existingResult = existingService.ImportJson(backup, restoreWorkspaceFiles: true);
        Assert.AreEqual(0, existingResult.FilesRestored);
        Assert.AreEqual(1, existingResult.FilesSkipped);
        Assert.AreEqual("KEEP_LOCAL_FILE", File.ReadAllText(Path.Combine(existingTarget, "report.txt")));
    }

    [TestMethod]
    public void ImportFailureInSecondProtectedStoreRollsBackFirstStoreAndRestoredFiles()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var sourceWorkspace = Path.Combine(temp.Path, "source");
        var targetWorkspace = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(sourceWorkspace);
        Directory.CreateDirectory(targetWorkspace);
        File.WriteAllText(Path.Combine(sourceWorkspace, "report.txt"), "RESTORED_FILE_SENTINEL");

        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        sourceTasks.Upsert(new AgentTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Goal = "Imported task",
            Status = "Completed",
            Result = "Imported result"
        });
        var sourceMemory = new MemoryService(Path.Combine(temp.Path, "source-memory.dpapi"));
        _ = sourceMemory.Save("Imported memory", "Imported content.");
        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemory, sourceWorkspace)
            .ExportJson(includeWorkspaceFileContents: true);

        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemoryPath = Path.Combine(temp.Path, "target-memory.dpapi");
        var targetMemory = new MemoryService(targetMemoryPath);
        var retained = targetMemory.Save("Keep local", "This note must remain.");
        var targetService = new WorkspaceSnapshotService(targetTasks, targetMemory, targetWorkspace);

        // Permit reads during preflight but deny replacement/deletion of the target memory file.
        // Task import and file restoration therefore happen first; the memory save then fails.
        using (var memoryLease = new FileStream(
                   targetMemoryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                _ = targetService.ImportJson(backup, restoreWorkspaceFiles: true);
                Assert.Fail("The locked DPAPI store should reject replacement.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Expected: all earlier writes must be compensated.
            }
        }

        Assert.AreEqual(0, targetTasks.GetRecent(10).Count,
            "The newly imported task must be removed when the memory-store commit fails.");
        var remainingMemories = targetMemory.GetAll(includeArchived: true);
        Assert.AreEqual(1, remainingMemories.Count);
        Assert.AreEqual(retained.Id, remainingMemories.Single().Id);
        Assert.AreEqual("This note must remain.", remainingMemories.Single().Content);
        Assert.IsFalse(File.Exists(Path.Combine(targetWorkspace, "report.txt")),
            "Files restored before the failing store commit must be rolled back.");
        Assert.AreEqual(0, Directory.EnumerateFiles(targetWorkspace, "*", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public void AtomicBackupWriterReplacesOnlyAfterSuccessfulWriteAndLeavesOldBackupOnFailure()
    {
        using var temp = new TestTempDirectory();
        var backupPath = Path.Combine(temp.Path, "workspace-backup.json");
        File.WriteAllText(backupPath, "OLD_BACKUP_SENTINEL");

        using (var existingBackupLease = new FileStream(
                   backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                WorkspaceSnapshotService.WriteJsonAtomically(backupPath, "{\\"SchemaVersion\\":2}");
                Assert.Fail("A destination opened without delete sharing must not be replaced.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Expected: a failed final move must preserve the previous backup.
            }

            Assert.AreEqual("OLD_BACKUP_SENTINEL", File.ReadAllText(backupPath));
        }

        WorkspaceSnapshotService.WriteJsonAtomically(backupPath, "{\\"SchemaVersion\\":2}");
        Assert.AreEqual("{\\"SchemaVersion\\":2}", File.ReadAllText(backupPath));
        Assert.AreEqual(0, Directory.EnumerateFiles(temp.Path, ".layal-backup-*.tmp").Count(),
            "Temporary backup files must not be left behind.");
    }

    [TestMethod]
    public void EmbeddedWorkspaceFileHashMismatchRejectsBackupBeforeImport()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Workspace stores use Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "file.txt"), "original");
        var sourceTasks = new TaskHistoryService(Path.Combine(temp.Path, "source-tasks.dpapi"));
        var sourceMemory = new MemoryService(Path.Combine(temp.Path, "source-memory.dpapi"));
        var backup = new WorkspaceSnapshotService(sourceTasks, sourceMemory, workspace)
            .ExportJson(includeWorkspaceFileContents: true);
        var node = JsonNode.Parse(backup) ?? throw new AssertFailedException("Backup JSON was empty.");
        node["Files"]![0]![nameof(WorkspaceFileManifestEntry.ContentBase64)] =
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("tampered"));

        var targetTasks = new TaskHistoryService(Path.Combine(temp.Path, "target-tasks.dpapi"));
        var targetMemory = new MemoryService(Path.Combine(temp.Path, "target-memory.dpapi"));
        var target = new WorkspaceSnapshotService(targetTasks, targetMemory, workspace);
        Assert.ThrowsExactly<InvalidOperationException>(() => target.ImportJson(node.ToJsonString()));
        Assert.AreEqual(0, targetTasks.GetRecent(10).Count);
        Assert.AreEqual(0, targetMemory.GetAll(includeArchived: true).Count);
    }

}
