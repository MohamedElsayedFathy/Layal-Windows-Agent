using System;
using System.Collections.Generic;

namespace Layal.Models;

/// <summary>Versioned, plaintext backup document. Workspace files are represented by metadata only.</summary>
public sealed class WorkspaceSnapshotDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentTaskRecord> Tasks { get; set; } = new();
    public List<MemoryNote> Memories { get; set; } = new();
    public List<WorkspaceFileManifestEntry> Files { get; set; } = new();
}

public sealed record WorkspaceFileManifestEntry(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    bool IsDirectory);

public sealed record WorkspaceSnapshotImportPreview(
    int TaskCount,
    int MemoryCount,
    int FileManifestCount,
    DateTimeOffset ExportedAt);

public sealed record WorkspaceSnapshotImportResult(
    int TasksImported,
    int MemoriesImported,
    int FileManifestEntries);
