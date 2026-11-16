using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

public enum LocalResourceLifecycle
{
    Pending,
    Downloading,
    Verifying,
    Installed,
    Ready,
    Failed
}

public sealed record LocalResourceStateEntry(
    string ResourceId,
    LocalResourceLifecycle State,
    long BytesDownloaded,
    long? TotalBytes,
    double? BytesPerSecond,
    double? RemainingSeconds,
    string? ErrorMessage,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Persists resource lifecycle and byte-level progress separately from the resource files.
/// A resource is only marked Ready after its owner completes integrity and health checks.
/// </summary>
public sealed class LocalResourceManager
{
    private const int CurrentSchemaVersion = 1;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _gate;
    private readonly Dictionary<string, LocalResourceStateEntry> _entries =
        new(StringComparer.Ordinal);
    private bool _initialized;

    private sealed record StateDocument(
        int SchemaVersion,
        DateTimeOffset UpdatedAtUtc,
        Dictionary<string, LocalResourceStateEntry> Resources);

    public LocalResourceManager(string? stateFilePath = null)
    {
        var path = stateFilePath ??
            Path.Combine(LocalVoiceService.DataRoot, "resource-state.json");
        _stateFilePath = Path.GetFullPath(path);
        _gate = FileLocks.GetOrAdd(_stateFilePath, _ => new SemaphoreSlim(1, 1));
    }

    public string StateFilePath => _stateFilePath;

    public event Action<LocalResourceStateEntry>? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            var directory = Path.GetDirectoryName(_stateFilePath)
                ?? throw new InvalidOperationException("Resource state file has no parent directory.");
            EnsureNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
            EnsureNoReparsePoints(directory);
            EnsureFileIsNotReparsePoint(_stateFilePath);

            var persisted = new Dictionary<string, LocalResourceStateEntry>(StringComparer.Ordinal);
            if (File.Exists(_stateFilePath))
            {
                await using var input = new FileStream(
                    _stateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var document = await JsonSerializer.DeserializeAsync<StateDocument>(
                    input, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (document is null || document.SchemaVersion != CurrentSchemaVersion ||
                    document.Resources is null)
                    throw new InvalidDataException("The local resource state file has an unsupported or invalid schema.");

                foreach (var pair in document.Resources)
                {
                    var item = pair.Value;
                    if (item is null ||
                        !string.Equals(pair.Key, item.ResourceId, StringComparison.Ordinal) ||
                        !Enum.IsDefined(item.State) ||
                        item.BytesDownloaded < 0 ||
                        item.TotalBytes is <= 0 ||
                        (item.TotalBytes.HasValue && item.BytesDownloaded > item.TotalBytes.Value) ||
                        (item.BytesPerSecond.HasValue &&
                         (!double.IsFinite(item.BytesPerSecond.Value) || item.BytesPerSecond.Value < 0)) ||
                        (item.RemainingSeconds.HasValue &&
                         (!double.IsFinite(item.RemainingSeconds.Value) || item.RemainingSeconds.Value < 0)))
                        throw new InvalidDataException("The local resource state file contains an invalid entry.");

                    if (LocalResourceCatalog.TryGet(pair.Key, out _))
                        persisted[pair.Key] = item;
                }
            }

            foreach (var definition in LocalResourceCatalog.All)
            {
                _entries[definition.Id] = persisted.TryGetValue(definition.Id, out var item)
                    ? item
                    : NewEntry(definition.Id, LocalResourceLifecycle.Pending);
            }

            _initialized = true;
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalResourceStateEntry>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _entries.Values
                .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalResourceStateEntry> BeginDownloadAsync(
        string resourceId,
        long? totalBytes,
        CancellationToken cancellationToken = default)
    {
        if (totalBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));

        return await MutateAsync(resourceId, current =>
        {
            EnsureTransition(current.State, LocalResourceLifecycle.Downloading);
            return current with
            {
                State = LocalResourceLifecycle.Downloading,
                BytesDownloaded = 0,
                TotalBytes = totalBytes,
                BytesPerSecond = 0,
                RemainingSeconds = null,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalResourceStateEntry> ReportDownloadProgressAsync(
        string resourceId,
        long bytesDownloaded,
        long? totalBytes,
        double bytesPerSecond,
        double? remainingSeconds,
        CancellationToken cancellationToken = default)
    {
        if (bytesDownloaded < 0) throw new ArgumentOutOfRangeException(nameof(bytesDownloaded));
        if (totalBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        if (totalBytes.HasValue && bytesDownloaded > totalBytes.Value)
            throw new ArgumentOutOfRangeException(nameof(bytesDownloaded));
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        if (remainingSeconds.HasValue &&
            (!double.IsFinite(remainingSeconds.Value) || remainingSeconds.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(remainingSeconds));

        return await MutateAsync(resourceId, current =>
        {
            if (current.State != LocalResourceLifecycle.Downloading)
                throw new InvalidOperationException("Progress can only be recorded while a resource is downloading.");

            return current with
            {
                BytesDownloaded = bytesDownloaded,
                TotalBytes = totalBytes,
                BytesPerSecond = bytesPerSecond,
                RemainingSeconds = remainingSeconds,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<LocalResourceStateEntry> BeginVerificationAsync(
        string resourceId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(resourceId, current =>
        {
            EnsureTransition(current.State, LocalResourceLifecycle.Verifying);
            return current with
            {
                State = LocalResourceLifecycle.Verifying,
                BytesPerSecond = null,
                RemainingSeconds = null,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken);

    public Task<LocalResourceStateEntry> MarkInstalledAsync(
        string resourceId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(resourceId, current =>
        {
            EnsureTransition(current.State, LocalResourceLifecycle.Installed);
            return current with
            {
                State = LocalResourceLifecycle.Installed,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken);

    public Task<LocalResourceStateEntry> MarkReadyAsync(
        string resourceId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(resourceId, current =>
        {
            EnsureTransition(current.State, LocalResourceLifecycle.Ready);
            return current with
            {
                State = LocalResourceLifecycle.Ready,
                BytesPerSecond = null,
                RemainingSeconds = null,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken);

    public Task<LocalResourceStateEntry> MarkFailedAsync(
        string resourceId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        var safeMessage = errorMessage.Length <= 1000 ? errorMessage : errorMessage[..1000];
        return MutateAsync(resourceId, current =>
        {
            EnsureTransition(current.State, LocalResourceLifecycle.Failed);
            return current with
            {
                State = LocalResourceLifecycle.Failed,
                BytesPerSecond = null,
                RemainingSeconds = null,
                ErrorMessage = safeMessage,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken);
    }

    public Task<LocalResourceStateEntry> ResetAsync(
        string resourceId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(resourceId, current => current with
        {
            State = LocalResourceLifecycle.Pending,
            BytesDownloaded = 0,
            TotalBytes = null,
            BytesPerSecond = null,
            RemainingSeconds = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);

    private async Task<LocalResourceStateEntry> MutateAsync(
        string resourceId,
        Func<LocalResourceStateEntry, LocalResourceStateEntry> update,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (!LocalResourceCatalog.TryGet(resourceId, out _))
            throw new KeyNotFoundException("Unknown local resource id: " + resourceId);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        LocalResourceStateEntry updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updated = update(_entries[resourceId]);
            _entries[resourceId] = updated;
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        StateChanged?.Invoke(updated);
        return updated;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_stateFilePath)!;
        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        EnsureFileIsNotReparsePoint(_stateFilePath);

        var document = new StateDocument(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, LocalResourceStateEntry>(_entries, StringComparer.Ordinal));
        var temporary = _stateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _stateFilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static LocalResourceStateEntry NewEntry(string id, LocalResourceLifecycle state) =>
        new(id, state, 0, null, null, null, null, DateTimeOffset.UtcNow);

    private static void EnsureTransition(LocalResourceLifecycle from, LocalResourceLifecycle to)
    {
        var allowed = to switch
        {
            LocalResourceLifecycle.Downloading =>
                from is LocalResourceLifecycle.Pending or LocalResourceLifecycle.Downloading or
                    LocalResourceLifecycle.Verifying or LocalResourceLifecycle.Installed or
                    LocalResourceLifecycle.Ready or LocalResourceLifecycle.Failed,
            LocalResourceLifecycle.Verifying =>
                from is LocalResourceLifecycle.Pending or LocalResourceLifecycle.Downloading or
                    LocalResourceLifecycle.Verifying or LocalResourceLifecycle.Installed or
                    LocalResourceLifecycle.Ready or LocalResourceLifecycle.Failed,
            LocalResourceLifecycle.Installed =>
                from is LocalResourceLifecycle.Verifying or LocalResourceLifecycle.Downloading or
                    LocalResourceLifecycle.Failed,
            LocalResourceLifecycle.Ready =>
                from is LocalResourceLifecycle.Verifying or LocalResourceLifecycle.Installed,
            LocalResourceLifecycle.Failed =>
                from is not LocalResourceLifecycle.Failed,
            LocalResourceLifecycle.Pending => true,
            _ => false
        };

        if (!allowed)
            throw new InvalidOperationException(
                "Invalid local resource lifecycle transition: " + from + " -> " + to + ".");
    }

    private static void EnsureFileIsNotReparsePoint(string path)
    {
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to use a reparse-point resource state file.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The resource state path has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to store local resource state through a reparse-point directory.");
        }
    }
}
