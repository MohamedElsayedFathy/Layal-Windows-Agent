using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Layal.Services;

/// <summary>Applies canonical workspace path and reparse-point restrictions shared by workspace tools.</summary>
internal sealed class WorkspacePathPolicy
{
    // Windows reserves DOS device names even when an extension is appended. The superscript
    // digits are explicitly recognized by Windows as COM/LPT device digits as well.
    private static readonly Regex ReservedDeviceName =
        new(@"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM(?:[1-9]|[¹²³])|LPT(?:[1-9]|[¹²³]))$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly string _workspaceRoot;
    internal WorkspacePathPolicy(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    internal string ResolvePath(string relativePath, bool allowEmpty = false)
    {
        relativePath ??= string.Empty;
        if (allowEmpty && relativePath.Length == 0) return _workspaceRoot;
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath != relativePath.Trim() || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Use a non-empty relative path without surrounding whitespace inside LayalWorkspace.");
        var parts = relativePath.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) throw new InvalidOperationException("Relative paths cannot contain '.' or '..' path segments.");
        if (parts.Any(IsUnsafeWindowsSegment))
            throw new InvalidOperationException("Path segments contain a Windows-invalid character, end in a space or period, or name a reserved Windows device.");
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("The path escapes the approved workspace.");
        return fullPath;
    }

    private static bool IsUnsafeWindowsSegment(string segment)
    {
        // Win32 silently normalizes trailing ASCII spaces and periods, so accepting them can
        // redirect an apparently distinct workspace path to a different existing file.
        if (segment.EndsWith(' ') || segment.EndsWith('.'))
            return true;

        if (segment.Any(char.IsControl) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return true;

        // Device aliases are recognized before the first extension separator. Trim spaces
        // immediately before that separator too (for example, "NUL .txt").
        var extensionSeparator = segment.IndexOf('.');
        var deviceBaseName = (extensionSeparator < 0 ? segment : segment[..extensionSeparator]).TrimEnd(' ');
        return ReservedDeviceName.IsMatch(deviceBaseName);
    }

    internal void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_workspaceRoot, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The path is outside the workspace.");
        var current = _workspaceRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The workspace root cannot be a symbolic link/reparse point.");
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic links/reparse points are not allowed in this tool path.");
        }
    }
}
