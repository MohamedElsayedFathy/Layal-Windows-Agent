using System.Security.Cryptography;
using System.Text.Json;

namespace Layal.Services;

/// <summary>Stores JSON encrypted by Windows DPAPI for the current Windows user.</summary>
public sealed class ProtectedJsonStore<T> where T : class
{
    private readonly string _path;
    private readonly DpapiSecretStore _secretStore;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    public ProtectedJsonStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _secretStore = new DpapiSecretStore(_path, "Layal encrypted JSON state");
    }

    // Cache the content fingerprint, not a lifetime-wide boolean. A different corrupt payload
    // must receive its own recovery copy even when this store instance has already recovered once.
    private string? _preservedUnreadableFingerprint;

    public T Load(T fallback)
    {
        var json = _secretStore.Load();
        if (string.IsNullOrWhiteSpace(json))
        {
            // A missing file is normal. An existing file that could not be decrypted must be preserved,
            // otherwise the next Save of the fallback value would overwrite it permanently.
            if (_secretStore.Exists) PreserveOnce();
            return fallback;
        }
        try
        {
            // A JSON literal "null" is not a valid persisted state for this store. Treat it
            // like corrupt JSON so the original encrypted payload is preserved before fallback
            // data can be saved over it.
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new JsonException("The protected JSON state must not have a null root value.");
        }
        catch
        {
            PreserveOnce();
            return fallback;
        }
    }

    private void PreserveOnce()
    {
        var currentFingerprint = ComputeFingerprint(_path);
        if (string.Equals(currentFingerprint, _preservedUnreadableFingerprint, StringComparison.Ordinal))
            return;

        var recoveryCopy = _secretStore.PreserveUnreadableCopy();
        if (string.IsNullOrWhiteSpace(recoveryCopy) || !File.Exists(recoveryCopy))
        {
            // Do not hand out fallback state that a caller may later save over the only
            // remaining copy of data we could not read. A failed recovery copy must stop
            // the operation instead of turning unreadable state into an apparent empty store.
            throw new IOException(
                "The protected state file is unreadable and a recovery copy could not be created. The original was left untouched; refusing to return fallback state.");
        }

        // The original may change while it is copied by another process. Only cache the state
        // if the recovery file and current source still match the bytes we initially inspected.
        var recoveryFingerprint = ComputeFingerprint(recoveryCopy);
        var afterCopyFingerprint = ComputeFingerprint(_path);
        if (!string.Equals(currentFingerprint, recoveryFingerprint, StringComparison.Ordinal) ||
            !string.Equals(currentFingerprint, afterCopyFingerprint, StringComparison.Ordinal))
        {
            throw new IOException(
                "The protected state file changed while its recovery copy was being created. The copy was retained; retry the operation before saving fallback state.");
        }

        _preservedUnreadableFingerprint = currentFingerprint;
    }

    private static string ComputeFingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Save(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        _secretStore.Save(json);
        _preservedUnreadableFingerprint = null;
    }

    public void Delete()
    {
        _secretStore.Delete();
        _preservedUnreadableFingerprint = null;
    }
}
