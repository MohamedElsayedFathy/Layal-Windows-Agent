namespace Layal.Models;

public sealed class UserSettings
{
    public string Language { get; set; } = "ar";
    public string ThemeMode { get; set; } = "light";
    /// <summary>Accent and surface palette; High Contrast always overrides this preference.</summary>
    public string ThemePreset { get; set; } = "classic";
    public string WeatherLocation { get; set; } = string.Empty;
    public bool UseAutomaticLocation { get; set; } = true;
    public string ProviderBaseUrl { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public bool UseLocalAi { get; set; } = true;
    public string LocalAiModelId { get; set; } = Layal.Services.LocalAiService.DefaultModelId;
    public string SpeechToTextModelId { get; set; } = "gpt-transcribe";
    public bool VoiceRepliesEnabled { get; set; } = true;
    public string VoiceReplyModelId { get; set; } = "gpt-realtime-2.1-mini";
    public string VoiceReplyId { get; set; } = "marin";
    public bool MascotAlwaysOnTop { get; set; } = true;
    public bool MascotAnimationsEnabled { get; set; } = true;
    public bool MascotUseGlbPreview { get; set; } = false;
    public double MascotScale { get; set; } = 1.0;
    public double MascotOpacity { get; set; } = 1.0;
    public double MascotLeft { get; set; } = -1;
    public double MascotTop { get; set; } = -1;
    public bool AllowWindowTitleInspection { get; set; } = false;
    /// <summary>Blocks new microphone, camera, screen-capture and clipboard-read operations when enabled.</summary>
    public bool PrivacyShieldEnabled { get; set; } = false;
    /// <summary>Enables local scheduled check-in notifications; this never inspects the screen or calls an AI model.</summary>
    public bool ProactiveCheckInEnabled { get; set; } = false;
    public int ProactiveCheckInIntervalMinutes { get; set; } = 60;
    public DateTimeOffset? LastProactiveCheckInAt { get; set; }

    public UserSettings Clone() => new()
    {
        Language = Language,
        ThemeMode = ThemeMode,
        ThemePreset = ThemePreset,
        WeatherLocation = WeatherLocation,
        UseAutomaticLocation = UseAutomaticLocation,
        ProviderBaseUrl = ProviderBaseUrl,
        ModelId = ModelId,
        UseLocalAi = UseLocalAi,
        LocalAiModelId = LocalAiModelId,
        SpeechToTextModelId = SpeechToTextModelId,
        VoiceRepliesEnabled = VoiceRepliesEnabled,
        VoiceReplyModelId = VoiceReplyModelId,
        VoiceReplyId = VoiceReplyId,
        MascotAlwaysOnTop = MascotAlwaysOnTop,
        MascotAnimationsEnabled = MascotAnimationsEnabled,
        MascotUseGlbPreview = MascotUseGlbPreview,
        MascotScale = MascotScale,
        MascotOpacity = MascotOpacity,
        MascotLeft = MascotLeft,
        MascotTop = MascotTop,
        AllowWindowTitleInspection = AllowWindowTitleInspection,
        PrivacyShieldEnabled = PrivacyShieldEnabled,
        ProactiveCheckInEnabled = ProactiveCheckInEnabled,
        ProactiveCheckInIntervalMinutes = ProactiveCheckInIntervalMinutes,
        LastProactiveCheckInAt = LastProactiveCheckInAt
    };

    public void CopyFrom(UserSettings source)
    {
        Language = source.Language;
        ThemeMode = source.ThemeMode;
        ThemePreset = source.ThemePreset;
        WeatherLocation = source.WeatherLocation;
        UseAutomaticLocation = source.UseAutomaticLocation;
        ProviderBaseUrl = source.ProviderBaseUrl;
        ModelId = source.ModelId;
        UseLocalAi = source.UseLocalAi;
        LocalAiModelId = source.LocalAiModelId;
        SpeechToTextModelId = source.SpeechToTextModelId;
        VoiceRepliesEnabled = source.VoiceRepliesEnabled;
        VoiceReplyModelId = source.VoiceReplyModelId;
        VoiceReplyId = source.VoiceReplyId;
        MascotAlwaysOnTop = source.MascotAlwaysOnTop;
        MascotAnimationsEnabled = source.MascotAnimationsEnabled;
        MascotUseGlbPreview = source.MascotUseGlbPreview;
        MascotScale = source.MascotScale;
        MascotOpacity = source.MascotOpacity;
        MascotLeft = source.MascotLeft;
        MascotTop = source.MascotTop;
        AllowWindowTitleInspection = source.AllowWindowTitleInspection;
        PrivacyShieldEnabled = source.PrivacyShieldEnabled;
        ProactiveCheckInEnabled = source.ProactiveCheckInEnabled;
        ProactiveCheckInIntervalMinutes = source.ProactiveCheckInIntervalMinutes;
        LastProactiveCheckInAt = source.LastProactiveCheckInAt;
    }
}
