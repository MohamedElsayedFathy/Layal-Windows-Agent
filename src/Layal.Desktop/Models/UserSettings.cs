namespace Layal.Models;

public sealed class UserSettings
{
    public string Language { get; set; } = "ar";
    public string ThemeMode { get; set; } = "light";
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
}
