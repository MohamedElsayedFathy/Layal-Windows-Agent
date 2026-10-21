using System.Text.Json;

namespace Layal.Services;

/// <summary>Stores JSON encrypted by Windows DPAPI for the current Windows user.</summary>
public sealed class ProtectedJsonStore<T> where T : class
{
    private readonly DpapiSecretStore _secretStore;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    public ProtectedJsonStore(string path) => _secretStore = new DpapiSecretStore(path, "Layal encrypted JSON state");

    public T Load(T fallback)
    {
        var json = _secretStore.Load();
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? fallback; }
        catch { return fallback; }
    }

    public void Save(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        _secretStore.Save(json);
    }

    public void Delete() => _secretStore.Delete();
}
