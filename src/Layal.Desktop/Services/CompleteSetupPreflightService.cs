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
    string? Warning,
    long? TotalPhysicalMemoryBytes = null,
    long? AvailablePhysicalMemoryBytes = null,
    long? TotalDedicatedVideoMemoryBytes = null,
    long? AvailableDedicatedVideoMemoryBytes = null,
    string GpuMemorySource = "unknown");

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
        ("GitHub source", "https://github.com/"),
        ("GitHub archives", "https://codeload.github.com/"),
        ("GitHub release assets", "https://release-assets.githubusercontent.com/"),
        ("GitHub objects", "https://objects.githubusercontent.com/"),
        ("Python", "https://www.python.org/"),
        ("PyPI index", "https://pypi.org/"),
        ("Python package files", "https://files.pythonhosted.org/"),
        ("PyTorch wheels", "https://download.pytorch.org/"),
        ("Hugging Face Hub", "https://huggingface.co/"),
        ("Hugging Face LFS", "https://cdn-lfs.huggingface.co/"),
        ("Hugging Face Xet", "https://cas-bridge.xethub.hf.co/"),
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
        var processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        var cpu = (string.IsNullOrWhiteSpace(processorIdentifier)
                ? "CPU model unavailable"
                : processorIdentifier.Trim()) +
            " | " + Environment.ProcessorCount.ToString(
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

        var ollamaModelsPath = OllamaLocalModelSetupService.ModelsDirectory;
        var ollamaStorageLabel = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_MODELS")) &&
                                 string.IsNullOrWhiteSpace(Layal.AppRuntime.Settings.LocalAiDataDirectory) &&
                                 string.IsNullOrWhiteSpace(Layal.AppRuntime.Settings.OllamaModelsDirectory)
            ? "Ollama default model volume"
            : "Ollama configured model volume";

        storage += Environment.NewLine +
                   DescribeVolume(ollamaStorageLabel, ollamaModelsPath);
        storage += Environment.NewLine +
                   DescribeVolume(
                       "Ollama runtime install volume",
                       OllamaLocalModelSetupService.RuntimeCapacityDirectory);
        storage += Environment.NewLine +
                   DescribeVolume(
                       "Layal Python pip cache and build temporary-file volume",
                       Path.Combine(LocalVoiceService.DataRoot, "temp"));
        storage += Environment.NewLine +
                   DescribeVolume("Windows temporary-file volume", Path.GetTempPath());
        storage += Environment.NewLine +
                   "Layal redirects Python pip cache and TEMP/TMP into the selected AI resource volume. Ollama uses its resolved model-directory setting.";

        if (!isSupportedWindows || !isX64)
        {
            warning = "Complete local setup requires Windows 10 version 2004 (build 19041) or later on x64.";
        }

        return new CompleteSetupPreflight(
            isSupportedWindows && isX64,
            platform,
            cpu,
            memory.Description,
            gpu.Description,
            storage,
            availableBytes,
            warning,
            memory.TotalBytes,
            memory.AvailableBytes,
            gpu.TotalDedicatedBytes,
            gpu.AvailableDedicatedBytes,
            gpu.MemorySource);
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

    private static string DescribeVolume(string label, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return label + ": path is not configured.";

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return label + ": volume root could not be determined.";

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return label + ": " + drive.Name + " is not ready.";

            return label + ": " + drive.Name + " — " +
                   FormatBytes(drive.AvailableFreeSpace) + " free";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                System.Security.SecurityException or NotSupportedException)
        {
            return label + ": unavailable (" + exception.GetType().Name + ").";
        }
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

    private sealed record PhysicalMemoryDetails(
        string Description,
        long? TotalBytes,
        long? AvailableBytes);

    private sealed record VideoAdapterDetails(
        string Description,
        long? TotalDedicatedBytes,
        long? AvailableDedicatedBytes,
        string MemorySource);

    private static PhysicalMemoryDetails InspectPhysicalMemory()
    {
        if (!OperatingSystem.IsWindows())
            return new PhysicalMemoryDetails(
                "Physical RAM details are available only on Windows.", null, null);

        try
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };
            if (!GlobalMemoryStatusEx(ref status))
                return new PhysicalMemoryDetails(
                    "Physical RAM details could not be read.", null, null);

            var total = checked((long)status.TotalPhysical);
            var available = checked((long)status.AvailablePhysical);
            return new PhysicalMemoryDetails(
                FormatBytes(total) + " total | " + FormatBytes(available) + " currently available",
                total,
                available);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return new PhysicalMemoryDetails(
                "Physical RAM details unavailable: " + exception.GetType().Name, null, null);
        }
    }

    private static VideoAdapterDetails InspectVideoControllers()
    {
        if (!OperatingSystem.IsWindows())
            return new VideoAdapterDetails(
                "Video adapter details are available only on Windows.", null, null, "unavailable");

        var description = "Video adapter name and driver version could not be queried.";
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(
            systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell))
        {
            const string script =
                "$ErrorActionPreference='Stop'; " +
                "Get-CimInstance -ClassName Win32_VideoController | " +
                "ForEach-Object { " +
                "$vram='unknown'; if ($null -ne $_.AdapterRAM) { $vram=[string]$_.AdapterRAM }; " +
                "Write-Output ($_.Name + ' | driver ' + $_.DriverVersion + ' | adapter RAM bytes ' + $vram) }";

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
                if (process.Start() && process.WaitForExit(4000))
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    if (!string.IsNullOrWhiteSpace(output))
                        description = output.Length <= 700 ? output : output[..700] + "…";
                }
                else if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    description = "Video adapter query timed out.";
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                    IOException or UnauthorizedAccessException)
            {
                description = "Video adapter details unavailable: " + exception.GetType().Name;
            }
        }

        var nvidiaMemory = InspectNvidiaGpuMemory();
        if (nvidiaMemory is null)
            return new VideoAdapterDetails(
                description + Environment.NewLine +
                "Dedicated GPU free memory unavailable; model selection will use system RAM only.",
                null,
                null,
                "unavailable");

        return new VideoAdapterDetails(
            description + Environment.NewLine +
            "NVIDIA VRAM (nvidia-smi): " + FormatBytes(nvidiaMemory.Value.TotalBytes) +
            " total | " + FormatBytes(nvidiaMemory.Value.FreeBytes) + " free",
            nvidiaMemory.Value.TotalBytes,
            nvidiaMemory.Value.FreeBytes,
            "nvidia-smi");
    }

    private static (long TotalBytes, long FreeBytes)? InspectNvidiaGpuMemory()
    {
        var executable = FindNvidiaSmi();
        if (executable is null) return null;

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };
        start.ArgumentList.Add("--query-gpu=memory.total,memory.free");
        start.ArgumentList.Add("--format=csv,noheader,nounits");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return null;
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            long selectedTotalMiB = 0;
            long selectedFreeMiB = 0;
            var rows = process.StandardOutput.ReadToEnd().Split(
                new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var found = 0;
            foreach (var row in rows)
            {
                var values = row.Split(',');
                if (values.Length != 2 ||
                    !long.TryParse(values[0].Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var deviceTotal) ||
                    !long.TryParse(values[1].Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var deviceFree) ||
                    deviceTotal <= 0 || deviceFree < 0 || deviceFree > deviceTotal)
                    continue;

                // Ollama may use a single adapter for this model. Never add VRAM
                // from distinct devices: doing so can claim a model fits when no
                // individual device has enough free memory.
                if (deviceFree > selectedFreeMiB)
                {
                    selectedTotalMiB = deviceTotal;
                    selectedFreeMiB = deviceFree;
                }
                found++;
            }

            if (found == 0 || selectedTotalMiB <= 0) return null;
            return (checked(selectedTotalMiB * 1024 * 1024), checked(selectedFreeMiB * 1024 * 1024));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim().Trim('"'), "nvidia-smi.exe")));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }
}
