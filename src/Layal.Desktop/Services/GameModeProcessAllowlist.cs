using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Services;

/// <summary>
/// Validates an explicit allowlist of foreground executable process names for optional
/// game-mode detection. It does not enumerate processes or inspect window titles.
/// </summary>
public static class GameModeProcessAllowlist
{
    public const int MaximumEntries = 30;
    public const int MaximumNameCharacters = 64;

    public static bool TryNormalizeName(string? value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^4];

        if (candidate.Length is < 1 or > MaximumNameCharacters ||
            candidate[0] == '.' ||
            candidate[^1] == '.' ||
            candidate.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            return false;

        normalized = candidate;
        return true;
    }

    /// <summary>Normalizes stored preferences, dropping invalid and duplicate entries.</summary>
    public static List<string> NormalizeSettings(IEnumerable<string>? values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? Array.Empty<string>())
        {
            if (!TryNormalizeName(value, out var normalized) || !seen.Add(normalized))
                continue;
            result.Add(normalized);
            if (result.Count >= MaximumEntries)
                break;
        }
        return result;
    }

    /// <summary>Parses user-entered lines/commas and rejects the entire edit if any name is unsafe.</summary>
    public static List<string> ParseAndValidate(string text)
    {
        var tokens = (text ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > MaximumEntries)
            throw new ArgumentException($"Enter no more than {MaximumEntries} game process names.", nameof(text));

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (!TryNormalizeName(token, out var normalized))
                throw new ArgumentException(
                    $"'{Truncate(token, 40)}' is not a valid executable process name. Use letters, numbers, dot, underscore or hyphen only.",
                    nameof(text));
            if (seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
