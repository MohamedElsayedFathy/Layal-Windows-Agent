using System.Text.Json;

namespace Layal.Services;

/// <summary>Stores JSON encrypted by Windows DPAPI for the current Windows user.</summary>
public sealed class ProtectedJsonStore<T> where T : class
{
    private readonly DpapiSecretStore _secretStore;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    public ProtectedJsonStore(string path) => _secretStore = new DpapiSecretStore(path, "Layal encrypted JSON state");

    private bool _preservedUnreadable;

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
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? fallback; }
        catch
        {
            PreserveOnce();
            return fallback;
        }
    }

    private void PreserveOnce()
    {
        if (_preservedUnreadable) return;

        var recoveryCopy = _secretStore.PreserveUnreadableCopy();
        if (string.IsNullOrWhiteSpace(recoveryCopy) || !File.Exists(recoveryCopy))
        {
            // Do not hand out fallback state that a caller may later save over the only
            // remaining copy of data we could not read. A failed recovery copy must stop
            // the operation instead of turning unreadable state into an apparent empty store.
            throw new IOException(
                "The protected state file is unreadable and a recovery copy could not be created. The original was left untouched; refusing to return fallback state.");
        }

        _preservedUnreadable = true;
    }

    public void Save(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        _secretStore.Save(json);
    }

    public void Delete() => _secretStore.Delete();
}
