using Layal.Models;

namespace Layal.Services;

public sealed class TaskHistoryService
{
    public const int MaximumArtifactsPerTask = 50;
    public const int MaximumArtifactPathCharacters = 400;

    private readonly ProtectedJsonStore<List<AgentTaskRecord>> _store;
    private readonly object _sync = new();

    public TaskHistoryService(string path) => _store = new ProtectedJsonStore<List<AgentTaskRecord>>(path);

    public IReadOnlyList<AgentTaskRecord> GetRecent(int count = 30)
    {
        lock (_sync)
        {
            return _store.Load(new List<AgentTaskRecord>())
                .OrderByDescending(t => t.UpdatedAt)
                .Take(Math.Clamp(count, 1, 100))
                .Select(Clone)
                .ToList();
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

    /// <summary>Canonicalizes a safe, relative workspace path for storage and display.</summary>
    public static string NormalizeArtifactPath(string relativePath)
    {
        var value = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        if (value.Length is < 1 or > MaximumArtifactPathCharacters ||
            value.StartsWith('/', StringComparison.Ordinal) ||
            value.Contains(':') ||
            value.Any(char.IsControl))
            throw new ArgumentException("Artifact paths must be bounded, printable, workspace-relative paths.", nameof(relativePath));

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("Artifact paths cannot contain empty, '.' or '..' path segments.", nameof(relativePath));

        return string.Join('/', segments);
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
