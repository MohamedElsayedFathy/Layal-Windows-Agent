using System.Text.RegularExpressions;

namespace Layal.Services;

/// <summary>
/// Isolates Latin/number runs inside RTL labels so API names and English button text
/// stay in logical reading order while the surrounding Arabic UI remains RTL.
/// </summary>
public static class BidiTextNormalizer
{
    private const char LeftToRightIsolate = '\u2066';
    private const char PopDirectionalIsolate = '\u2069';

    private static readonly Regex LatinRunPattern = new(
        @"[A-Za-z0-9]+(?:[._:/+-][A-Za-z0-9]+)*(?:[ ]+[A-Za-z0-9]+(?:[._:/+-][A-Za-z0-9]+)*)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizeMixedText(string? value)
    {
        var clean = StripDirectionalControls(value);
        if (clean.Length == 0)
            return string.Empty;

        return LatinRunPattern.Replace(clean, match =>
            string.Concat(LeftToRightIsolate, match.Value, PopDirectionalIsolate));
    }

    /// <summary>Removes invisible bidi controls before copying displayed text to the clipboard.</summary>
    public static string StripDirectionalControls(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace(LeftToRightIsolate.ToString(), string.Empty)
            .Replace(PopDirectionalIsolate.ToString(), string.Empty)
            .Replace('\u200E'.ToString(), string.Empty)
            .Replace('\u200F'.ToString(), string.Empty);
    }
}
