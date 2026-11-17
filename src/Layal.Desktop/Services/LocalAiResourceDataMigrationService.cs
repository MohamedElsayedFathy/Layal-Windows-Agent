using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

public sealed record LocalAiResourceMigrationSource(string SourceDirectory, string DestinationRelativeDirectory);

public sealed record LocalAiResourceMigrationResult(
    string DestinationPath,
    long CopiedBytes,
    int CopiedFiles);

/// <summary>
/// Copies existing local AI runtimes, caches and (optionally) the Ollama model store into
/// one new resource root. Every file is length- and SHA-256-verified before the root is
/// published. Sources are never deleted or modified.
/// </summary>
public static class LocalAiResourceDataMigrationService
{
    public static Task<LocalAiResourceMigrationResult> MigrateAsync(
        string sourcePath,
        string destinationPath,
        IProgress<(long CopiedBytes, long TotalBytes, string CurrentFile)>? progress = null,
        CancellationToken cancellationToken = default) =>
        MigrateAsync(
            new[] { new LocalAiResourceMigrationSource(sourcePath, ".") },
            destinationPath,
            progress,
            cancellationToken);

    public static async Task<LocalAiResourceMigrationResult> MigrateAsync(
        IReadOnlyList<LocalAiResourceMigrationSource> sources,
        string destinationPath,
        IProgress<(long CopiedBytes, long TotalBytes, string CurrentFile)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destination = Path.GetFullPath(destinationPath);
        var root = Path.GetPathRoot(destination);
        if (string.IsNullOrWhiteSpace(root) || LocalAiResourcePathPolicy.SamePath(destination, root))
            throw new IOException("Choose a resource folder below a local drive root.");

        var normalizedSources = sources.Select(source =>
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceDirectory);
            var sourceDirectory = Path.GetFullPath(source.SourceDirectory);
            var relative = NormalizeRelativeDirectory(source.DestinationRelativeDirectory);
            return new LocalAiResourceMigrationSource(sourceDirectory, relative);
        }).ToArray();

        if (normalizedSources.Select(source => source.DestinationRelativeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedSources.Length)
            throw new IOException("Two resource sources cannot target the same destination directory.");

        for (var i = 0; i < normalizedSources.Length; i++)
        {
            for (var j = i + 1; j < normalizedSources.Length; j++)
            {
                if (PathsOverlap(normalizedSources[i].SourceDirectory, normalizedSources[j].SourceDirectory))
                    throw new IOException("Resource migration source directories must not overlap.");
            }

            if (PathsOverlap(normalizedSources[i].SourceDirectory, destination))
                throw new IOException("The destination cannot overlap an existing resource source.");
        }

        if (Directory.Exists(destination))
        {
            if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("The selected destination folder is not empty. Choose an empty folder.");
            Directory.Delete(destination);
        }
        else if (File.Exists(destination))
        {
            throw new IOException("The selected destination path is an existing file.");
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException("The destination resource folder has no parent directory.");
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoints(parent);

        var files = new List<(string SourcePath, string RelativePath, long Size)>();
        foreach (var source in normalizedSources)
        {
            if (!Directory.Exists(source.SourceDirectory))
                continue;

            foreach (var file in EnumerateSafeFiles(source.SourceDirectory))
            {
                var insideSource = Path.GetRelativePath(source.SourceDirectory, file.FullName);
                var relative = source.DestinationRelativeDirectory == "."
                    ? insideSource
                    : Path.Combine(source.DestinationRelativeDirectory, insideSource);
                files.Add((file.FullName, relative, file.Length));
            }
        }

        var totalBytes = files.Aggregate(0L, (sum, file) => checked(sum + file.Size));
        LocalResourceStorageCapacityGuard.EnsureAvailable(parent, totalBytes);

        var staging = destination + ".layal-migration-" + Guid.NewGuid().ToString("N");
        long copiedBytes = 0;
        var copiedFiles = 0;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetFile = Path.GetFullPath(Path.Combine(staging, file.RelativePath));
                EnsureWithinRoot(staging, targetFile);
                var targetDirectory = Path.GetDirectoryName(targetFile)
                    ?? throw new IOException("A migrated file has no destination directory.");
                Directory.CreateDirectory(targetDirectory);

                await CopyAndVerifyAsync(file.SourcePath, targetFile, cancellationToken).ConfigureAwait(false);
                copiedBytes = checked(copiedBytes + file.Size);
                copiedFiles++;
                progress?.Report((copiedBytes, totalBytes, file.RelativePath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return new LocalAiResourceMigrationResult(destination, copiedBytes, copiedFiles);
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string NormalizeRelativeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A destination-relative directory is required.", nameof(value));
        if (value == ".") return value;
        if (Path.IsPathRooted(value))
            throw new IOException("A migration destination directory must be relative.");
        var segments = value.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new IOException("A migration destination directory contains an unsafe path segment.");
        return Path.Combine(segments);
    }

    private static IEnumerable<FileInfo> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var directoryInfo = new DirectoryInfo(current);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The existing resource tree contains a reparse-point directory; migration was refused.");

            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(path);
                var relative = Path.GetRelativePath(root, path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The existing resource tree contains a reparse-point directory; migration was refused.");

                    // Python installations and virtual environments contain absolute paths.
                    // Re-create these after restart, while retaining the pip cache and model weights.
                    var pythonRuntimeDirectory = "python-runtime";
                    var voiceTutEnvironmentDirectory = Path.Combine("voicetut", "venv");
                    if (relative.Equals(pythonRuntimeDirectory, StringComparison.OrdinalIgnoreCase) ||
                        relative.Equals(voiceTutEnvironmentDirectory, StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith(voiceTutEnvironmentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Push(path);
                }
                else
                {
                    var fileName = Path.GetFileName(relative);
                    if (relative.Equals("resource-state.json", StringComparison.OrdinalIgnoreCase) ||
                        relative.Equals(Path.Combine("voicetut", "voicetut-install.json"), StringComparison.OrdinalIgnoreCase) ||
                        relative.Equals(Path.Combine("voicetut", "python-packages.freeze.txt"), StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals(".layal-server-lease.json", StringComparison.OrdinalIgnoreCase) ||
                        (fileName.StartsWith("resource-state.json.", StringComparison.OrdinalIgnoreCase) &&
                         fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Hugging Face snapshots may use file symlinks to blobs in the same cache.
                    // Copy their final targets as regular bytes, but refuse targets outside this root.
                    // Directory links are never traversed.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var link = new FileInfo(path);
                        var target = link.ResolveLinkTarget(returnFinalTarget: true)
                            ?? throw new IOException("A model cache file has an unresolved symbolic link.");
                        EnsureWithinRoot(root, target.FullName);
                        if (!File.Exists(target.FullName))
                            throw new IOException("A model cache symbolic link target is missing.");
                    }
                    yield return new FileInfo(path);
                }
            }
        }
    }

    private static async Task CopyAndVerifyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        if (!destinationInfo.Exists || sourceInfo.Length != destinationInfo.Length)
            throw new IOException("A copied resource file failed its length verification.");

        var sourceHash = await HashFileAsync(source, cancellationToken).ConfigureAwait(false);
        var destinationHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            throw new IOException("A copied resource file failed SHA-256 verification: " + Path.GetFileName(source));

        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new IOException("A resource parent has no volume root.");
        var current = root;
        foreach (var segment in full[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A resource destination parent contains a reparse point.");
        }
    }

    private static void EnsureWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A migration file escaped the staging directory.");
    }

    private static bool PathsOverlap(string first, string second)
    {
        var a = WithSeparator(Path.GetFullPath(first));
        var b = WithSeparator(Path.GetFullPath(second));
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}