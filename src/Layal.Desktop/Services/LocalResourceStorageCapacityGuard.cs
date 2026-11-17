using System;
using System.IO;

namespace Layal.Services;

public sealed class InsufficientStorageException : IOException
{
    public InsufficientStorageException(
        string directory,
        long requiredBytes,
        long availableBytes)
        : base(
            "Insufficient free space at '" + directory + "'. Required at least " +
            CompleteSetupPreflightService.FormatBytes(requiredBytes) + "; available " +
            CompleteSetupPreflightService.FormatBytes(availableBytes) + ".")
    {
        DirectoryPath = Path.GetFullPath(directory);
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    public string DirectoryPath { get; }
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
}

/// <summary>
/// Checks the volume that will actually receive a resource. Call this immediately before
/// a large transfer using the server-reported remaining byte count, not a guessed total.
/// </summary>
public static class LocalResourceStorageCapacityGuard
{
    public static long GetAvailableBytes(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new IOException("The resource destination has no volume root.");

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
            throw new IOException("The resource destination volume is not ready: " + drive.Name);
        return drive.AvailableFreeSpace;
    }

    public static void EnsureAvailable(
        string directory,
        long remainingDownloadBytes,
        long additionalTemporaryBytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (remainingDownloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(remainingDownloadBytes));
        if (additionalTemporaryBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalTemporaryBytes));

        var required = checked(remainingDownloadBytes + additionalTemporaryBytes);
        var available = GetAvailableBytes(directory);
        if (available < required)
            throw new InsufficientStorageException(directory, required, available);
    }
}
