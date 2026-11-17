using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Verifies a set of pinned files whose snapshots may use Hugging Face cache symlinks.
/// File links are allowed only when their final targets remain inside the approved cache root.
/// </summary>
public static class LocalResourceFileIntegrityVerifier
{
    public static async Task VerifyAsync(
        string snapshotDirectory,
        string allowedCacheRoot,
        IReadOnlyList<LocalResourceFilePin> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedCacheRoot);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("At least one pinned file is required.", nameof(files));

        var allowedRoot = ResolveExistingDirectory(allowedCacheRoot);
        var snapshotRoot = Path.GetFullPath(snapshotDirectory);
        EnsureWithinRoot(allowedRoot, snapshotRoot, "The model snapshot is outside the approved cache root.");
        EnsureDirectoryChainHasNoReparsePoints(allowedRoot, snapshotRoot);

        foreach (var pin in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(pin.RelativePath) ||
                Path.IsPathRooted(pin.RelativePath) ||
                pin.RelativePath.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or ".."))
                throw new InvalidDataException("A pinned model file path is not a safe relative path.");
            if (pin.ExpectedSizeBytes <= 0 || !pin.HasPinnedSha256)
                throw new InvalidDataException("A pinned model file has an invalid size or SHA-256.");

            var candidatePath = Path.GetFullPath(Path.Combine(snapshotRoot, pin.RelativePath));
            EnsureWithinRoot(snapshotRoot, candidatePath, "A pinned model file escapes its snapshot directory.");

            var candidate = new FileInfo(candidatePath);
            if (!candidate.Exists)
                throw new FileNotFoundException("A required pinned model file is missing: " + pin.RelativePath, candidatePath);

            string actualPath;
            if ((candidate.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = candidate.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new InvalidDataException("A pinned model file has an unresolved reparse point: " + pin.RelativePath);
                actualPath = Path.GetFullPath(target.FullName);
            }
            else
            {
                actualPath = candidate.FullName;
            }

            EnsureWithinRoot(allowedRoot, actualPath, "A pinned model file resolves outside the approved Hugging Face cache.");
            var actualFile = new FileInfo(actualPath);
            if (!actualFile.Exists || actualFile.Length != pin.ExpectedSizeBytes)
                throw new InvalidDataException("A pinned model file has an unexpected size: " + pin.RelativePath);

            await using var stream = new FileStream(
                actualPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var expectedHash = Convert.FromHexString(pin.ExpectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new InvalidDataException("A pinned model file failed SHA-256 verification: " + pin.RelativePath);
        }
    }

    private static string ResolveExistingDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
            throw new DirectoryNotFoundException("The approved model cache directory does not exist.");
        var target = directory.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(target?.FullName ?? directory.FullName);
    }

    private static void EnsureDirectoryChainHasNoReparsePoints(string root, string targetDirectory)
    {
        EnsureWithinRoot(root, targetDirectory, "The model snapshot is outside the approved cache root.");
        var relative = Path.GetRelativePath(root, targetDirectory);
        var current = root;
        if (relative == ".") return;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists)
                throw new DirectoryNotFoundException("The pinned model snapshot directory is missing.");
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A model snapshot directory must not be a reparse point.");
        }
    }

    private static void EnsureWithinRoot(string root, string candidate, string error)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(error);
    }
}
