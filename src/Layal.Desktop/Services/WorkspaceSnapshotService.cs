using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Creates and merges a versioned workspace backup. Task and memory data are exported as plaintext
/// JSON; workspace files are represented by metadata only and are never read, copied, executed, or restored.
/// Imports merge into the local DPAPI stores without overwriting existing IDs or deleting records.
/// </summary>
public sealed class WorkspaceSnapshotService
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumJsonBytes = 10 * 1024 * 1024;
    public const int MaximumManifestEntries = 300;
    private const int MaximumFolderDepth = 4;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", ".cache"
    };

    private readonly TaskHistoryService _taskHistory;
    private readonly MemoryService _memory;
    private readonly string _workspaceRoot;

    public WorkspaceSnapshotService(TaskHistoryService taskHistory, MemoryService memory, string workspaceRoot)
    {
        _taskHistory = taskHistory ?? throw new ArgumentNullException(nameof(taskHistory));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public string ExportJson()
    {
        var document = new WorkspaceSnapshotDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Tasks = _taskHistory.GetRecent(100).ToList(),
            Memories = _memory.GetAll(includeArchived: true).ToList(),
            Files = BuildFileManifest()
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidOperationException($"The workspace backup exceeds the {MaximumJsonBytes / (1024 * 1024)} MB export limit.");
        return json;
    }

    public WorkspaceSnapshotImportPreview PreviewImportJson(string json)
    {
        var document = ParseAndValidate(json);
        return new WorkspaceSnapshotImportPreview(
            document.Tasks.Count,
            document.Memories.Count,
            document.Files.Count,
            document.ExportedAt);
    }

    public WorkspaceSnapshotImportResult ImportJson(string json)
    {
        var document = ParseAndValidate(json);

        // Preflight both DPAPI stores before either is changed. Each store repeats validation
        // while holding its own lock when the merge is committed.
        _taskHistory.ValidateImportMany(document.Tasks);
        _memory.ValidateImportMany(document.Memories);

        var tasksImported = _taskHistory.ImportMany(document.Tasks);
        var memoriesImported = _memory.ImportMany(document.Memories);
        return new WorkspaceSnapshotImportResult(tasksImported, memoriesImported, document.Files.Count);
    }

    private WorkspaceSnapshotDocument ParseAndValidate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The selected workspace backup is empty.");
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidOperationException($"The workspace backup exceeds the {MaximumJsonBytes / (1024 * 1024)} MB import limit.");

        WorkspaceSnapshotDocument document;
        try
        {
            document = JsonSerializer.Deserialize<WorkspaceSnapshotDocument>(
                json,
                new JsonSerializerOptions { MaxDepth = 24 })
                ?? throw new JsonException("The backup document is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The selected file is not a valid Layal workspace backup.", ex);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"Workspace backup version {document.SchemaVersion} is not supported.");
        if (document.Tasks is null || document.Memories is null || document.Files is null)
            throw new InvalidOperationException("The workspace backup is missing one or more required collections.");
        if (document.Tasks.Count > 100 || document.Memories.Count > MemoryService.MaxSavedNotes ||
            document.Files.Count > MaximumManifestEntries)
            throw new InvalidOperationException("The workspace backup exceeds one or more record-count limits.");

        ValidateFileManifest(document.Files);
        _taskHistory.ValidateImportMany(document.Tasks);
        _memory.ValidateImportMany(document.Memories);
        return document;
    }

    private static void ValidateFileManifest(IEnumerable<WorkspaceFileManifestEntry> manifest)
    {
        foreach (var entry in manifest)
        {
            if (entry is null || entry.SizeBytes < 0)
                throw new InvalidOperationException("The workspace backup contains an invalid file-manifest entry.");
            var normalized = TaskHistoryService.NormalizeArtifactPath(entry.RelativePath);
            if (!string.Equals(normalized, entry.RelativePath, StringComparison.Ordinal))
                throw new InvalidOperationException("The workspace backup contains a non-canonical file path.");
        }
    }

    private List<WorkspaceFileManifestEntry> BuildFileManifest()
    {
        var output = new List<WorkspaceFileManifestEntry>();
        if (!Directory.Exists(_workspaceRoot))
            return output;

        try
        {
            if ((File.GetAttributes(_workspaceRoot) & FileAttributes.ReparsePoint) != 0)
                return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return output;
        }

        var prefix = Path.TrimEndingDirectorySeparator(_workspaceRoot) + Path.DirectorySeparatorChar;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((_workspaceRoot, 0));

        while (pending.Count > 0 && output.Count < MaximumManifestEntries)
        {
            var (directory, depth) = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.EnumerateFiles(directory).Take(MaximumManifestEntries).ToArray();
                directories = Directory.EnumerateDirectories(directory).Take(MaximumManifestEntries).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (output.Count >= MaximumManifestEntries) break;
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var fullPath = Path.GetFullPath(file);
                    if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = TaskHistoryService.NormalizeArtifactPath(
                        Path.GetRelativePath(_workspaceRoot, fullPath).Replace('\\', '/'));
                    output.Add(new WorkspaceFileManifestEntry(
                        relative,
                        Math.Max(0, info.Length),
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        IsDirectory: false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Skip entries that disappear or become inaccessible while building the manifest.
                }
            }

            if (depth >= MaximumFolderDepth) continue;
            foreach (var child in directories)
            {
                if (output.Count >= MaximumManifestEntries) break;
                try
                {
                    var info = new DirectoryInfo(child);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ExcludedDirectories.Contains(info.Name))
                        continue;
                    var fullPath = Path.GetFullPath(child);
                    if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = TaskHistoryService.NormalizeArtifactPath(
                        Path.GetRelativePath(_workspaceRoot, fullPath).Replace('\\', '/'));
                    pending.Push((fullPath, depth + 1));
                    output.Add(new WorkspaceFileManifestEntry(
                        relative,
                        0,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        IsDirectory: true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Skip entries that disappear or become inaccessible during traversal.
                }
            }
        }

        return output;
    }
}
