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
        _preservedUnreadable = true;
        _ = _secretStore.PreserveUnreadableCopy();
    }

    public void Save(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        _secretStore.Save(json);
    }

    public void Delete() => _secretStore.Delete();
}
