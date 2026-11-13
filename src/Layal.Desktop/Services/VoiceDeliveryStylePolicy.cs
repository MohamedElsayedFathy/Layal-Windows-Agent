using System;

namespace Layal.Services;

/// <summary>
/// Named delivery styles are presentation-only. They never alter assistant identity,
/// safety rules, tool permissions, approval requirements, or the selected voice.
/// </summary>
public static class VoiceDeliveryStylePolicy
{
    public const string DefaultStyle = "natural";

    private static readonly string[] SupportedStyles =
    [
        "natural", "gentle", "bright", "clear",
        "empathetic", "serious", "energetic", "playful"
    ];

    public static string Normalize(string? styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId))
            return DefaultStyle;

        var normalized = styleId.Trim().ToLowerInvariant();
        return Array.IndexOf(SupportedStyles, normalized) >= 0 ? normalized : DefaultStyle;
    }

    public static bool IsSupported(string? styleId) =>
        !string.IsNullOrWhiteSpace(styleId) &&
        Array.IndexOf(SupportedStyles, styleId.Trim().ToLowerInvariant()) >= 0;

    public static string BuildRealtimeInstruction(string? styleId) =>
        Normalize(styleId) switch
        {
            "gentle" => "Delivery style: soft, gentle and calm. Use a slightly slower, relaxed pace and soft, natural emphasis. Keep speech clear and conversational; do not whisper or become overly breathy.",
            "bright" => "Delivery style: bright and lightly upbeat. Use a slightly brisker pace and lively but restrained emphasis. Avoid exaggerated or childish acting.",
            "clear" => "Delivery style: clear and measured. Articulate words carefully, keep a steady moderate pace, and use deliberate short pauses at sentence boundaries without sounding formal or robotic.",
            "empathetic" => "Delivery style: empathetic and reassuring. Use a calm, unhurried pace, gentle emphasis and slightly longer pauses. Do not exaggerate emotion.",
            "serious" => "Delivery style: serious and composed. Keep a measured pace, steady articulation and deliberate pauses; avoid playful emphasis.",
            "energetic" => "Delivery style: energetic and encouraging. Use a brisk but intelligible pace, light emphasis and short pauses; avoid shouting.",
            "playful" => "Delivery style: playful and light. Use a gently lively rhythm and varied emphasis while staying natural and never childish or exaggerated.",
            _ => "Delivery style: natural and conversational. Use balanced pacing, connected speech, and emphasis that follows the meaning without exaggerated performance."
        };

    /// <summary>
    /// Piper's bundled voice models have fixed timbre and do not expose semantic emotion controls.
    /// These local presets adjust phoneme pace only; sentence pauses are controlled separately.
    /// </summary>
    public static double GetLocalLengthScale(string? styleId) =>
        Normalize(styleId) switch
        {
            "gentle" => 1.10,
            "bright" => 0.95,
            "clear" => 1.03,
            "empathetic" => 1.08,
            "serious" => 1.05,
            "energetic" => 0.92,
            "playful" => 0.97,
            _ => 1.00
        };

    /// <summary>
    /// Seconds of silence after each sentence. This is pacing, not emotional voice synthesis.
    /// Values are deliberately conservative to avoid breaking conversational turn-taking.
    /// </summary>
    public static double GetLocalSentenceSilenceSeconds(string? styleId) =>
        Normalize(styleId) switch
        {
            "gentle" => 0.36,
            "bright" => 0.16,
            "clear" => 0.28,
            "empathetic" => 0.40,
            "serious" => 0.34,
            "energetic" => 0.12,
            "playful" => 0.14,
            _ => 0.20
        };

    public static string GetDisplayName(string? styleId, bool arabic) =>
        Normalize(styleId) switch
        {
            "gentle" => arabic ? "هادئة وناعمة" : "Soft and gentle",
            "bright" => arabic ? "مبهجة بخفة" : "Bright",
            "clear" => arabic ? "واضحة ومتزنة" : "Clear and measured",
            "empathetic" => arabic ? "متعاطفة ومطمئنة" : "Empathetic",
            "serious" => arabic ? "جادة ورصينة" : "Serious",
            "energetic" => arabic ? "نشيطة وحيوية" : "Energetic",
            "playful" => arabic ? "مرحة وخفيفة" : "Playful",
            _ => arabic ? "طبيعية وعفوية" : "Natural"
        };
}
