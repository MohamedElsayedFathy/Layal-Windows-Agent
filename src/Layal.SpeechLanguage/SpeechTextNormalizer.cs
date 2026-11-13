using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Layal.SpeechLanguage;

public static class SpeechTextNormalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Keeps the speaker's visible words and punctuation. Only control and directional marks
    /// are removed; this result is suitable for display and is not a translation.
    /// </summary>
    public static string NormalizeTranscript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var source = text.Normalize(NormalizationForm.FormC);
        var output = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (IsDirectionalControl(character) || character == '\0')
                continue;

            if (char.IsControl(character))
            {
                if (character is '\r' or '\n' or '\t')
                    output.Append(' ');
                continue;
            }

            output.Append(character);
        }

        return Whitespace.Replace(output.ToString(), " ").Trim();
    }

    /// <summary>
    /// Search-only normalization. Never use this as the user's visible transcript.
    /// </summary>
    public static string NormalizeForLookup(string? text)
    {
        var source = NormalizeTranscript(text).Normalize(NormalizationForm.FormKD);
        var output = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;

            switch (character)
            {
                case '\u0640':
                    continue;
                case '\u0622':
                case '\u0623':
                case '\u0625':
                case '\u0671':
                    output.Append('ا');
                    break;
                case '\u0649':
                case '\u0626':
                    output.Append('ي');
                    break;
                case '\u0624':
                    output.Append('و');
                    break;
                default:
                    output.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
                    break;
            }
        }

        return Whitespace.Replace(output.ToString(), " ").Trim();
    }

    private static bool IsDirectionalControl(char value) =>
        value is '\u061C' or '\u200E' or '\u200F' or
            >= '\u202A' and <= '\u202E' or
            >= '\u2066' and <= '\u2069';
}
