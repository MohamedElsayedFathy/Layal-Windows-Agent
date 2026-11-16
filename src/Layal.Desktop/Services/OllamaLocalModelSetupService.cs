using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Installs the Ollama Windows runtime through WinGet when needed, starts its loopback server,
/// and pulls the pinned default chat tag. The caller must obtain explicit user consent first.
/// </summary>
public sealed class OllamaLocalModelSetupService
{
    public static readonly string DefaultModelId = "qwen3.5:2b";
    public static readonly string ModelSizeDescription = "2.7–3.1 GB";
    private static readonly TimeSpan ServerStartupTimeout = TimeSpan.FromSeconds(45);
    private const string OllamaApiBaseUrl = "http://127.0.0.1:11434/";
    private static readonly HttpClient PullClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly LocalChatModelService _chatService;
    private readonly LocalResourceSetupCoordinator _resourceCoordinator;

    public OllamaLocalModelSetupService()
        : this(
            new LocalChatModelService(),
            new LocalResourceSetupCoordinator(new LocalResourceManager()))
    {
    }

    public OllamaLocalModelSetupService(
        LocalChatModelService chatService,
        LocalResourceSetupCoordinator? resourceCoordinator = null)
    {
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _resourceCoordinator = resourceCoordinator ??
            new LocalResourceSetupCoordinator(new LocalResourceManager());
    }

    public async Task InstallAndPrepareAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = FindOllamaExecutable();

        if (executable is null)
        {
            await _resourceCoordinator.ExecuteAsync(
                LocalResourceIds.OllamaRuntime,
                async token =>
                {
                    var winget = FindOnPath("winget.exe") ??
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Microsoft", "WindowsApps", "winget.exe");
                    if (!File.Exists(winget))
                        throw new InvalidOperationException(
                            "لم أجد Ollama أو WinGet على الجهاز. ثبّت Ollama من https://ollama.com/download/windows ثم أعد المحاولة.");

                    progress?.Report("تثبيت Ollama لنظام Windows عبر WinGet…");
                    await RunCommandAsync(
                        winget,
                        new[]
                        {
                            "install", "--id", "Ollama.Ollama", "--exact", "--source", "winget",
                            "--accept-package-agreements", "--accept-source-agreements"
                        },
                        progress,
                        token).ConfigureAwait(false);
                },
                VerifyOllamaRuntimeAsync,
                cancellationToken).ConfigureAwait(false);

            executable = FindOllamaExecutable();
            if (executable is null)
                throw new InvalidOperationException(
                    "انتهى أمر تثبيت Ollama، لكن لم أجد ollama.exe. أعد فتح Layal أو أعد محاولة التثبيت.");
        }
        else
        {
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.OllamaRuntime,
                VerifyOllamaRuntimeAsync,
                cancellationToken).ConfigureAwait(false);
        }

        await EnsureServerReadyAsync(executable, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report($"تنزيل نموذج المحادثة المحلي {DefaultModelId} (الحجم المتوقع {ModelSizeDescription})…");
        await _resourceCoordinator.ExecuteAsync(
            LocalResourceIds.ChatModel,
            token => PullModelAsync(progress, token),
            async token =>
            {
                if (!await VerifyChatModelAsync(executable, token).ConfigureAwait(false))
                    return false;

                await _chatService.RunModelSmokeTestAsync(
                    LocalChatModelService.DefaultBaseUrl, DefaultModelId, token).ConfigureAwait(false);
                await SaveModelDigestManifestAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        progress?.Report("اكتمل تنزيل النموذج، وتم اختبار توليد رد محلي والتحقق من بصمة النموذج.");
    }

    public async Task<bool> ValidateCachedResourcesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = FindOllamaExecutable();
        if (executable is null) return false;

        try
        {
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.OllamaRuntime,
                VerifyOllamaRuntimeAsync,
                cancellationToken).ConfigureAwait(false);
            await EnsureServerReadyAsync(executable, null, cancellationToken).ConfigureAwait(false);

            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.ChatModel,
                async token =>
                {
                    if (!await VerifyChatModelAsync(executable, token).ConfigureAwait(false))
                        return false;
                    var digest = await GetInstalledModelDigestAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(digest))
                        return false;
                    await _chatService.RunModelSmokeTestAsync(
                        LocalChatModelService.DefaultBaseUrl, DefaultModelId, token).ConfigureAwait(false);
                    await SaveModelDigestManifestAsync(digest, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or TimeoutException or HttpRequestException)
        {
            return false;
        }
    }

    private async Task<bool> VerifyOllamaRuntimeAsync(CancellationToken cancellationToken)
    {
        var executable = FindOllamaExecutable();
        if (executable is null) return false;
        try
        {
            if (!await VerifyOllamaAuthenticodeAsync(executable, cancellationToken).ConfigureAwait(false))
                return false;
            await RunCommandAsync(
                executable, new[] { "--version" }, null, cancellationToken).ConfigureAwait(false);
            return File.Exists(executable);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> VerifyOllamaAuthenticodeAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(executable))
            return false;

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(
            systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return false;

        var literalPath = "'" + executable.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script =
            "$ErrorActionPreference='Stop'; " +
            "$signature=Get-AuthenticodeSignature -LiteralPath " + literalPath + "; " +
            "if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) { exit 17 }; " +
            "[Console]::Out.WriteLine($signature.SignerCertificate.Subject)";

        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return false;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var subject = await stdout.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            _ = await stderr.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 &&
                   subject.Contains("Ollama", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            return false;
        }
    }

    private async Task PullModelAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(OllamaApiBaseUrl), "api/pull");
        if (!endpoint.IsLoopback)
            throw new InvalidOperationException("Ollama model downloads are restricted to the local loopback endpoint.");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { model = DefaultModelId, stream = true }),
                Encoding.UTF8,
                "application/json")
        };
        using var response = await PullClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Ollama pull failed with HTTP {(int)response.StatusCode}: {Truncate(detail, 1200)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lastBytes = 0L;
        var lastTotal = 0L;
        var lastSample = Stopwatch.GetTimestamp();
        var sawStatus = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (line.Length > 64 * 1024)
                throw new InvalidDataException("Ollama returned a pull progress record larger than 64 KB.");

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException("Ollama model download failed: " + Truncate(error.GetString() ?? string.Empty, 1200));

            var status = root.TryGetProperty("status", out var statusElement) &&
                         statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString() ?? "Downloading model"
                : "Downloading model";
            sawStatus = true;

            var completed = TryReadInt64(root, "completed");
            var total = TryReadInt64(root, "total");
            if (completed.HasValue && total is > 0)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = Math.Max((now - lastSample) / (double)Stopwatch.Frequency, 0.001);
                var speed = completed.Value >= lastBytes && total == lastTotal
                    ? (completed.Value - lastBytes) / elapsed
                    : 0d;
                var remaining = speed > 0
                    ? Math.Max(0, total.Value - completed.Value) / speed
                    : (double?)null;

                await _resourceCoordinator.ReportDownloadProgressAsync(
                    LocalResourceIds.ChatModel,
                    completed.Value,
                    total.Value,
                    speed,
                    remaining,
                    cancellationToken).ConfigureAwait(false);

                progress?.Report(
                    $"{status} · {FormatBytes(completed.Value)} / {FormatBytes(total.Value)}" +
                    (speed > 0 ? $" · {FormatBytes((long)speed)}/s" : string.Empty) +
                    (remaining.HasValue
                        ? $" · {TimeSpan.FromSeconds(Math.Clamp(remaining.Value, 0, 86400)):mm\\:ss}"
                        : string.Empty));

                lastBytes = completed.Value;
                lastTotal = total.Value;
                lastSample = now;
            }
            else
            {
                progress?.Report(status);
            }
        }

        if (!sawStatus)
            throw new InvalidDataException("Ollama ended its model pull stream without reporting a result.");
    }

    private async Task<bool> VerifyChatModelAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunCommandAsync(
                executable,
                new[] { "show", DefaultModelId },
                null,
                cancellationToken).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(
                await GetInstalledModelDigestAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or
                System.ComponentModel.Win32Exception or HttpRequestException or JsonException)
        {
            return false;
        }
    }

    private sealed record OllamaModelDigestManifest(
        string ModelId,
        string Digest,
        DateTimeOffset VerifiedAtUtc);

    private async Task<string?> GetInstalledModelDigestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(new Uri(OllamaApiBaseUrl), "api/tags"));
        using var response = await PullClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Ollama model inventory returned HTTP {(int)response.StatusCode}.");

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var model in models.EnumerateArray())
        {
            var name = TryReadString(model, "name");
            var modelId = TryReadString(model, "model");
            if (!string.Equals(name, DefaultModelId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(modelId, DefaultModelId, StringComparison.OrdinalIgnoreCase))
                continue;

            var digest = TryReadString(model, "digest");
            return string.IsNullOrWhiteSpace(digest) ? null : digest;
        }
        return null;
    }

    private static string ModelDigestManifestPath => Path.Combine(
        LocalVoiceService.DataRoot, "resource-manifests", "qwen3.5-2b.json");

    private async Task SaveModelDigestManifestAsync(CancellationToken cancellationToken)
    {
        var digest = await GetInstalledModelDigestAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(digest))
            throw new InvalidOperationException("Ollama did not return an immutable digest for the installed model.");
        await SaveModelDigestManifestAsync(digest, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveModelDigestManifestAsync(
        string digest,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ModelDigestManifestPath)!;
        Directory.CreateDirectory(directory);
        var manifest = new OllamaModelDigestManifest(
            DefaultModelId, digest, DateTimeOffset.UtcNow);
        var temporary = ModelDigestManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, ModelDigestManifestPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static long? TryReadInt64(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result) ? result : null;

    private static string TryReadString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        if (value >= 1024L * 1024 * 1024)
            return (value / 1024d / 1024d / 1024d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
        if (value >= 1024L * 1024)
            return (value / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        return (value / 1024d).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";
    }

    private static string Truncate(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[..maxCharacters] + "…";

    private async Task EnsureServerReadyAsync(
        string executable,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _chatService.TestConnectionAsync(
                LocalChatModelService.DefaultBaseUrl, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Start the local daemon below, then poll only the loopback endpoint.
        }

        progress?.Report("تشغيل خادم Ollama المحلي…");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("serve");

        try
        {
            using var server = Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Another Ollama tray instance may have started the server concurrently.
        }

        var deadline = DateTimeOffset.UtcNow + ServerStartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _chatService.TestConnectionAsync(
                    LocalChatModelService.DefaultBaseUrl, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "لم يستجب خادم Ollama على 127.0.0.1:11434. افتح Ollama من قائمة Start ثم أعد محاولة تجهيز النموذج.");
    }

    private static async Task RunCommandAsync(
        string executable,
        string[] arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"تعذر تشغيل {Path.GetFileName(executable)}. تحقق من تثبيت البرنامج وصلاحيات حساب Windows.", ex);
        }

        var stdout = PumpOutputAsync(process.StandardOutput, progress, cancellationToken);
        var stderr = PumpOutputAsync(process.StandardError, progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* The process may have exited while cancellation was requested. */ }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original cancellation. */ }
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} انتهى برمز الخطأ {process.ExitCode}. راجع حالة Ollama ثم أعد المحاولة.");
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var clean = line.Trim();
            if (clean.Length > 0)
                progress?.Report(clean.Length <= 240 ? clean : clean[..240] + "…");
        }
    }

    private static string? FindOllamaExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            FindOnPath("ollama.exe"),
            Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"),
            Path.Combine(programFiles, "Ollama", "ollama.exe"),
            Path.Combine(programFilesX86, "Ollama", "ollama.exe")
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string? FindOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
        }
        return null;
    }
}
