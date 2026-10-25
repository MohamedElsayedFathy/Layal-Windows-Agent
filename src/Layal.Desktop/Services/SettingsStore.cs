using System.Text.Json;
using Layal.Models;

namespace Layal.Services;

public sealed class SettingsStore
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public SettingsStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _settingsPath = Path.Combine(dataRoot, "settings.json");
    }

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new UserSettings();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath), Options) ?? new UserSettings();
            if (settings.Language is not ("ar" or "en")) settings.Language = "ar";
            if (settings.ThemeMode is not ("light" or "dark")) settings.ThemeMode = "light";
            settings.ProviderBaseUrl = (settings.ProviderBaseUrl ?? string.Empty).Trim();
            settings.ModelId = (settings.ModelId ?? string.Empty).Trim();
            settings.SpeechToTextModelId = string.IsNullOrWhiteSpace(settings.SpeechToTextModelId)
                ? "gpt-transcribe"
                : settings.SpeechToTextModelId.Trim();
            settings.VoiceReplyModelId = string.IsNullOrWhiteSpace(settings.VoiceReplyModelId)
                ? LayalSpeechService.DefaultModelId
                : settings.VoiceReplyModelId.Trim();
            settings.VoiceReplyId = LayalSpeechService.IsSupportedVoice(settings.VoiceReplyId)
                ? settings.VoiceReplyId.Trim().ToLowerInvariant()
                : LayalSpeechService.DefaultVoiceId;
            settings.MascotScale = Math.Clamp(settings.MascotScale, 0.65, 1.8);
            settings.MascotOpacity = Math.Clamp(settings.MascotOpacity, 0.35, 1.0);
            return settings;
        }
        catch
        {
            // A damaged settings file must not prevent the application from launching.
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        var tempPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(tempPath, json);
        if (File.Exists(_settingsPath)) File.Replace(tempPath, _settingsPath, null);
        else File.Move(tempPath, _settingsPath);
    }
}
