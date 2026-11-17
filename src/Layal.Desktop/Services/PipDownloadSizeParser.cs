using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Layal.Services;

/// <summary>
/// Parses the explicit archive-size hints emitted by pip before a wheel/sdist download.
/// Units in pip's display are decimal (kB/MB/GB); the capacity guard reserves the
/// rounded-up number of bytes on the volume hosting the Layal-managed pip cache/temp.
/// </summary>
public static class PipDownloadSizeParser
{
    private static readonly Regex SizePattern = new(
        @"\((?<value>\d+(?:[.,]\d+)?)\s*(?<unit>bytes?|B|kB|KB|MB|GB)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProgressPattern = new(
        @"(?<downloaded>\d+(?:[.,]\d+)?)\s*/\s*(?<total>\d+(?:[.,]\d+)?)\s*(?<unit>bytes?|B|kB|KB|MB|GB)\s+(?<speed>\d+(?:[.,]\d+)?)\s*(?<speedUnit>bytes?|B|kB|KB|MB|GB)/s(?:\s+eta)?(?:\s+(?<eta>\d+:\d{2}(?::\d{2})?))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParseProgressLine(string? outputLine, out PipDownloadProgress progress)
    {
        progress = default!;
        if (string.IsNullOrWhiteSpace(outputLine))
            return false;

        var match = ProgressPattern.Match(outputLine);
        if (!match.Success)
            return false;

        if (!TryParseDecimal(match.Groups["downloaded"].Value, out var downloadedValue) ||
            !TryParseDecimal(match.Groups["total"].Value, out var totalValue) ||
            !TryParseDecimal(match.Groups["speed"].Value, out var speedValue) ||
            downloadedValue < 0 || totalValue <= 0 || speedValue < 0)
            return false;

        if (!TryConvertBytes(downloadedValue, match.Groups["unit"].Value, allowZero: true, out var downloaded) ||
            !TryConvertBytes(totalValue, match.Groups["unit"].Value, allowZero: false, out var total) ||
            !TryConvertBytes(speedValue, match.Groups["speedUnit"].Value, allowZero: true, out var speedBytes))
            return false;

        var speed = (double)speedBytes;
        if (total <= 0 || downloaded > total || !double.IsFinite(speed))
            return false;

        double? remainingSeconds = null;
        if (match.Groups["eta"].Success &&
            TryParseEta(match.Groups["eta"].Value, out var eta) &&
            eta >= TimeSpan.Zero && eta.TotalSeconds <= 365d * 24 * 60 * 60)
            remainingSeconds = eta.TotalSeconds;
        else if (speed > 0)
            remainingSeconds = (total - downloaded) / speed;

        progress = new PipDownloadProgress(
            downloaded,
            total,
            speed,
            remainingSeconds);
        return true;
    }

    private static bool TryConvertBytes(
        decimal value,
        string unit,
        bool allowZero,
        out long bytes)
    {
        bytes = 0;
        try
        {
            var converted = decimal.Ceiling(value * GetMultiplier(unit));
            if (converted < 0 || (!allowZero && converted == 0) || converted > long.MaxValue)
                return false;
            bytes = decimal.ToInt64(converted);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseEta(string value, out TimeSpan eta)
    {
        eta = default;
        var parts = value.Split(':');
        if (parts.Length is not (2 or 3))
            return false;

        if (!int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or > 59)
            return false;

        int minutes;
        int hours = 0;
        if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
            minutes < 0 || (parts.Length == 3 && minutes > 59))
            return false;
        if (parts.Length == 3 &&
            (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
             hours < 0))
            return false;

        try
        {
            eta = TimeSpan.FromSeconds(checked(hours * 3600L + minutes * 60L + seconds));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseDecimal(string value, out decimal parsed) =>
        decimal.TryParse(
            value.Replace(',', '.'),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out parsed);

    private static decimal GetMultiplier(string unit)
    {
        if (unit.Equals("GB", StringComparison.OrdinalIgnoreCase)) return 1_000_000_000m;
        if (unit.Equals("MB", StringComparison.OrdinalIgnoreCase)) return 1_000_000m;
        if (unit.Equals("KB", StringComparison.OrdinalIgnoreCase) ||
            unit.Equals("kB", StringComparison.OrdinalIgnoreCase)) return 1_000m;
        return 1m;
    }

    public static bool TryParseAnnouncedBytes(string? outputLine, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(outputLine))
            return false;

        var match = SizePattern.Match(outputLine);
        if (!match.Success)
            return false;

        var numberText = match.Groups["value"].Value.Replace(',', '.');
        if (!decimal.TryParse(numberText, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;

        return TryConvertBytes(value, match.Groups["unit"].Value, allowZero: false, out bytes);
    }
}
