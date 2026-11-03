using System;
using System.Collections.Generic;

namespace Layal.Models;

/// <summary>
/// Versioned workspace backup. Task/memory data is JSON; selected workspace file bytes are optional,
/// explicitly requested, hash-verified payloads.
/// </summary>
public sealed class WorkspaceSnapshotDocument
{
    public int SchemaVersion { get; set; } = 2;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentTaskRecord> Tasks { get; set; } = new();
    public List<MemoryNote> Memories { get; set; } = new();
    public List<WorkspaceFileManifestEntry> Files { get; set; } = new();
}

/// <summary>
/// A manifest entry. ContentBase64 and Sha256 are absent for metadata-only entries.
/// ContentOmittedReason explains why a file was not embedded in an opt-in content backup.
/// </summary>
public sealed record WorkspaceFileManifestEntry(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    bool IsDirectory,
    string? ContentBase64 = null,
    string? Sha256 = null,
    string? ContentOmittedReason = null);

public sealed record WorkspaceSnapshotImportPreview(
    int TaskCount,
    int MemoryCount,
    int FileManifestCount,
    DateTimeOffset ExportedAt,
    int FilesWithContent = 0);

public sealed record WorkspaceSnapshotImportResult(
    int TasksImported,
    int MemoriesImported,
    int FileManifestEntries,
    int FilesRestored = 0,
    int FilesSkipped = 0);
