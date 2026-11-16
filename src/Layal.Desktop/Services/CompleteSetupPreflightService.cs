using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Layal.Services;

public sealed record CompleteSetupPreflight(
    bool IsSupported,
    string PlatformDescription,
    string StorageDescription,
    long? AvailableBytes,
    string? Warning);

/// <summary>
/// Performs a non-mutating first-run platform and destination-volume check.
/// It deliberately reports available storage rather than inventing a total size:
/// some model and package sources do not publish reliable sizes before download.
/// </summary>
public static class CompleteSetupPreflightService
{
    public static CompleteSetupPreflight Inspect(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var isSupportedWindows = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        var isX64 = Environment.Is64BitOperatingSystem &&
                    RuntimeInformation.OSArchitecture == Architecture.X64;
        var platform = RuntimeInformation.OSDescription + " | " +
                       RuntimeInformation.OSArchitecture + " | " +
                       (Environment.Is64BitOperatingSystem ? "64-bit OS" : "32-bit OS");

        long? availableBytes = null;
        var storage = "Storage capacity could not be inspected.";
        string? warning = null;

        try
        {
            var fullPath = Path.GetFullPath(dataRoot);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("The Layal data directory has no volume root.");

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                throw new IOException("The volume containing the Layal data directory is not ready.");

            availableBytes = drive.AvailableFreeSpace;
            storage = drive.Name + " — " +
                      FormatBytes(availableBytes.Value) + " free";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                System.Security.SecurityException or NotSupportedException)
        {
            warning = exception.Message;
        }

        if (!isSupportedWindows || !isX64)
        {
            warning = "Complete local setup requires Windows 10 version 2004 (build 19041) or later on x64.";
        }

        return new CompleteSetupPreflight(
            isSupportedWindows && isX64,
            platform,
            storage,
            availableBytes,
            warning);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString(index == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture)
               + " " + units[index];
    }
}
