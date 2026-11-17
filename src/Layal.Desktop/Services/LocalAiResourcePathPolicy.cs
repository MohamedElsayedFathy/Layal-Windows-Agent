using System;
using System.IO;

namespace Layal.Services;

public static class LocalAiResourcePathPolicy
{
    public static string Resolve(string? configuredPath, string defaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultPath);
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(defaultPath);

        var configuredValue = configuredPath.Trim();
        if (!Path.IsPathFullyQualified(configuredValue))
            throw new IOException("The saved AI resource folder must be an absolute local path.");

        var configured = Path.GetFullPath(configuredValue);
        var fallback = Path.GetFullPath(defaultPath);
        if (SamePath(configured, fallback))
            return configured;
        try
        {
            return Validate(configured, fallback);
        }
        catch (ArgumentException exception)
        {
            throw new IOException("The saved AI resource folder is invalid: " + exception.Message, exception);
        }
    }

    public static string Validate(string selectedPath, string existingResourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingResourcePath);
        var selected = selectedPath.Trim();
        if (!Path.IsPathFullyQualified(selected))
            throw new ArgumentException("The resource folder must be an absolute local path.", nameof(selectedPath));

        var full = Path.GetFullPath(selected);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) ||
            SamePath(full, root))
            throw new ArgumentException("Choose a folder below the drive root, not the drive root itself.", nameof(selectedPath));
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Network/UNC locations are not supported for local AI resources.", nameof(selectedPath));

        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("The selected resource volume is not a ready local fixed or removable drive.");
        if (PathsOverlap(full, Path.GetFullPath(existingResourcePath)) &&
            !SamePath(full, existingResourcePath))
            throw new ArgumentException("The new resource folder must not overlap the existing Layal resource folder.", nameof(selectedPath));

        EnsureNoExistingReparsePoints(full);
        return full;
    }

    public static string ValidateLocalDirectory(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var selected = selectedPath.Trim();
        if (!Path.IsPathFullyQualified(selected))
            throw new ArgumentException("The resource folder must be an absolute local path.", nameof(selectedPath));

        var full = Path.GetFullPath(selected);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) ||
            SamePath(full, root))
            throw new ArgumentException("Choose a folder below the drive root, not the drive root itself.", nameof(selectedPath));
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Network/UNC locations are not supported for local AI resources.", nameof(selectedPath));

        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("The selected resource volume is not a ready local fixed or removable drive.");

        EnsureNoExistingReparsePoints(full);
        return full;
    }

    public static bool SamePath(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static bool PathsOverlap(string first, string second)
    {
        var a = WithTrailingSeparator(Path.GetFullPath(first));
        var b = WithTrailingSeparator(Path.GetFullPath(second));
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoExistingReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new IOException("The selected resource folder has no volume root.");
        var current = root;
        foreach (var segment in full[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The selected resource path contains a symbolic link or reparse point.");
        }
    }

    private static string WithTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}