namespace Layal.Models;

public sealed class UserSettings
{
    public string Language { get; set; } = "ar";
    public string ThemeMode { get; set; } = "light";
    /// <summary>Accent and surface palette; High Contrast always overrides this preference.</summary>
    public string ThemePreset { get; set; } = "classic";
    /// <summary>Validated global shortcut preset for the Quick Ask window.</summary>
    public string QuickAskHotkeyPreset { get; set; } = "AltShiftS";
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
    /// <summary>Conversational response style only; never changes safety rules, tool permissions, or confirmation requirements.</summary>
    public string AssistantPersona { get; set; } = "warm";
    /// <summary>Public Google OAuth Desktop client ID; the refresh token itself is stored in a separate DPAPI-protected file.</summary>
    public string GmailOAuthClientId { get; set; } = string.Empty;
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
        QuickAskHotkeyPreset = QuickAskHotkeyPreset,
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
        AssistantPersona = AssistantPersona,
        GmailOAuthClientId = GmailOAuthClientId,
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
        QuickAskHotkeyPreset = source.QuickAskHotkeyPreset;
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
        AssistantPersona = source.AssistantPersona;
        GmailOAuthClientId = source.GmailOAuthClientId;
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
