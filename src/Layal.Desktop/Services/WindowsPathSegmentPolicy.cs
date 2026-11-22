using System;
using System.Linq;

namespace Layal.Services;

/// <summary>
/// Shared Windows filename-segment policy for every path that can create, restore, upload,
/// download, or import user-controlled files.
/// </summary>
internal static class WindowsPathSegmentPolicy
{
    private static readonly char[] InvalidWindowsCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    internal static bool IsSafeSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > 255 ||
            segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.Any(char.IsControl) ||
            segment.IndexOfAny(InvalidWindowsCharacters) >= 0)
            return false;

        return !IsReservedDeviceName(segment);
    }

    internal static bool IsReservedDeviceName(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // DOS device names remain reserved with extensions. Windows also treats the
        // superscript 1, 2 and 3 characters as device digits for COM and LPT names.
        var extensionSeparator = value.IndexOf('.');
        var stem = (extensionSeparator < 0 ? value : value[..extensionSeparator])
            .TrimEnd(' ', '.');

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsNumberedDeviceName(stem, "COM") || IsNumberedDeviceName(stem, "LPT");
    }

    private static bool IsNumberedDeviceName(string value, string prefix)
    {
        if (value.Length != prefix.Length + 1 ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return value[^1] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
