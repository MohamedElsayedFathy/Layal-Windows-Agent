using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Layal.Services;

/// <summary>Applies canonical workspace path and reparse-point restrictions shared by workspace tools.</summary>
internal sealed class WorkspacePathPolicy
{
    private static readonly Regex ReservedDeviceName =
        new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly string _workspaceRoot;
    internal WorkspacePathPolicy(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    internal string ResolvePath(string relativePath, bool allowEmpty = false)
    {
        relativePath = (relativePath ?? string.Empty).Trim();
        if (allowEmpty && relativePath.Length == 0) return _workspaceRoot;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Use a non-empty relative path inside LayalWorkspace.");
        var parts = relativePath.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) throw new InvalidOperationException("Relative paths cannot contain '.' or '..' path segments.");
        // Alternate data streams ("file.txt:hidden") and Windows device names (NUL, CON, COM1...) are never valid workspace targets.
        if (parts.Any(p => p.Contains(':') || ReservedDeviceName.IsMatch(p)))
            throw new InvalidOperationException("Path segments cannot contain ':' or use reserved Windows device names.");
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("The path escapes the approved workspace.");
        return fullPath;
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
