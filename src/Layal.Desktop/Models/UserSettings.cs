using System.Collections.Generic;

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
    /// <summary>Automatic device location is off until the user explicitly opts in.</summary>
    public bool UseAutomaticLocation { get; set; } = false;
    /// <summary>Records the user's explicit disclosure consent before device coordinates may be sent to Open-Meteo.</summary>
    public bool AutomaticLocationConsentConfirmed { get; set; } = false;
    public string ProviderBaseUrl { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    /// <summary>Optional OpenAI-compatible local model endpoint. Only loopback URLs are accepted.</summary>
    public string LocalChatModelBaseUrl { get; set; } = Layal.Services.LocalChatModelService.DefaultBaseUrl;
    /// <summary>Model ID served by the local loopback endpoint; blank keeps the deterministic native core active.</summary>
    public string LocalChatModelId { get; set; } = string.Empty;
    public bool UseLocalAi { get; set; } = true;
    public bool VoiceRepliesEnabled { get; set; } = true;
    /// <summary>Stable WinMM microphone selection; Windows default is used until the user chooses a device.</summary>
    public string AudioInputDeviceId { get; set; } = Layal.Services.AudioDeviceService.WindowsDefaultDeviceId;
    /// <summary>Stable WinMM playback selection; Windows default is used until the user chooses a device.</summary>
    public string AudioOutputDeviceId { get; set; } = Layal.Services.AudioDeviceService.WindowsDefaultDeviceId;
    /// <summary>Conversational response style only; never changes safety rules, tool permissions, or confirmation requirements.</summary>
    public string AssistantPersona { get; set; } = "warm";
    /// <summary>Optional local Piper speech delivery pace; never changes generated text or task permissions.</summary>
    public string VoiceDeliveryStyleId { get; set; } = Layal.Services.VoiceDeliveryStylePolicy.DefaultStyle;
    /// <summary>VoiceTut Voice Design attributes; no reference audio or speaker cloning is used.</summary>
    public string VoiceDesignInstruction { get; set; } = Layal.Services.VoiceTutLocalService.DefaultVoiceDesign;
    public int VoiceQualitySteps { get; set; } = 32;
    public double VoiceGuidanceScale { get; set; } = 2.0;
    public double VoiceSpeed { get; set; } = 1.0;
    public bool VoiceTextNormalizationEnabled { get; set; } = true;
    /// <summary>Which existing artwork is displayed by the floating mascot: Layal, robot, or both.</summary>
    public string MascotCharacterDisplayMode { get; set; } = Layal.Services.CharacterDisplayModePolicy.DefaultMode;
    /// <summary>Public Google OAuth Desktop client ID; the refresh token itself is stored in a separate DPAPI-protected file.</summary>
    public string GmailOAuthClientId { get; set; } = string.Empty;
    public bool MascotAlwaysOnTop { get; set; } = true;
    public bool MascotAnimationsEnabled { get; set; } = true;
    /// <summary>Temporarily pauses mascot motion and particles, disposes the optional GLB renderer, reduces visible-window hit testing to about 30 Hz, and suspends polling while hidden. Does not overwrite saved animation or GLB preferences.</summary>
    public bool PerformanceModeEnabled { get; set; } = false;
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
    /// <summary>Enables foreground-only game detection using the user's explicit process allowlist.</summary>
    public bool GameModeAutoDetectionEnabled { get; set; } = false;
    public List<string> GameModeAllowedProcessNames { get; set; } = new();

    public UserSettings Clone() => new()
    {
        Language = Language,
        ThemeMode = ThemeMode,
        ThemePreset = ThemePreset,
        QuickAskHotkeyPreset = QuickAskHotkeyPreset,
        WeatherLocation = WeatherLocation,
        UseAutomaticLocation = UseAutomaticLocation,
        AutomaticLocationConsentConfirmed = AutomaticLocationConsentConfirmed,
        ProviderBaseUrl = ProviderBaseUrl,
        ModelId = ModelId,
        LocalChatModelBaseUrl = LocalChatModelBaseUrl,
        LocalChatModelId = LocalChatModelId,
        UseLocalAi = UseLocalAi,
        VoiceRepliesEnabled = VoiceRepliesEnabled,
        AudioInputDeviceId = AudioInputDeviceId,
        AudioOutputDeviceId = AudioOutputDeviceId,
        AssistantPersona = AssistantPersona,
        VoiceDeliveryStyleId = VoiceDeliveryStyleId,
        VoiceDesignInstruction = VoiceDesignInstruction,
        VoiceQualitySteps = VoiceQualitySteps,
        VoiceGuidanceScale = VoiceGuidanceScale,
        VoiceSpeed = VoiceSpeed,
        VoiceTextNormalizationEnabled = VoiceTextNormalizationEnabled,
        MascotCharacterDisplayMode = MascotCharacterDisplayMode,
        GmailOAuthClientId = GmailOAuthClientId,
        MascotAlwaysOnTop = MascotAlwaysOnTop,
        MascotAnimationsEnabled = MascotAnimationsEnabled,
        PerformanceModeEnabled = PerformanceModeEnabled,
        MascotUseGlbPreview = MascotUseGlbPreview,
        MascotScale = MascotScale,
        MascotOpacity = MascotOpacity,
        MascotLeft = MascotLeft,
        MascotTop = MascotTop,
        AllowWindowTitleInspection = AllowWindowTitleInspection,
        PrivacyShieldEnabled = PrivacyShieldEnabled,
        ProactiveCheckInEnabled = ProactiveCheckInEnabled,
        ProactiveCheckInIntervalMinutes = ProactiveCheckInIntervalMinutes,
        LastProactiveCheckInAt = LastProactiveCheckInAt,
        GameModeAutoDetectionEnabled = GameModeAutoDetectionEnabled,
        GameModeAllowedProcessNames = new List<string>(GameModeAllowedProcessNames ?? new())
    };

    public void CopyFrom(UserSettings source)
    {
        Language = source.Language;
        ThemeMode = source.ThemeMode;
        ThemePreset = source.ThemePreset;
        QuickAskHotkeyPreset = source.QuickAskHotkeyPreset;
        WeatherLocation = source.WeatherLocation;
        UseAutomaticLocation = source.UseAutomaticLocation;
        AutomaticLocationConsentConfirmed = source.AutomaticLocationConsentConfirmed;
        ProviderBaseUrl = source.ProviderBaseUrl;
        ModelId = source.ModelId;
        LocalChatModelBaseUrl = source.LocalChatModelBaseUrl;
        LocalChatModelId = source.LocalChatModelId;
        UseLocalAi = source.UseLocalAi;
        VoiceRepliesEnabled = source.VoiceRepliesEnabled;
        AudioInputDeviceId = source.AudioInputDeviceId;
        AudioOutputDeviceId = source.AudioOutputDeviceId;
        AssistantPersona = source.AssistantPersona;
        VoiceDeliveryStyleId = source.VoiceDeliveryStyleId;
        VoiceDesignInstruction = source.VoiceDesignInstruction;
        VoiceQualitySteps = source.VoiceQualitySteps;
        VoiceGuidanceScale = source.VoiceGuidanceScale;
        VoiceSpeed = source.VoiceSpeed;
        VoiceTextNormalizationEnabled = source.VoiceTextNormalizationEnabled;
        MascotCharacterDisplayMode = source.MascotCharacterDisplayMode;
        GmailOAuthClientId = source.GmailOAuthClientId;
        MascotAlwaysOnTop = source.MascotAlwaysOnTop;
        MascotAnimationsEnabled = source.MascotAnimationsEnabled;
        PerformanceModeEnabled = source.PerformanceModeEnabled;
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
        GameModeAutoDetectionEnabled = source.GameModeAutoDetectionEnabled;
        GameModeAllowedProcessNames = new List<string>(source.GameModeAllowedProcessNames ?? new());
    }
}
