using System;
using System.Diagnostics;
using System.IO;
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
            token => RunCommandAsync(
                executable,
                new[] { "pull", DefaultModelId },
                progress,
                token),
            async token =>
            {
                var modelAvailable = await VerifyChatModelAsync(executable, token).ConfigureAwait(false);
                if (!modelAvailable) return false;

                await _chatService.TestConnectionAsync(
                    LocalChatModelService.DefaultBaseUrl, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        progress?.Report("اكتمل تنزيل النموذج واستجاب خادم Ollama المحلي.");
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

            if (!await VerifyChatModelAsync(executable, cancellationToken).ConfigureAwait(false))
                return false;

            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.ChatModel,
                async token =>
                {
                    if (!await VerifyChatModelAsync(executable, token).ConfigureAwait(false))
                        return false;
                    await _chatService.TestConnectionAsync(
                        LocalChatModelService.DefaultBaseUrl, token).ConfigureAwait(false);
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

    private static async Task<bool> VerifyChatModelAsync(
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
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

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
