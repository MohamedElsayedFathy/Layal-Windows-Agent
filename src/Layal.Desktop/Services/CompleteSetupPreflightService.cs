using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

public sealed record CompleteSetupPreflight(
    bool IsSupported,
    string PlatformDescription,
    string CpuDescription,
    string MemoryDescription,
    string GpuDescription,
    string StorageDescription,
    long? AvailableBytes,
    string? Warning);

public sealed record CompleteSetupConnectivityEndpoint(
    string Name,
    bool Reachable,
    string Detail);

public sealed record CompleteSetupConnectivityReport(
    bool HasAnyReachableSource,
    IReadOnlyList<CompleteSetupConnectivityEndpoint> Endpoints);

/// <summary>
/// Non-mutating hardware, OS, storage and official-source preflight for the first-run wizard.
/// Reported component sizes are references only; this service never invents an aggregate.
/// </summary>
public static class CompleteSetupPreflightService
{
    private static readonly (string Name, string Url)[] ConnectivityTargets =
    {
        ("GitHub", "https://github.com/"),
        ("Python", "https://www.python.org/"),
        ("PyPI", "https://pypi.org/"),
        ("PyTorch", "https://download.pytorch.org/"),
        ("Hugging Face", "https://huggingface.co/"),
        ("Ollama", "https://ollama.com/"),
        ("Ollama registry", "https://registry.ollama.ai/")
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static CompleteSetupPreflight Inspect(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var isSupportedWindows = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        var isX64 = Environment.Is64BitOperatingSystem &&
                    RuntimeInformation.OSArchitecture == Architecture.X64;
        var platform = RuntimeInformation.OSDescription + " | " +
                       RuntimeInformation.OSArchitecture + " | " +
                       (Environment.Is64BitOperatingSystem ? "64-bit OS" : "32-bit OS");
        var cpu = Environment.ProcessorCount.ToString(
            System.Globalization.CultureInfo.InvariantCulture) +
            " logical processor(s) | " + RuntimeInformation.ProcessArchitecture;
        var memory = InspectPhysicalMemory();
        var gpu = InspectVideoControllers();

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
            storage = drive.Name + " — " + FormatBytes(availableBytes.Value) + " free";
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
            cpu,
            memory,
            gpu,
            storage,
            availableBytes,
            warning);
    }

    public static async Task<CompleteSetupConnectivityReport> InspectConnectivityAsync(
        CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            MaxResponseHeadersLength = 16
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));

        var checks = await Task.WhenAll(ConnectivityTargets.Select(async target =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, target.Url);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                return new CompleteSetupConnectivityEndpoint(
                    target.Name,
                    true,
                    ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CompleteSetupConnectivityEndpoint(target.Name, false, "timeout");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidOperationException or
                    System.Security.SecurityException or UriFormatException)
            {
                return new CompleteSetupConnectivityEndpoint(
                    target.Name,
                    false,
                    exception.GetType().Name);
            }
        })).ConfigureAwait(false);

        return new CompleteSetupConnectivityReport(checks.Any(item => item.Reachable), checks);
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

    private static string InspectPhysicalMemory()
    {
        if (!OperatingSystem.IsWindows())
            return "Physical RAM details are available only on Windows.";

        try
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };
            if (!GlobalMemoryStatusEx(ref status))
                return "Physical RAM details could not be read.";

            return FormatBytes(checked((long)status.TotalPhysical)) + " total | " +
                   FormatBytes(checked((long)status.AvailablePhysical)) + " currently available";
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return "Physical RAM details unavailable: " + exception.GetType().Name;
        }
    }

    private static string InspectVideoControllers()
    {
        if (!OperatingSystem.IsWindows())
            return "Video adapter details are available only on Windows.";

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(
            systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return "Video adapter name and driver version could not be queried.";

        const string script =
            "$ErrorActionPreference='Stop'; " +
            "Get-CimInstance -ClassName Win32_VideoController | " +
            "ForEach-Object { Write-Output ($_.Name + ' | driver ' + $_.DriverVersion) }";

        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                return "Video adapter name and driver version could not be queried.";

            if (!process.WaitForExit(4000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "Video adapter query timed out; GPU acceleration will be selected by runtime checks.";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(output)
                ? "No video adapter details were returned; GPU acceleration will be selected by runtime checks."
                : output.Length <= 700 ? output : output[..700] + "…";
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                IOException or UnauthorizedAccessException)
        {
            return "Video adapter details unavailable: " + exception.GetType().Name;
        }
    }
}
