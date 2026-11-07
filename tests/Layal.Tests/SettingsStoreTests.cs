using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void MissingFileReturnsSafeDefaults()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("ar", settings.Language);
        Assert.AreEqual(string.Empty, settings.ProviderBaseUrl);
        Assert.AreEqual(string.Empty, settings.ModelId);
        Assert.IsTrue(settings.UseLocalAi);
        Assert.AreEqual(LocalAiService.DefaultModelId, settings.LocalAiModelId);
        Assert.IsTrue(settings.MascotAlwaysOnTop);
        Assert.IsTrue(settings.MascotAnimationsEnabled);
        Assert.AreEqual(1.0, settings.MascotScale, 0.0001);
        Assert.AreEqual(1.0, settings.MascotOpacity, 0.0001);
        Assert.AreEqual(-1.0, settings.MascotLeft, 0.0001);
        Assert.AreEqual(-1.0, settings.MascotTop, 0.0001);
        Assert.IsFalse(settings.AllowWindowTitleInspection);
        Assert.AreEqual("warm", settings.AssistantPersona);
        Assert.AreEqual("natural", settings.VoiceDeliveryStyleId);
        Assert.AreEqual("duo", settings.MascotCharacterDisplayMode);
        Assert.AreEqual(string.Empty, settings.GmailOAuthClientId);
        Assert.IsFalse(settings.PrivacyShieldEnabled);
        Assert.IsFalse(settings.ProactiveCheckInEnabled);
        Assert.AreEqual(60, settings.ProactiveCheckInIntervalMinutes);
        Assert.IsNull(settings.LastProactiveCheckInAt);
        Assert.IsFalse(settings.GameModeAutoDetectionEnabled);
        Assert.AreEqual(0, settings.GameModeAllowedProcessNames.Count);
        Assert.IsFalse(settings.UseAutomaticLocation);
        Assert.IsFalse(settings.AutomaticLocationConsentConfirmed);
    }

    [TestMethod]
    public void LegacyAutomaticLocationDefaultCannotBypassTheNewExplicitConsentGate()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            """{"UseAutomaticLocation":true,"WeatherLocation":"Cairo, Egypt"}""");

        var settings = new SettingsStore(temp.Path).Load();

        Assert.IsFalse(settings.UseAutomaticLocation,
            "Legacy settings may have persisted the former automatic-location default without a user opt-in.");
        Assert.IsFalse(settings.AutomaticLocationConsentConfirmed);
        Assert.AreEqual("Cairo, Egypt", settings.WeatherLocation,
            "A previously saved city remains available as a manual fallback.");
    }

    [TestMethod]
    public void LoadNormalizesInvalidValuesAndClampsMascotSettings()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"Language\":\"fr\",\"ThemeMode\":\"neon\",\"ProviderBaseUrl\":\" \",\"ModelId\":\"\",\"MascotScale\":12,\"MascotOpacity\":-2,\"VoiceDeliveryStyleId\":\"untrusted\",\"MascotCharacterDisplayMode\":\"hidden-all\"}");

        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("ar", settings.Language);
        Assert.AreEqual(string.Empty, settings.ProviderBaseUrl);
        Assert.AreEqual(string.Empty, settings.ModelId);
        Assert.AreEqual(1.8, settings.MascotScale, 0.0001);
        Assert.AreEqual(0.35, settings.MascotOpacity, 0.0001);
        Assert.AreEqual("natural", settings.VoiceDeliveryStyleId);
        Assert.AreEqual("duo", settings.MascotCharacterDisplayMode);
    }

    [TestMethod]
    public void InvalidLocalModelIdPreservesOtherSettingsAndUsesSafeModelDefault()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"Language\":\"en\",\"ThemeMode\":\"dark\",\"ProviderBaseUrl\":\"https://provider.example/v1\",\"ModelId\":\"saved-model\",\"UseLocalAi\":false,\"LocalAiModelId\":\"../../invalid\",\"MascotScale\":1.25}");

        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("en", settings.Language);
        Assert.AreEqual("dark", settings.ThemeMode);
        Assert.AreEqual("https://provider.example/v1", settings.ProviderBaseUrl);
        Assert.AreEqual("saved-model", settings.ModelId);
        Assert.IsFalse(settings.UseLocalAi);
        Assert.AreEqual(LocalAiService.DefaultModelId, settings.LocalAiModelId);
        Assert.AreEqual(1.25, settings.MascotScale, 0.0001);
    }

    [TestMethod]
    public void InvalidPropertyTypeFallsBackOnlyForThatProperty()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"Language\":\"en\",\"ThemeMode\":\"dark\",\"ProviderBaseUrl\":\"https://provider.example/v1\",\"UseLocalAi\":false,\"MascotScale\":\"not-a-number\",\"MascotOpacity\":0.8}");

        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("en", settings.Language);
        Assert.AreEqual("dark", settings.ThemeMode);
        Assert.AreEqual("https://provider.example/v1", settings.ProviderBaseUrl);
        Assert.IsFalse(settings.UseLocalAi);
        Assert.AreEqual(1.0, settings.MascotScale, 0.0001);
        Assert.AreEqual(0.8, settings.MascotOpacity, 0.0001);
    }

    [TestMethod]
    public void SaveAndLoadRoundTripPreservesValidSettings()
    {
        using var temp = new TestTempDirectory();
        var store = new SettingsStore(temp.Path);
        store.Save(new UserSettings
        {
            Language = "en",
            ThemeMode = "dark",
            ThemePreset = "ocean",
            AssistantPersona = "focused",
            VoiceReplyId = "shimmer",
            VoiceDeliveryStyleId = "gentle",
            MascotCharacterDisplayMode = "robot",
            QuickAskHotkeyPreset = "AltShiftQ",
            GmailOAuthClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            ProviderBaseUrl = "https://provider.example/v1",
            ModelId = "test-model",
            UseLocalAi = false,
            LocalAiModelId = "qwen3.5:2b",
            MascotAlwaysOnTop = false,
            MascotAnimationsEnabled = false,
            PerformanceModeEnabled = true,
            MascotScale = 1.25,
            MascotOpacity = 0.8,
            MascotLeft = 1284.5,
            MascotTop = 424.25,
            AllowWindowTitleInspection = true,
            PrivacyShieldEnabled = true,
            ProactiveCheckInEnabled = true,
            ProactiveCheckInIntervalMinutes = 30,
            LastProactiveCheckInAt = new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.Zero),
            UseAutomaticLocation = true,
            AutomaticLocationConsentConfirmed = true
        });

        var reloaded = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("en", reloaded.Language);
        Assert.AreEqual("dark", reloaded.ThemeMode);
        Assert.AreEqual("ocean", reloaded.ThemePreset);
        Assert.AreEqual("AltShiftQ", reloaded.QuickAskHotkeyPreset);
        Assert.AreEqual("123456789012-abcdefghijklmnop.apps.googleusercontent.com", reloaded.GmailOAuthClientId);
        Assert.AreEqual("focused", reloaded.AssistantPersona);
        Assert.AreEqual("shimmer", reloaded.VoiceReplyId, "A valid user's selected voice must not be silently replaced during load.");
        Assert.AreEqual("gentle", reloaded.VoiceDeliveryStyleId);
        Assert.AreEqual("robot", reloaded.MascotCharacterDisplayMode);
        Assert.AreEqual("https://provider.example/v1", reloaded.ProviderBaseUrl);
        Assert.AreEqual("test-model", reloaded.ModelId);
        Assert.IsFalse(reloaded.UseLocalAi);
        Assert.AreEqual("qwen3.5:2b", reloaded.LocalAiModelId);
        Assert.IsFalse(reloaded.MascotAlwaysOnTop);
        Assert.IsFalse(reloaded.MascotAnimationsEnabled);
        Assert.IsTrue(reloaded.PerformanceModeEnabled);
        Assert.AreEqual(1.25, reloaded.MascotScale, 0.0001);
        Assert.AreEqual(0.8, reloaded.MascotOpacity, 0.0001);
        Assert.AreEqual(1284.5, reloaded.MascotLeft, 0.0001);
        Assert.AreEqual(424.25, reloaded.MascotTop, 0.0001);
        Assert.IsTrue(reloaded.AllowWindowTitleInspection);
        Assert.IsTrue(reloaded.PrivacyShieldEnabled);
        Assert.IsTrue(reloaded.ProactiveCheckInEnabled);
        Assert.AreEqual(30, reloaded.ProactiveCheckInIntervalMinutes);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.Zero), reloaded.LastProactiveCheckInAt);
        Assert.IsTrue(reloaded.UseAutomaticLocation);
        Assert.IsTrue(reloaded.AutomaticLocationConsentConfirmed);
    }

    [TestMethod]
    public void UserSettingsCloneAndCopyFromPreserveUnrelatedPreferences()
    {
        var original = new UserSettings
        {
            Language = "en",
            ThemeMode = "dark",
            WeatherLocation = "Cairo",
            UseAutomaticLocation = false,
            AutomaticLocationConsentConfirmed = true,
            ProviderBaseUrl = "https://provider.example/v1",
            ModelId = "old-model",
            UseLocalAi = false,
            LocalAiModelId = "qwen3.5:2b",
            SpeechToTextModelId = "old-transcriber",
            VoiceRepliesEnabled = false,
            VoiceReplyModelId = "old-voice-model",
            VoiceReplyId = "marin",
            AssistantPersona = "professional",
            VoiceDeliveryStyleId = "bright",
            MascotCharacterDisplayMode = "layal",
            GmailOAuthClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            MascotAlwaysOnTop = false,
            MascotAnimationsEnabled = false,
            PerformanceModeEnabled = true,
            MascotUseGlbPreview = true,
            MascotScale = 1.4,
            MascotOpacity = 0.75,
            MascotLeft = 120.5,
            MascotTop = 240.5,
            AllowWindowTitleInspection = true,
            PrivacyShieldEnabled = true,
            ProactiveCheckInEnabled = true,
            ProactiveCheckInIntervalMinutes = 120,
            LastProactiveCheckInAt = new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero)
        };

        var candidate = original.Clone();
        candidate.ProviderBaseUrl = "https://new-provider.example/v1";
        candidate.ModelId = "new-model";

        Assert.AreEqual("https://provider.example/v1", original.ProviderBaseUrl);
        Assert.AreEqual("old-model", original.ModelId);
        Assert.AreEqual("dark", candidate.ThemeMode);
        Assert.AreEqual("Cairo", candidate.WeatherLocation);
        Assert.IsFalse(candidate.UseAutomaticLocation);
        Assert.IsTrue(candidate.AutomaticLocationConsentConfirmed);
        Assert.IsFalse(candidate.UseLocalAi);
        Assert.AreEqual("qwen3.5:2b", candidate.LocalAiModelId);
        Assert.AreEqual("old-transcriber", candidate.SpeechToTextModelId);
        Assert.IsFalse(candidate.VoiceRepliesEnabled);
        Assert.AreEqual("old-voice-model", candidate.VoiceReplyModelId);
        Assert.AreEqual("professional", candidate.AssistantPersona);
        Assert.AreEqual("bright", candidate.VoiceDeliveryStyleId);
        Assert.AreEqual("layal", candidate.MascotCharacterDisplayMode);
        Assert.AreEqual("123456789012-abcdefghijklmnop.apps.googleusercontent.com", candidate.GmailOAuthClientId);
        Assert.IsFalse(candidate.MascotAlwaysOnTop);
        Assert.IsFalse(candidate.MascotAnimationsEnabled);
        Assert.IsTrue(candidate.PerformanceModeEnabled);
        Assert.IsTrue(candidate.MascotUseGlbPreview);
        Assert.AreEqual(1.4, candidate.MascotScale, 0.0001);
        Assert.AreEqual(0.75, candidate.MascotOpacity, 0.0001);
        Assert.AreEqual(120.5, candidate.MascotLeft, 0.0001);
        Assert.AreEqual(240.5, candidate.MascotTop, 0.0001);
        Assert.IsTrue(candidate.AllowWindowTitleInspection);
        Assert.IsTrue(candidate.PrivacyShieldEnabled);
        Assert.IsTrue(candidate.ProactiveCheckInEnabled);
        Assert.AreEqual(120, candidate.ProactiveCheckInIntervalMinutes);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero), candidate.LastProactiveCheckInAt);

        var target = new UserSettings();
        target.CopyFrom(candidate);
        Assert.AreEqual("https://new-provider.example/v1", target.ProviderBaseUrl);
        Assert.AreEqual("new-model", target.ModelId);
        Assert.AreEqual("en", target.Language);
        Assert.AreEqual("dark", target.ThemeMode);
        Assert.AreEqual("marin", target.VoiceReplyId);
        Assert.AreEqual("professional", target.AssistantPersona);
        Assert.AreEqual("bright", target.VoiceDeliveryStyleId);
        Assert.AreEqual("layal", target.MascotCharacterDisplayMode);
        Assert.IsTrue(target.AutomaticLocationConsentConfirmed);
        Assert.AreEqual("123456789012-abcdefghijklmnop.apps.googleusercontent.com", target.GmailOAuthClientId);
        Assert.IsTrue(target.AllowWindowTitleInspection);
        Assert.IsTrue(target.PerformanceModeEnabled);
        Assert.IsTrue(target.PrivacyShieldEnabled);
        Assert.IsTrue(target.ProactiveCheckInEnabled);
        Assert.AreEqual(120, target.ProactiveCheckInIntervalMinutes);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero), target.LastProactiveCheckInAt);
    }

    [TestMethod]
    public void InvalidThemePresetFallsBackToClassicWithoutChangingLightDarkPreference()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"ThemeMode\":\"dark\",\"ThemePreset\":\"malicious-custom-value\",\"AssistantPersona\":\"malicious-custom-value\",\"GmailOAuthClientId\":\"not-a-valid-client\"}");
        var settings = new SettingsStore(temp.Path).Load();
        Assert.AreEqual("dark", settings.ThemeMode);
        Assert.AreEqual("classic", settings.ThemePreset);
        Assert.AreEqual("AltShiftS", settings.QuickAskHotkeyPreset);
        Assert.AreEqual("warm", settings.AssistantPersona);
    }

    [TestMethod]
    public void InvalidCheckInIntervalFallsBackToSixtyMinutes()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"ProactiveCheckInEnabled\":true,\"ProactiveCheckInIntervalMinutes\":9999}");
        var settings = new SettingsStore(temp.Path).Load();
        Assert.IsTrue(settings.ProactiveCheckInEnabled);
        Assert.AreEqual(60, settings.ProactiveCheckInIntervalMinutes);
    }

    [TestMethod]
    public void GameModeDetectionRequiresAValidExplicitProcessAllowlist()
    {
        using var temp = new TestTempDirectory();
        var store = new SettingsStore(temp.Path);
        store.Save(new UserSettings
        {
            GameModeAutoDetectionEnabled = true,
            GameModeAllowedProcessNames = new() { "Cyberpunk2077.exe", "cs2", "../unsafe", "Cyberpunk2077" }
        });

        var loaded = store.Load();

        Assert.IsTrue(loaded.GameModeAutoDetectionEnabled);
        CollectionAssert.AreEqual(new[] { "Cyberpunk2077", "cs2" }, loaded.GameModeAllowedProcessNames);
    }

    [TestMethod]
    public void GameModeDetectionIsForcedOffWhenAllowlistIsEmptyOrInvalid()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            """{"GameModeAutoDetectionEnabled":true,"GameModeAllowedProcessNames":["../bad","bad/name"]}""");
        var loaded = new SettingsStore(temp.Path).Load();

        Assert.IsFalse(loaded.GameModeAutoDetectionEnabled);
        Assert.AreEqual(0, loaded.GameModeAllowedProcessNames.Count);
    }

    [TestMethod]
    public void UserSettingsCloneAndCopyFromDeepCopyGameModeAllowlist()
    {
        var original = new UserSettings
        {
            GameModeAutoDetectionEnabled = true,
            GameModeAllowedProcessNames = new() { "cs2", "game-win64" }
        };
        var clone = original.Clone();
        clone.GameModeAllowedProcessNames.Add("another-game");
        Assert.AreEqual(2, original.GameModeAllowedProcessNames.Count);

        var target = new UserSettings();
        target.CopyFrom(clone);
        target.GameModeAllowedProcessNames.Clear();
        Assert.AreEqual(3, clone.GameModeAllowedProcessNames.Count);
        Assert.IsTrue(target.GameModeAutoDetectionEnabled);
    }

    [TestMethod]
    public void DamagedSettingsAreBackedUpBeforeSafeDefaultsAreReturned()
    {
        using var temp = new TestTempDirectory();
        const string damagedJson = "{ this is not valid json";
        var settingsPath = System.IO.Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settingsPath, damagedJson);

        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("ar", settings.Language);
        Assert.AreEqual("light", settings.ThemeMode);
        Assert.AreEqual("classic", settings.ThemePreset);
        Assert.AreEqual(string.Empty, settings.ModelId);
        Assert.IsFalse(settings.AllowWindowTitleInspection);
        Assert.AreEqual(damagedJson, File.ReadAllText(settingsPath), "The original malformed file must remain untouched during recovery.");

        var backups = Directory.GetFiles(temp.Path, "settings.json.corrupt-*.bak");
        Assert.AreEqual(1, backups.Length, "A recoverable copy of the malformed settings must be created.");
        Assert.AreEqual(damagedJson, File.ReadAllText(backups[0]));
    }
}
