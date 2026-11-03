using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace Layal.Services;

/// <summary>
/// Resolves and removes only regular files explicitly linked to tasks inside LayalWorkspace.
/// Files are sent to the current user's Recycle Bin rather than permanently deleted.
/// </summary>
public sealed class WorkspaceArtifactService
{
    private readonly string _workspaceRoot;
    private readonly string _rootPrefix;

    public WorkspaceArtifactService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _rootPrefix = _workspaceRoot + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Resolves a canonical workspace-relative file after rejecting traversal and every reparse-point
    /// component. Directories are not accepted as task output artifacts.
    /// </summary>
    public string ResolveExistingSafeFilePath(string relativePath)
    {
        var normalized = TaskHistoryService.NormalizeArtifactPath(relativePath);
        if (!string.Equals(normalized, (relativePath ?? string.Empty).Trim().Replace('\\', '/'), StringComparison.Ordinal))
            throw new ArgumentException("Artifact path must be canonical before it can be removed.", nameof(relativePath));

        EnsureDirectoryIsNotReparsePoint(_workspaceRoot);
        var segments = normalized.Split('/');
        var current = _workspaceRoot;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.GetFullPath(Path.Combine(current, segments[index]));
            if (!current.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected artifact resolves outside LayalWorkspace.");

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Task output deletion is blocked for paths that contain symbolic links or other reparse points.");

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (index < segments.Length - 1 && !isDirectory)
                throw new InvalidOperationException("An artifact path contains a non-directory parent component.");
            if (index == segments.Length - 1 && isDirectory)
                throw new InvalidOperationException("Only an individual regular file can be removed as a task artifact.");
        }

        if (!File.Exists(current))
            throw new FileNotFoundException("The linked task artifact no longer exists.", normalized);
        return current;
    }

    /// <summary>Re-resolves the path immediately before asking Windows to move it to the Recycle Bin.</summary>
    public void SendToRecycleBin(string relativePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sending workspace artifacts to the Windows Recycle Bin is available only on Windows.");

        var path = ResolveExistingSafeFilePath(relativePath);
        // Recheck the final file and its parent chain immediately before the destructive filesystem call.
        var confirmedPath = ResolveExistingSafeFilePath(relativePath);
        if (!string.Equals(path, confirmedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected artifact path changed before deletion. No file was removed.");

        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        if (File.Exists(path))
            throw new IOException("Windows did not move the selected artifact to the Recycle Bin.");
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The Layal workspace directory does not exist.");

        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The Layal workspace directory has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                throw new DirectoryNotFoundException("A Layal workspace parent directory does not exist.");
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException(
                    "The Layal workspace path contains a symbolic link, reparse point, or non-directory parent.");
        }
    }
}
