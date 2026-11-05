using Layal.Models;

namespace Layal.Services;

public sealed class TaskHistoryService
{
    public const int MaximumArtifactsPerTask = 50;
    public const int MaximumArtifactPathCharacters = 400;

    private readonly ProtectedJsonStore<List<AgentTaskRecord>> _store;
    private readonly object _sync = new();

    public TaskHistoryService(string path) => _store = new ProtectedJsonStore<List<AgentTaskRecord>>(path);

    public IReadOnlyList<AgentTaskRecord> GetRecent(int count = 30) =>
        GetRecentWithSelectedTask(null, count);

    /// <summary>
    /// Returns the normal recent window and, when it is still retained, appends the exact
    /// selected task if it falls outside that window. This lets reminder links resolve a
    /// task from the retained 100-record history without substituting a newer task.
    /// </summary>
    public IReadOnlyList<AgentTaskRecord> GetRecentWithSelectedTask(string? selectedTaskId, int count = 30)
    {
        lock (_sync)
        {
            var ordered = _store.Load(new List<AgentTaskRecord>())
                .OrderByDescending(t => t.UpdatedAt)
                .ToList();
            var visible = ordered.Take(Math.Clamp(count, 1, 100)).ToList();

            if (!string.IsNullOrWhiteSpace(selectedTaskId) &&
                visible.All(task => !string.Equals(task.Id, selectedTaskId, StringComparison.Ordinal)))
            {
                var exactTask = ordered.FirstOrDefault(task =>
                    string.Equals(task.Id, selectedTaskId, StringComparison.Ordinal));
                if (exactTask is not null)
                    visible.Add(exactTask);
            }

            return visible.Select(Clone).ToList();
        }
    }

    public void Upsert(AgentTaskRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            item.Artifacts = NormalizeStoredArtifacts(item.Artifacts);
            var items = _store.Load(new List<AgentTaskRecord>());
            items.RemoveAll(t => t.Id == item.Id);
            item.UpdatedAt = DateTimeOffset.Now;
            items.Add(item);
            if (items.Count > 100)
                items = items.OrderByDescending(t => t.UpdatedAt).Take(100).ToList();
            _store.Save(items);
        }
    }

    /// <summary>
    /// Merges validated backup records without overwriting or deleting existing task history.
    /// File paths remain bounded relative metadata and are never opened during import.
    /// </summary>
    public void ValidateImportMany(IEnumerable<AgentTaskRecord> imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var candidates = imported.Take(101).ToList();
        if (candidates.Count > 100)
            throw new InvalidOperationException("A task backup may contain at most 100 records.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in candidates)
        {
            if (record is null ||
                !Guid.TryParseExact(record.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(record.Goal) || record.Goal.Length > 10_000 ||
                string.IsNullOrWhiteSpace(record.Status) || record.Status.Length > 40 ||
                record.Result is null || record.Result.Length > 120_000)
                throw new InvalidOperationException("The task backup contains an invalid task record.");
            if (!ids.Add(record.Id)) continue;

            var events = record.Events ?? new List<string>();
            if (events.Count > 300 || events.Any(item => item is null || item.Length > 4_000))
                throw new InvalidOperationException("A task backup contains too many or oversized event entries.");
            _ = NormalizeStoredArtifactsForImport(record.Artifacts);
        }

        lock (_sync)
        {
            var items = _store.Load(new List<AgentTaskRecord>());
            var existingIds = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var additions = ids.Count(id => !existingIds.Contains(id));
            if (items.Count + additions > 100)
                throw new InvalidOperationException("There is not enough task-history capacity to merge this backup. Existing tasks were not changed.");
        }
    }

    public int ImportMany(IEnumerable<AgentTaskRecord> imported) =>
        ImportManyWithIds(imported).Count;

    /// <summary>
    /// Merges validated task records and returns exactly the IDs newly added by this call.
    /// The returned IDs allow the workspace snapshot coordinator to compensate if a later
    /// protected-store import fails.
    /// </summary>
    public IReadOnlyList<string> ImportManyWithIds(IEnumerable<AgentTaskRecord> imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var candidates = imported.Take(101).ToList();
        if (candidates.Count > 100)
            throw new InvalidOperationException("A task backup may contain at most 100 records.");

        var normalized = new List<AgentTaskRecord>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in candidates)
        {
            if (record is null ||
                !Guid.TryParseExact(record.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(record.Goal) || record.Goal.Length > 10_000 ||
                string.IsNullOrWhiteSpace(record.Status) || record.Status.Length > 40 ||
                record.Result is null || record.Result.Length > 120_000)
                throw new InvalidOperationException("The task backup contains an invalid task record.");

            if (!seenIds.Add(record.Id))
                continue;

            var events = record.Events ?? new List<string>();
            if (events.Count > 300 || events.Any(item => item is null || item.Length > 4_000))
                throw new InvalidOperationException("A task backup contains too many or oversized event entries.");

            var artifacts = NormalizeStoredArtifactsForImport(record.Artifacts);
            normalized.Add(new AgentTaskRecord
            {
                Id = record.Id,
                Goal = record.Goal,
                Status = record.Status,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
                Result = record.Result,
                Events = events.ToList(),
                Artifacts = artifacts
            });
        }

        lock (_sync)
        {
            var items = _store.Load(new List<AgentTaskRecord>());
            var existingIds = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var toAdd = normalized.Where(item => !existingIds.Contains(item.Id)).ToList();
            if (items.Count + toAdd.Count > 100)
                throw new InvalidOperationException("There is not enough task-history capacity to merge this backup. Existing tasks were not changed.");

            if (toAdd.Count == 0)
                return Array.Empty<string>();

            var addedIds = toAdd.Select(item => item.Id).ToArray();
            items.AddRange(toAdd);
            _store.Save(items);
            return addedIds;
        }
    }

    /// <summary>Compensates a multi-store import by removing only records added by ImportManyWithIds.</summary>
    public int RollbackImportedMany(IEnumerable<string> importedIds)
    {
        ArgumentNullException.ThrowIfNull(importedIds);
        var ids = importedIds
            .Where(id => Guid.TryParseExact(id, "N", out _))
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return 0;

        lock (_sync)
        {
            var items = _store.Load(new List<AgentTaskRecord>());
            var removed = items.RemoveAll(item => ids.Contains(item.Id));
            if (removed > 0)
                _store.Save(items);
            return removed;
        }
    }

    private static List<string> NormalizeStoredArtifactsForImport(IEnumerable<string>? artifacts)
    {
        var output = new List<string>();
        foreach (var artifact in artifacts ?? Array.Empty<string>())
        {
            var normalized = NormalizeArtifactPath(artifact);
            if (!output.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                output.Add(normalized);
            if (output.Count > MaximumArtifactsPerTask)
                throw new InvalidOperationException($"A task backup may contain at most {MaximumArtifactsPerTask} file references per task.");
        }
        return output;
    }

    /// <summary>
    /// Associates an existing task with a workspace-relative artifact. The path is metadata only:
    /// this method never opens, reads, executes, or exports the referenced file.
    /// </summary>
    public bool AttachArtifact(AgentTaskRecord task, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new ArgumentException("A task ID is required.", nameof(task));

        var normalizedPath = NormalizeArtifactPath(relativePath);
        lock (_sync)
        {
            var items = _store.Load(new List<AgentTaskRecord>());
            var stored = items.FirstOrDefault(item => string.Equals(item.Id, task.Id, StringComparison.Ordinal));
            var target = stored ?? task;
            target.Artifacts ??= new List<string>();
            target.Artifacts = NormalizeStoredArtifacts(target.Artifacts);

            if (target.Artifacts.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
            {
                task.Artifacts = target.Artifacts.ToList();
                return false;
            }
            if (target.Artifacts.Count >= MaximumArtifactsPerTask)
                throw new InvalidOperationException($"A task can reference at most {MaximumArtifactsPerTask} workspace artifacts.");

            target.Artifacts.Add(normalizedPath);
            target.UpdatedAt = DateTimeOffset.Now;
            task.Artifacts = target.Artifacts.ToList();

            if (stored is null)
                items.Add(target);
            if (items.Count > 100)
                items = items.OrderByDescending(item => item.UpdatedAt).Take(100).ToList();
            _store.Save(items);
            return true;
        }
    }

    /// <summary>Removes one artifact reference from a task after its file has been moved to the Recycle Bin.</summary>
    public bool RemoveArtifact(string taskId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return false;
        var normalizedPath = NormalizeArtifactPath(relativePath);

        lock (_sync)
        {
            var items = _store.Load(new List<AgentTaskRecord>());
            var task = items.FirstOrDefault(item => string.Equals(item.Id, taskId, StringComparison.Ordinal));
            if (task is null)
                return false;

            task.Artifacts ??= new List<string>();
            var removed = task.Artifacts.RemoveAll(path =>
                string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
                return false;

            task.Events ??= new List<string>();
            task.Events.Add("Workspace artifact moved to the Windows Recycle Bin and unlinked: " + normalizedPath);
            if (task.Events.Count > 300)
                task.Events.RemoveRange(0, task.Events.Count - 300);
            task.UpdatedAt = DateTimeOffset.Now;
            _store.Save(items);
            return true;
        }
    }

    /// <summary>Canonicalizes a safe, relative workspace path for storage and display.</summary>
    public static string NormalizeArtifactPath(string relativePath)
    {
        var value = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        if (value.Length is < 1 or > MaximumArtifactPathCharacters ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains(':') ||
            value.Any(char.IsControl))
            throw new ArgumentException("Artifact paths must be bounded, printable, workspace-relative paths.", nameof(relativePath));

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("Artifact paths cannot contain empty, '.' or '..' path segments.", nameof(relativePath));

        return string.Join("/", segments);
    }

    private static List<string> NormalizeStoredArtifacts(IEnumerable<string>? artifacts)
    {
        var normalized = new List<string>();
        foreach (var artifact in artifacts ?? Array.Empty<string>())
        {
            try
            {
                var path = NormalizeArtifactPath(artifact);
                if (!normalized.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                    normalized.Count < MaximumArtifactsPerTask)
                    normalized.Add(path);
            }
            catch (ArgumentException)
            {
                // Ignore invalid legacy/corrupt metadata rather than allowing it to escape the workspace.
            }
        }
        return normalized;
    }

    private static AgentTaskRecord Clone(AgentTaskRecord item) => new()
    {
        Id = item.Id,
        Goal = item.Goal,
        Status = item.Status,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Result = item.Result,
        Events = item.Events?.ToList() ?? new List<string>(),
        Artifacts = NormalizeStoredArtifacts(item.Artifacts)
    };
}
