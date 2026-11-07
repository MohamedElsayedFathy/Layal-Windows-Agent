using System;

namespace Layal.Services;

/// <summary>
/// Named delivery styles are presentation-only. They never alter assistant identity,
/// safety rules, tool permissions, approval requirements, or the selected voice.
/// </summary>
public static class VoiceDeliveryStylePolicy
{
    public const string DefaultStyle = "natural";

    public static string Normalize(string? styleId) =>
        styleId?.Trim().ToLowerInvariant() switch
        {
            "natural" => "natural",
            "gentle" => "gentle",
            "bright" => "bright",
            "clear" => "clear",
            _ => DefaultStyle
        };

    public static bool IsSupported(string? styleId) =>
        styleId is not null &&
        styleId.Trim().ToLowerInvariant() is "natural" or "gentle" or "bright" or "clear";

    public static string BuildRealtimeInstruction(string? styleId) =>
        Normalize(styleId) switch
        {
            "gentle" => "Delivery style: gentle and calm. Use a slightly slower, relaxed pace and soft, natural emphasis. Keep speech clear and conversational; do not whisper or become overly breathy.",
            "bright" => "Delivery style: bright and lightly upbeat. Use a slightly brisker pace and lively but restrained emphasis. Do not sound excited when the text is serious, and avoid exaggerated or childish acting.",
            "clear" => "Delivery style: clear and measured. Articulate words carefully, keep a steady moderate pace, and use deliberate short pauses at sentence boundaries without sounding formal or robotic.",
            _ => "Delivery style: natural and conversational. Use balanced pacing, connected speech, and emphasis that follows the meaning without exaggerated performance."
        };

    /// <summary>
    /// Piper's bundled voice models have fixed timbre and do not expose semantic emotion controls.
    /// Local delivery therefore changes pace only; voice identity and language-specific model stay fixed.
    /// </summary>
    public static double GetLocalLengthScale(string? styleId) =>
        Normalize(styleId) switch
        {
            "gentle" => 1.10,
            "bright" => 0.94,
            "clear" => 1.03,
            _ => 1.00
        };

    public static string GetDisplayName(string? styleId, bool arabic) =>
        Normalize(styleId) switch
        {
            "gentle" => arabic ? "هادئة ولطيفة" : "Gentle",
            "bright" => arabic ? "مشرقة وحيوية باعتدال" : "Bright",
            "clear" => arabic ? "واضحة ومتزنة" : "Clear",
            _ => arabic ? "طبيعية وعفوية" : "Natural"
        };
}
