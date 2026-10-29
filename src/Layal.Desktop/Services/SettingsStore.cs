using System.Globalization;
using System.Reflection;
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
        if (!File.Exists(_settingsPath)) return new UserSettings();

        // Read failures must not be mistaken for corrupt JSON: returning defaults after an
        // I/O error could cause a later Save to overwrite settings that were never read.
        var json = File.ReadAllText(_settingsPath);
        UserSettings settings;
        try
        {
            settings = DeserializeSettingsTolerantly(json);
        }
        catch (JsonException)
        {
            // Keep a recoverable copy before allowing defaults to be saved over a malformed file.
            BackupCorruptSettingsFile();
            settings = new UserSettings();
        }

        return Normalize(settings);
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tempPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(tempPath, json);
        try
        {
            if (File.Exists(_settingsPath)) File.Replace(tempPath, _settingsPath, null);
            else File.Move(tempPath, _settingsPath);
        }
        finally
        {
            // A failed replace must not leave stale temporary settings beside the real file.
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static UserSettings DeserializeSettingsTolerantly(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The settings document must be a JSON object.");

        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateObject())
            values[item.Name] = item.Value;

        var settings = new UserSettings();
        foreach (var property in typeof(UserSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length != 0 ||
                !values.TryGetValue(property.Name, out var jsonValue))
                continue;

            try
            {
                var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.PropertyType, Options);
                // JSON null is not a valid replacement for a non-nullable user preference;
                // leave that individual property at its safe default instead.
                if (value is not null)
                    property.SetValue(settings, value);
            }
            catch (JsonException)
            {
                // A single wrongly typed value should not discard every other valid setting.
            }
            catch (ArgumentException)
            {
                // Preserve the default for this property if the JSON value cannot be assigned.
            }
        }

        return settings;
    }

    private static UserSettings Normalize(UserSettings settings)
    {
        if (settings.Language is not ("ar" or "en")) settings.Language = "ar";
        if (settings.ThemeMode is not ("light" or "dark")) settings.ThemeMode = "light";

        settings.WeatherLocation = (settings.WeatherLocation ?? string.Empty).Trim();
        settings.ProviderBaseUrl = (settings.ProviderBaseUrl ?? string.Empty).Trim();
        settings.ModelId = (settings.ModelId ?? string.Empty).Trim();

        try
        {
            settings.LocalAiModelId = string.IsNullOrWhiteSpace(settings.LocalAiModelId)
                ? LocalAiService.DefaultModelId
                : LocalAiService.ValidateModelId(settings.LocalAiModelId);
        }
        catch (InvalidOperationException)
        {
            // An invalid local model identifier should only reset that preference.
            settings.LocalAiModelId = LocalAiService.DefaultModelId;
        }

        settings.SpeechToTextModelId = string.IsNullOrWhiteSpace(settings.SpeechToTextModelId)
            ? "gpt-transcribe"
            : settings.SpeechToTextModelId.Trim();
        settings.VoiceReplyModelId = string.IsNullOrWhiteSpace(settings.VoiceReplyModelId)
            ? LayalSpeechService.DefaultModelId
            : settings.VoiceReplyModelId.Trim();
        settings.VoiceReplyId = LayalSpeechService.IsSupportedVoice(settings.VoiceReplyId)
            ? settings.VoiceReplyId.Trim().ToLowerInvariant()
            : LayalSpeechService.DefaultVoiceId;

        settings.ProactiveCheckInIntervalMinutes = settings.ProactiveCheckInIntervalMinutes is 15 or 30 or 60 or 120
            ? settings.ProactiveCheckInIntervalMinutes
            : 60;
        if (settings.LastProactiveCheckInAt is { } lastCheckIn && lastCheckIn > DateTimeOffset.Now.AddMinutes(5))
            settings.LastProactiveCheckInAt = DateTimeOffset.Now;

        settings.MascotScale = double.IsFinite(settings.MascotScale)
            ? Math.Clamp(settings.MascotScale, 0.65, 1.8)
            : 1.0;
        settings.MascotOpacity = double.IsFinite(settings.MascotOpacity)
            ? Math.Clamp(settings.MascotOpacity, 0.35, 1.0)
            : 1.0;
        if (!double.IsFinite(settings.MascotLeft)) settings.MascotLeft = -1;
        if (!double.IsFinite(settings.MascotTop)) settings.MascotTop = -1;

        return settings;
    }

    private void BackupCorruptSettingsFile()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture);
            var backupPath = _settingsPath + ".corrupt-" + timestamp + suffix + ".bak";
            if (File.Exists(backupPath)) continue;

            try
            {
                File.Copy(_settingsPath, backupPath, overwrite: false);
                return;
            }
            catch (IOException) when (File.Exists(backupPath))
            {
                // Another process created this timestamped backup; choose another unique name.
            }
        }

        // Fail closed: if the corrupt file cannot be backed up, do not return defaults that
        // could later overwrite the only remaining copy of the user's settings.
        throw new IOException("The settings file is malformed, but a backup could not be created. The original file was left untouched.");
    }
}
