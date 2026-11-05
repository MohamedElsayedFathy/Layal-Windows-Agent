using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Creates and merges a bounded workspace backup. Task/memory records are exported as plaintext JSON.
/// File contents are metadata-only by default; a separate explicit option embeds a small, bounded set.
/// Imports merge records without overwriting existing IDs. File restoration is independently opt-in,
/// hash-verified, create-new only, and never executes or opens restored files.
/// </summary>
public sealed class WorkspaceSnapshotService
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumJsonBytes = 10 * 1024 * 1024;
    public const int MaximumManifestEntries = 300;
    public const int MaximumEmbeddedFiles = 100;
    public const int MaximumSingleFileContentBytes = 1 * 1024 * 1024;
    public const int MaximumTotalFileContentBytes = 4 * 1024 * 1024;
    private const int MaximumFolderDepth = 4;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", ".cache"
    };

    private readonly TaskHistoryService _taskHistory;
    private readonly MemoryService _memory;
    private readonly string _workspaceRoot;
    private readonly string _rootPrefix;

    public WorkspaceSnapshotService(TaskHistoryService taskHistory, MemoryService memory, string workspaceRoot)
    {
        _taskHistory = taskHistory ?? throw new ArgumentNullException(nameof(taskHistory));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _rootPrefix = _workspaceRoot + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Metadata-only is the safe default. Pass includeWorkspaceFileContents=true only after
    /// warning the user that the resulting unencrypted JSON contains local file bytes.
    /// </summary>
    public string ExportJson(bool includeWorkspaceFileContents = false)
    {
        var document = new WorkspaceSnapshotDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Tasks = _taskHistory.GetRecent(100).ToList(),
            Memories = _memory.GetAll(includeArchived: true).ToList(),
            Files = BuildFileManifest(includeWorkspaceFileContents)
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidOperationException($"The workspace backup exceeds the {MaximumJsonBytes / (1024 * 1024)} MB export limit.");
        return json;
    }

    /// <summary>
    /// Writes a validated-size plaintext JSON backup through a same-directory temporary file.
    /// The previous backup remains intact unless the complete new payload has been flushed and moved into place.
    /// </summary>
    public static void WriteJsonAtomically(string destinationPath, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The workspace backup is empty.");

        var bytes = Encoding.UTF8.GetBytes(json);
        var temporaryPath = string.Empty;
        try
        {
            if (bytes.Length > MaximumJsonBytes)
                throw new InvalidOperationException($"The workspace backup exceeds the {MaximumJsonBytes / (1024 * 1024)} MB export limit.");

            var fullPath = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The selected backup path has no parent directory.");
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("The selected backup directory no longer exists.");
            if (Directory.Exists(fullPath))
                throw new IOException("The selected backup destination is a directory.");

            temporaryPath = Path.Combine(directory, $".layal-backup-{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       options: FileOptions.WriteThrough))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(flushToDisk: true);
            }

            // Keep the temporary file on the same volume so the final replacement is atomic.
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            try
            {
                if (temporaryPath.Length > 0 && File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public WorkspaceSnapshotImportPreview PreviewImportJson(string json)
    {
        var document = ParseAndValidate(json);
        return new WorkspaceSnapshotImportPreview(
            document.Tasks.Count,
            document.Memories.Count,
            document.Files.Count,
            document.ExportedAt,
            document.Files.Count(entry => entry.ContentBase64 is not null),
            document.Files.Select(entry => new WorkspaceSnapshotFilePreview(
                entry.RelativePath,
                entry.SizeBytes,
                entry.IsDirectory,
                entry.ContentBase64 is not null,
                entry.ContentOmittedReason)).ToArray());
    }

    /// <summary>
    /// Merges task and memory records. Embedded file bytes are never restored unless the caller
    /// explicitly passes restoreWorkspaceFiles=true after a separate user confirmation.
    /// </summary>
    public WorkspaceSnapshotImportResult ImportJson(string json, bool restoreWorkspaceFiles = false)
    {
        var document = ParseAndValidate(json);

        // Preflight both protected stores before changing either one.
        _taskHistory.ValidateImportMany(document.Tasks);
        _memory.ValidateImportMany(document.Memories);

        var createdFiles = new List<string>();
        var createdDirectories = new List<string>();
        var filesRestored = 0;
        var embeddedCount = document.Files.Count(entry => entry.ContentBase64 is not null);
        var filesSkipped = restoreWorkspaceFiles ? 0 : embeddedCount;

        // Capture only IDs that were absent before this import. If either DPAPI store fails
        // after the other one has committed, these IDs let us compensate without touching
        // any pre-existing local task or memory record.
        var existingTaskIds = _taskHistory.GetRecent(100)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        var existingMemoryIds = _memory.GetAll(includeArchived: true)
            .Select(note => note.Id)
            .ToHashSet(StringComparer.Ordinal);
        var taskRollbackIds = document.Tasks
            .Select(task => task.Id)
            .Where(id => !existingTaskIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var memoryRollbackIds = document.Memories
            .Select(note => note.Id)
            .Where(id => !existingMemoryIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var taskImportAttempted = false;
        var memoryImportAttempted = false;
        try
        {
            if (restoreWorkspaceFiles)
                filesRestored = RestoreWorkspaceFiles(document.Files, createdFiles, createdDirectories, out filesSkipped);

            taskImportAttempted = true;
            var importedTaskIds = _taskHistory.ImportManyWithIds(document.Tasks);
            memoryImportAttempted = true;
            var importedMemoryIds = _memory.ImportManyWithIds(document.Memories);
            return new WorkspaceSnapshotImportResult(
                importedTaskIds.Count, importedMemoryIds.Count, document.Files.Count, filesRestored, filesSkipped);
        }
        catch (Exception importFailure)
        {
            var rollbackFailures = new List<Exception>();
            if (memoryImportAttempted)
            {
                try { _memory.RollbackImportedMany(memoryRollbackIds); }
                catch (Exception rollbackFailure) { rollbackFailures.Add(rollbackFailure); }
            }
            if (taskImportAttempted)
            {
                try { _taskHistory.RollbackImportedMany(taskRollbackIds); }
                catch (Exception rollbackFailure) { rollbackFailures.Add(rollbackFailure); }
            }

            try { RollBackCreatedFiles(createdFiles, createdDirectories); }
            catch (Exception rollbackFailure) { rollbackFailures.Add(rollbackFailure); }

            if (rollbackFailures.Count > 0)
                throw new AggregateException(
                    "Workspace import failed and one or more compensating rollback operations also failed. Review local DPAPI stores and restored workspace files.",
                    new[] { importFailure }.Concat(rollbackFailures));

            throw;
        }
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

        // Version 1 backups remain readable; their file entries contain metadata only.
        if (document.SchemaVersion is not 1 and not CurrentSchemaVersion)
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

    private static void ValidateFileManifest(IReadOnlyList<WorkspaceFileManifestEntry> manifest)
    {
        var paths = new Dictionary<string, WorkspaceFileManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var embeddedFiles = 0;
        long embeddedBytes = 0;

        foreach (var entry in manifest)
        {
            if (entry is null || entry.SizeBytes < 0 || string.IsNullOrWhiteSpace(entry.RelativePath))
                throw new InvalidOperationException("The workspace backup contains an invalid file-manifest entry.");

            var normalized = TaskHistoryService.NormalizeArtifactPath(entry.RelativePath);
            if (!string.Equals(normalized, entry.RelativePath, StringComparison.Ordinal))
                throw new InvalidOperationException("The workspace backup contains a non-canonical file path.");
            ValidateSafeWorkspaceRelativePath(normalized);
            if (!paths.TryAdd(normalized, entry))
                throw new InvalidOperationException("The workspace backup contains duplicate file paths.");

            if (entry.IsDirectory && entry.SizeBytes != 0)
                throw new InvalidOperationException("A directory manifest entry cannot have a file size.");
            if (entry.ContentOmittedReason?.Length > 200)
                throw new InvalidOperationException("A file omission note exceeds the allowed length.");

            if (entry.ContentBase64 is null)
            {
                if (entry.Sha256 is not null)
                    throw new InvalidOperationException("A file hash was supplied without its content.");
                continue;
            }

            if (entry.IsDirectory || entry.Sha256 is null || entry.Sha256.Length != 64 ||
                entry.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidOperationException("An embedded file entry has invalid type or hash metadata.");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(entry.ContentBase64); }
            catch (FormatException ex) { throw new InvalidOperationException("An embedded file payload is not valid Base64.", ex); }

            try
            {
                if (bytes.Length > MaximumSingleFileContentBytes || bytes.LongLength != entry.SizeBytes)
                    throw new InvalidOperationException("An embedded file payload does not match its declared size or exceeds the per-file limit.");
                var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("An embedded file failed its SHA-256 integrity check.");
                embeddedFiles++;
                embeddedBytes += bytes.Length;
                if (embeddedFiles > MaximumEmbeddedFiles || embeddedBytes > MaximumTotalFileContentBytes)
                    throw new InvalidOperationException("The backup exceeds the embedded file count or total-size limit.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        foreach (var pair in paths)
        {
            var segments = pair.Key.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                var parent = string.Join("/", segments.Take(index));
                if (paths.TryGetValue(parent, out var parentEntry) && !parentEntry.IsDirectory)
                    throw new InvalidOperationException("A file path is also used as a parent directory in the backup.");
            }
        }
    }

    private List<WorkspaceFileManifestEntry> BuildFileManifest(bool includeContents)
    {
        var output = new List<WorkspaceFileManifestEntry>();
        if (!Directory.Exists(_workspaceRoot))
            return output;

        try
        {
            EnsureNoReparsePointsInExistingPath(_workspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return output;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((_workspaceRoot, 0));
        var embeddedFiles = 0;
        long embeddedBytes = 0;

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
                    EnsureWithinWorkspace(fullPath);
                    var relative = TaskHistoryService.NormalizeArtifactPath(
                        Path.GetRelativePath(_workspaceRoot, fullPath).Replace('\\', '/'));

                    string? contentBase64 = null;
                    string? sha256 = null;
                    string? omittedReason = null;
                    if (includeContents)
                    {
                        if (info.Length > MaximumSingleFileContentBytes)
                            omittedReason = "File exceeds the 1 MiB per-file backup limit.";
                        else if (embeddedFiles >= MaximumEmbeddedFiles)
                            omittedReason = "The backup reached the 100-file content limit.";
                        else if (embeddedBytes + info.Length > MaximumTotalFileContentBytes)
                            omittedReason = "The backup reached the 4 MiB total file-content limit.";
                        else
                        {
                            var bytes = ReadStableFile(fullPath, info.Length);
                            try
                            {
                                contentBase64 = Convert.ToBase64String(bytes);
                                sha256 = Convert.ToHexString(SHA256.HashData(bytes));
                                embeddedFiles++;
                                embeddedBytes += bytes.Length;
                                info.Refresh();
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(bytes);
                            }
                        }
                    }

                    output.Add(new WorkspaceFileManifestEntry(
                        relative,
                        Math.Max(0, info.Length),
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        IsDirectory: false,
                        contentBase64,
                        sha256,
                        omittedReason));
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
                    EnsureWithinWorkspace(fullPath);
                    var relative = TaskHistoryService.NormalizeArtifactPath(
                        Path.GetRelativePath(_workspaceRoot, fullPath).Replace('\\', '/'));
                    pending.Push((fullPath, depth + 1));
                    output.Add(new WorkspaceFileManifestEntry(
                        relative, 0, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), IsDirectory: true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Skip entries that disappear or become inaccessible while traversing.
                }
            }
        }

        return output;
    }

    private byte[] ReadStableFile(string path, long expectedLength)
    {
        EnsureNoReparsePointsInExistingPath(Path.GetDirectoryName(path)!);
        var before = new FileInfo(path);
        if ((before.Attributes & FileAttributes.ReparsePoint) != 0 || before.Length != expectedLength ||
            expectedLength > MaximumSingleFileContentBytes)
            throw new IOException("A workspace file changed before it could be backed up.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expectedLength)
            throw new IOException("A workspace file changed while the backup was being prepared.");
        var bytes = new byte[checked((int)expectedLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new EndOfStreamException("A workspace file was truncated while being backed up.");
            offset += read;
        }
        if (stream.ReadByte() != -1)
            throw new IOException("A workspace file grew while the backup was being prepared.");

        var after = new FileInfo(path);
        after.Refresh();
        if ((after.Attributes & FileAttributes.ReparsePoint) != 0 || after.Length != expectedLength ||
            after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new IOException("A workspace file changed while the backup was being prepared.");
        }
        return bytes;
    }

    private int RestoreWorkspaceFiles(
        IEnumerable<WorkspaceFileManifestEntry> manifest,
        List<string> createdFiles,
        List<string> createdDirectories,
        out int skipped)
    {
        skipped = 0;
        var restored = 0;
        var files = manifest.Where(entry => entry.ContentBase64 is not null)
            .OrderBy(entry => entry.RelativePath.Count(character => character == '/'))
            .ToArray();
        if (files.Length == 0)
            return 0;

        Directory.CreateDirectory(_workspaceRoot);
        EnsureNoReparsePointsInExistingPath(_workspaceRoot);

        foreach (var entry in files)
        {
            var bytes = Convert.FromBase64String(entry.ContentBase64!);
            try
            {
                var target = ResolveSafeTargetPath(entry.RelativePath);
                var parent = Path.GetDirectoryName(target)!;
                EnsureParentDirectories(parent, createdDirectories);

                if (File.Exists(target) || Directory.Exists(target))
                {
                    skipped++;
                    continue;
                }

                EnsureNoReparsePointsInExistingPath(parent);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Register the file immediately after CreateNew succeeds so a failed write
                    // or timestamp update can still remove the partial file during rollback.
                    createdFiles.Add(target);
                    output.Write(bytes, 0, bytes.Length);
                }
                File.SetLastWriteTimeUtc(target, entry.LastWriteTimeUtc.UtcDateTime);
                restored++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return restored;
    }

    private void EnsureParentDirectories(string parent, List<string> createdDirectories)
    {
        EnsureWithinWorkspace(parent);
        var relative = Path.GetRelativePath(_workspaceRoot, parent);
        var current = _workspaceRoot;
        if (!Directory.Exists(current))
        {
            Directory.CreateDirectory(current);
            createdDirectories.Add(current);
        }
        EnsureNoReparsePointsInExistingPath(current);

        if (relative == ".")
            return;

        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                if (File.Exists(current))
                    throw new IOException("A workspace file blocks a required restore directory.");
                Directory.CreateDirectory(current);
                createdDirectories.Add(current);
            }
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("File restoration is blocked by a symbolic link or non-directory parent.");
        }
    }

    private string ResolveSafeTargetPath(string relativePath)
    {
        var normalized = TaskHistoryService.NormalizeArtifactPath(relativePath);
        if (!string.Equals(normalized, relativePath, StringComparison.Ordinal))
            throw new InvalidOperationException("A file restore path is not canonical.");
        ValidateSafeWorkspaceRelativePath(normalized);
        var target = Path.GetFullPath(Path.Combine(_workspaceRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithinWorkspace(target);
        return target;
    }

    private static void ValidateSafeWorkspaceRelativePath(string relativePath)
    {
        if (relativePath.Length > TaskHistoryService.MaximumArtifactPathCharacters)
            throw new InvalidOperationException("A workspace restore path exceeds the supported length.");

        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length is < 1 or > 255 ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(character => character is '<' or '>' or '"' or '|' or '?' or '*' or ':'))
                throw new InvalidOperationException("A workspace restore path contains a Windows-invalid file or directory name.");

            var deviceStem = segment.Split('.')[0].TrimEnd(' ', '.');
            if (deviceStem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceStem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceStem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceStem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                deviceStem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                deviceStem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                IsNumberedWindowsDeviceName(deviceStem, "COM") ||
                IsNumberedWindowsDeviceName(deviceStem, "LPT"))
                throw new InvalidOperationException("Windows device names cannot be restored as workspace files.");

            if (ExcludedDirectories.Contains(segment))
                throw new InvalidOperationException("A workspace backup cannot restore protected build or repository directories.");
        }
    }

    private static bool IsNumberedWindowsDeviceName(string value, string prefix) =>
        value.Length == prefix.Length + 1 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[^1] is >= '1' and <= '9';

    private void EnsureWithinWorkspace(string fullPath)
    {
        var canonical = Path.GetFullPath(fullPath);
        if (!canonical.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(canonical, _workspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A workspace path resolves outside LayalWorkspace.");
    }

    private static void EnsureNoReparsePointsInExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The workspace path has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                throw new DirectoryNotFoundException("A workspace directory does not exist.");
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("The workspace path contains a symbolic link, reparse point, or non-directory parent.");
        }
    }

    private static void RollBackCreatedFiles(List<string> files, List<string> directories)
    {
        foreach (var path in files.AsEnumerable().Reverse())
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var path in directories.OrderByDescending(value => value.Length))
        {
            try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
