using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public static readonly string DefaultModelId = QwenModelVariantCatalog.DefaultModelId;
    public static readonly string ModelSizeDescription = "2.7–3.1 GB";
    public string SelectedModelId => LocalResourceCatalog.CurrentChatModelId;
    private QwenModelVariant SelectedVariant => QwenModelVariantCatalog.GetRequired(SelectedModelId);
    public string SelectedModelSizeDescription => SelectedVariant.SizeDescription;
    private long ModelSizeUpperBoundBytes => SelectedVariant.MaximumDownloadBytes;
    private static readonly TimeSpan ServerStartupTimeout = TimeSpan.FromSeconds(45);

    public static string ModelsDirectory
    {
        get
        {
            var settings = Layal.AppRuntime.Settings;
            if (!string.IsNullOrWhiteSpace(settings.OllamaModelsDirectory))
                return LocalAiResourcePathPolicy.ValidateLocalDirectory(settings.OllamaModelsDirectory);

            if (!string.IsNullOrWhiteSpace(settings.LocalAiDataDirectory))
                return LocalAiResourcePathPolicy.ValidateLocalDirectory(
                    Path.Combine(LocalVoiceService.DataRoot, "ollama-models"));

            var configured = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
            return LocalAiResourcePathPolicy.ValidateLocalDirectory(
                string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models")
                    : configured);
        }
    }

    public static bool HasDefaultModelManifest(string? modelId = null)
    {
        var selected = QwenModelVariantCatalog.GetRequired(
            string.IsNullOrWhiteSpace(modelId) ? Layal.AppRuntime.Settings.LocalChatModelId : modelId);
        var separator = selected.ModelId.IndexOf(':');
        var tag = selected.ModelId[(separator + 1)..];
        return File.Exists(Path.Combine(
            ModelsDirectory, "manifests", "registry.ollama.ai", "library", "qwen3.5", tag));
    }

    private static bool UsesCustomModelsDirectory =>
        !string.IsNullOrWhiteSpace(Layal.AppRuntime.Settings.LocalAiDataDirectory) ||
        !string.IsNullOrWhiteSpace(Layal.AppRuntime.Settings.OllamaModelsDirectory);

    private static string RuntimeInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Ollama");

    public static string RuntimeCapacityDirectory =>
        Path.GetDirectoryName(FindOllamaExecutable() ?? Path.Combine(RuntimeInstallDirectory, "ollama.exe"))
        ?? RuntimeInstallDirectory;

    public static bool IsRuntimeInstalled => FindOllamaExecutable() is not null;

    public static bool IsWinGetAvailable
    {
        get
        {
            var winget = FindOnPath("winget.exe") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(winget);
        }
    }

    public static long FallbackInstallerExpectedBytes =>
        LocalResourceCatalog.GetRequired(LocalResourceIds.OllamaRuntimeInstaller).ExpectedSizeBytes ?? 0;

    private static void EnsureOllamaInstallCapacity(string? downloadDirectory, long downloadBytes)
    {
        const long runtimeReferenceBytes = 4_000_000_000;
        var runtimeRoot = Path.GetPathRoot(Path.GetFullPath(RuntimeCapacityDirectory));
        var downloadRoot = string.IsNullOrWhiteSpace(downloadDirectory)
            ? null
            : Path.GetPathRoot(Path.GetFullPath(downloadDirectory));
        if (!string.IsNullOrWhiteSpace(downloadRoot) &&
            string.Equals(runtimeRoot, downloadRoot, StringComparison.OrdinalIgnoreCase))
        {
            LocalResourceStorageCapacityGuard.EnsureAvailable(
                RuntimeCapacityDirectory, checked(downloadBytes * 2), runtimeReferenceBytes);
            return;
        }

        LocalResourceStorageCapacityGuard.EnsureAvailable(
            RuntimeCapacityDirectory, runtimeReferenceBytes);
        if (!string.IsNullOrWhiteSpace(downloadDirectory) && downloadBytes > 0)
            LocalResourceStorageCapacityGuard.EnsureAvailable(
                downloadDirectory, downloadBytes, downloadBytes);
    }

    public async Task<bool> IsServerRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _chatService.TestConnectionAsync(
                LocalChatModelService.DefaultBaseUrl, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
    private const string OllamaApiBaseUrl = "http://127.0.0.1:11434/";
    private static readonly HttpClient PullClient = new(CreatePullHttpHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static SocketsHttpHandler CreatePullHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    private readonly LocalChatModelService _chatService;
    private readonly LocalResourceSetupCoordinator _resourceCoordinator;
    private readonly OllamaRegistryManifestClient _registryClient;

    public OllamaLocalModelSetupService()
        : this(
            new LocalChatModelService(),
            new LocalResourceSetupCoordinator(
                new LocalResourceManager(),
                new LocalResourceDownloadService()))
    {
    }

    public OllamaLocalModelSetupService(
        LocalChatModelService chatService,
        LocalResourceSetupCoordinator? resourceCoordinator = null,
        OllamaRegistryManifestClient? registryClient = null)
    {
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _resourceCoordinator = resourceCoordinator ??
            new LocalResourceSetupCoordinator(new LocalResourceManager());
        _registryClient = registryClient ?? new OllamaRegistryManifestClient();
    }

    public async Task InstallAndPrepareAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<LocalResourceDownloadProgress>? downloadProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = FindOllamaExecutable();

        if (executable is null)
        {
            var winget = FindOnPath("winget.exe") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "winget.exe");

            if (File.Exists(winget))
            {
                EnsureOllamaInstallCapacity(null, 0);
                await _resourceCoordinator.ExecuteAsync(
                    LocalResourceIds.OllamaRuntime,
                    async token =>
                    {
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
            }
            else
            {
                // If WinGet is unavailable, use a version-pinned official Ollama installer.
                // Its bytes and publisher are verified before silent per-user installation.
                var fallback = LocalResourceCatalog.GetRequired(LocalResourceIds.OllamaRuntimeInstaller);
                var downloadsDirectory = Path.Combine(LocalVoiceService.DataRoot, "downloads");
                Directory.CreateDirectory(downloadsDirectory);
                var installerPath = Path.Combine(downloadsDirectory, "OllamaSetup-v0.40.16.exe");
                if (!await MatchesPinnedInstallerAsync(installerPath, fallback, cancellationToken).ConfigureAwait(false))
                    EnsureOllamaInstallCapacity(downloadsDirectory, fallback.ExpectedSizeBytes!.Value);
                else
                    EnsureOllamaInstallCapacity(null, 0);
                progress?.Report("WinGet غير متاح؛ جارٍ تنزيل مثبّت Ollama الرسمي المثبّت الإصدار…");

                await _resourceCoordinator.DownloadAsync(
                    fallback,
                    installerPath,
                    downloadProgress,
                    cancellationToken,
                    async token =>
                    {
                        if (!await VerifyOllamaAuthenticodeAsync(installerPath, token).ConfigureAwait(false))
                            return false;

                        progress?.Report("تم التحقق من توقيع Ollama؛ جارٍ التثبيت الصامت…");
                        await RunOfficialInstallerAsync(installerPath, token).ConfigureAwait(false);
                        return await VerifyOllamaRuntimeAsync(token).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                await _resourceCoordinator.EnsureReadyAsync(
                    LocalResourceIds.OllamaRuntime,
                    VerifyOllamaRuntimeAsync,
                    cancellationToken).ConfigureAwait(false);
            }

            executable = FindOllamaExecutable();
            if (executable is null)
                throw new InvalidOperationException(
                    "انتهى مسار تثبيت Ollama، لكن لم أجد ollama.exe. راجع سجل الإعداد ثم أعد المحاولة.");
        }
        else
        {
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.OllamaRuntime,
                VerifyOllamaRuntimeAsync,
                cancellationToken).ConfigureAwait(false);
        }

        await EnsureServerReadyAsync(executable, progress, cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedModelUpdateAsync(executable, progress, cancellationToken).ConfigureAwait(false);

        var existingPin = await ReadModelDigestManifestAsync(cancellationToken).ConfigureAwait(false);
        var installedDigest = await GetInstalledModelDigestAsync(cancellationToken).ConfigureAwait(false);

        if (existingPin is not null && string.IsNullOrWhiteSpace(installedDigest))
        {
            throw new InvalidOperationException(
                "The previously verified Qwen model is missing, but its local digest pin remains. " +
                "Automatic download from the mutable selected Qwen3.5 tag is blocked. " +
                "Use an explicit model-reset workflow before replacing this pin.");
        }

        // A Registry digest is required before the first install and before upgrading
        // an older local TOFU pin. Once a Registry-backed pin exists, normal startup
        // verifies it offline and does not silently follow a mutable tag.
        OllamaRegistryManifestSnapshot? registryManifest = null;
        if (existingPin is null || !IsRegistryTrustedPin(existingPin))
        {
            progress?.Report("التحقق من هوية نموذج Qwen عبر Ollama Registry قبل اعتماد التنزيل…");
            registryManifest = await _registryClient.FetchModelManifestAsync(SelectedModelId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(installedDigest))
        {
            progress?.Report("إعادة التحقق من نموذج Qwen المحلي ومقارنته بمرجع Registry الموثوق…");
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.ChatModel,
                token => VerifyPinnedModelAsync(executable, registryManifest, token),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (registryManifest is null)
                throw new InvalidOperationException(
                    "A trusted Qwen Registry manifest is required before the first model download.");

            progress?.Report($"تنزيل نموذج المحادثة المحلي {SelectedModelId} (الحجم المتوقع {SelectedModelSizeDescription})…");
            await _resourceCoordinator.ExecuteAsync(
                LocalResourceIds.ChatModel,
                token => PullModelAsync(progress, downloadProgress, token),
                token => VerifyPinnedModelAsync(executable, registryManifest, token),
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report("اكتمل التحقق من نموذج Qwen ومطابقة digest المحلي بمرجع Ollama Registry.");
    }

    /// <summary>
    /// Explicitly updates the pinned Qwen model. The current model is copied to a
    /// rollback alias and journaled before the mutable tag is pulled. The old pin is
    /// replaced only after Registry digest matching and a local chat smoke test.
    /// </summary>
    public async Task UpdateModelAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<LocalResourceDownloadProgress>? downloadProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = FindOllamaExecutable()
            ?? throw new InvalidOperationException("Ollama is not installed. Set up local chat before requesting an update.");

        await EnsureServerReadyAsync(executable, progress, cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedModelUpdateAsync(executable, progress, cancellationToken).ConfigureAwait(false);

        var previousPin = await ReadModelDigestManifestAsync(cancellationToken).ConfigureAwait(false);
        if (previousPin is null || !IsRegistryTrustedPin(previousPin))
            throw new InvalidOperationException(
                "A Registry-backed Qwen digest pin is required before an update. Complete the initial verified setup first.");

        var installedDigest = await GetInstalledModelDigestAsync(cancellationToken).ConfigureAwait(false);
        OllamaModelDigestPolicy.EnsureMatches(previousPin.Digest, installedDigest ?? string.Empty);

        progress?.Report("التحقق من أحدث manifest في Ollama Registry قبل بدء التحديث…");
        var registryManifest = await _registryClient.FetchModelManifestAsync(SelectedModelId, cancellationToken)
            .ConfigureAwait(false);
        if (OllamaModelDigestPolicy.Matches(
            previousPin.RegistryManifestDigest ?? string.Empty,
            registryManifest.ManifestDigest))
        {
            progress?.Report("بصمة manifest الحالية مطابقة للبصمة المثبتة؛ لا يوجد تحديث مطلوب.");
            return;
        }

        // A manifest-list parent can change while the installed child remains a
        // valid member. Refresh provenance without re-downloading identical weights.
        if (registryManifest.ContainsModelDigest(installedDigest!))
        {
            progress?.Report("النموذج المثبت ما زال ضمن manifest الحالي؛ تحديث مرجع Registry دون إعادة تنزيل الأوزان…");
            await _chatService.RunModelSmokeTestAsync(
                LocalChatModelService.DefaultBaseUrl, SelectedModelId, cancellationToken).ConfigureAwait(false);
            await SaveRegistryModelDigestManifestAsync(
                installedDigest!,
                registryManifest,
                cancellationToken,
                expectedPreviousDigest: previousPin.Digest).ConfigureAwait(false);
            progress?.Report("تم تحديث مرجع Registry للنموذج المثبت؛ لم تكن هناك حاجة لإعادة تنزيله.");
            return;
        }

        // Keep the previous model blobs reachable while the mutable model tag is replaced.
        LocalResourceStorageCapacityGuard.EnsureAvailable(
            ModelsDirectory,
            checked(ModelSizeUpperBoundBytes * 2),
            512L * 1024 * 1024);

        var rollbackAlias = "layal-rollback:qwen35-" + Guid.NewGuid().ToString("N")[..12];
        var rollbackVerified = false;
        var updateCommitted = false;

        try
        {
            var updatedDigest = await OllamaModelUpdateTransaction.ExecuteAsync(
                rollbackAlias,
                async token =>
                {
                    progress?.Report("إنشاء نسخة رجوع آمنة من نموذج Qwen الحالي…");
                    try
                    {
                        await RunCommandAsync(
                            executable,
                            new[] { "cp", SelectedModelId, rollbackAlias },
                            null,
                            token).ConfigureAwait(false);
                        await WriteModelUpdateJournalAsync(
                            rollbackAlias, previousPin, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A failed backup/journal step occurs before the target tag is mutated.
                        try
                        {
                            await RunCommandAsync(
                                executable, new[] { "rm", rollbackAlias }, null, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { /* Preserve the original backup/journal failure. */ }
                        throw;
                    }
                },
                async token =>
                {
                    progress?.Report("تنزيل إصدار Qwen الأحدث مع الإبقاء على نسخة الرجوع…");
                    await PullModelAsync(progress, downloadProgress, token).ConfigureAwait(false);
                    var digest = await GetInstalledModelDigestAsync(token).ConfigureAwait(false);
                    if (!OllamaModelDigestPolicy.IsValidSha256Digest(digest))
                        throw new InvalidDataException("Ollama did not report a valid digest after the model update.");
                    return digest!;
                },
                async (candidateDigest, token) =>
                {
                    if (!registryManifest.ContainsModelDigest(candidateDigest))
                        return false;

                    progress?.Report("مطابقة digest الجديد مع Registry وتشغيل اختبار صحة المحادثة…");
                    await _chatService.RunModelSmokeTestAsync(
                        LocalChatModelService.DefaultBaseUrl, SelectedModelId, token).ConfigureAwait(false);
                    return true;
                },
                async (candidateDigest, token) =>
                {
                    await SaveRegistryModelDigestManifestAsync(
                        candidateDigest,
                        registryManifest,
                        token,
                        expectedPreviousDigest: previousPin.Digest).ConfigureAwait(false);
                },
                async _ =>
                {
                    progress?.Report("فشل التحديث؛ استعادة نسخة Qwen السابقة…");
                    await RunCommandAsync(
                        executable,
                        new[] { "cp", rollbackAlias, SelectedModelId },
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                    await RestoreModelDigestManifestAsync(previousPin, CancellationToken.None)
                        .ConfigureAwait(false);
                },
                async _ =>
                {
                    var restoredDigest = await GetInstalledModelDigestAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    OllamaModelDigestPolicy.EnsureMatches(previousPin.Digest, restoredDigest ?? string.Empty);
                    await _chatService.RunModelSmokeTestAsync(
                        LocalChatModelService.DefaultBaseUrl, previousPin.ModelId, CancellationToken.None)
                        .ConfigureAwait(false);
                    rollbackVerified = true;
                },
                cancellationToken).ConfigureAwait(false);

            updateCommitted = true;
            // Removing the journal is the durable commit marker. If that fails,
            // restore the previous model+pin rather than reporting a commit that
            // startup recovery would silently undo later.
            try
            {
                if (File.Exists(ModelUpdateJournalPath))
                    File.Delete(ModelUpdateJournalPath);
            }
            catch (Exception journalError) when (
                journalError is IOException or UnauthorizedAccessException)
            {
                progress?.Report("تعذر إغلاق سجل التعافي بعد التحديث؛ استعادة الإصدار السابق بدل إعلان نجاح غير دائم…");
                try
                {
                    await RunCommandAsync(
                        executable,
                        new[] { "cp", rollbackAlias, SelectedModelId },
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                    await RestoreModelDigestManifestAsync(previousPin, CancellationToken.None)
                        .ConfigureAwait(false);
                    var restoredDigest = await GetInstalledModelDigestAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    OllamaModelDigestPolicy.EnsureMatches(previousPin.Digest, restoredDigest ?? string.Empty);
                    await _chatService.RunModelSmokeTestAsync(
                        LocalChatModelService.DefaultBaseUrl, SelectedModelId, CancellationToken.None)
                        .ConfigureAwait(false);
                    rollbackVerified = true;
                    updateCommitted = false;
                    if (File.Exists(ModelUpdateJournalPath))
                        File.Delete(ModelUpdateJournalPath);
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(
                        "The Qwen update could not clear its recovery journal and the previous model could not be fully restored. Preserve the rollback alias and journal for startup recovery.",
                        new AggregateException(journalError, rollbackError));
                }

                throw new IOException(
                    "Qwen was restored to its previous verified version because its update journal could not be cleared.",
                    journalError);
            }

            progress?.Report(
                "نجح تحديث Qwen. تم اعتماد digest الجديد بعد مطابقة Registry واختبار المحادثة: " +
                updatedDigest);
        }
        catch
        {
            if (!rollbackVerified && File.Exists(ModelUpdateJournalPath))
            {
                progress?.Report(
                    "تعذر إثبات اكتمال الرجوع؛ تم الاحتفاظ بسجل التعافي ونسخة الرجوع. لا تحذف مجلد النموذج حتى يكتمل الاسترداد.");
            }
            else if (!rollbackVerified)
            {
                progress?.Report(
                    "تعذر بدء تحديث Qwen أو إنشاء نسخة الرجوع؛ لم يبدأ استبدال النموذج. راجع رسالة الخطأ ثم أعد المحاولة.");
            }
            throw;
        }
        finally
        {
            if (updateCommitted || rollbackVerified)
            {
                try
                {
                    if (File.Exists(ModelUpdateJournalPath))
                        File.Delete(ModelUpdateJournalPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    progress?.Report("تعذر حذف سجل التعافي؛ ستظل نسخة الرجوع محفوظة ليكتمل الاسترداد في التشغيل التالي.");
                }

                // The rollback alias is removed only after the recovery journal is gone.
                if (!File.Exists(ModelUpdateJournalPath))
                {
                    try
                    {
                        await RunCommandAsync(
                            executable,
                            new[] { "rm", rollbackAlias },
                            null,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        progress?.Report("تعذر حذف نسخة الرجوع المؤقتة؛ يمكن حذفها لاحقًا بعد التأكد من سلامة النموذج الحالي.");
                    }
                }
            }
        }
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
            await RecoverInterruptedModelUpdateAsync(executable, null, cancellationToken).ConfigureAwait(false);

            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.ChatModel,
                token => VerifyPinnedModelAsync(executable, token),
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

    private static async Task<bool> MatchesPinnedInstallerAsync(
        string path,
        LocalResourceDefinition resource,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) ||
            resource.ExpectedSizeBytes is not > 0 ||
            !resource.HasPinnedSha256)
            return false;
        var info = new FileInfo(path);
        if (info.Length != resource.ExpectedSizeBytes.Value)
            return false;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            actual, Convert.FromHexString(resource.ExpectedSha256!));
    }

    private static async Task RunOfficialInstallerAsync(
        string installerPath,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("/VERYSILENT");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/SUPPRESSMSGBOXES");

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("تعذر بدء مثبّت Ollama الرسمي.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("انتهت مهلة تثبيت Ollama الرسمي.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "فشل مثبّت Ollama الرسمي برمز الخروج " +
                process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
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
            "$subject=$signature.SignerCertificate.Subject; " +
            "if ($subject -notmatch '(^|, )O=Ollama Inc\\.(,|$)') { exit 18 }; " +
            "[Console]::Out.WriteLine($subject)";

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
                   subject.Contains("Ollama Inc.", StringComparison.OrdinalIgnoreCase);
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
        IProgress<LocalResourceDownloadProgress>? downloadProgress,
        CancellationToken cancellationToken)
    {
        var modelStore = ModelsDirectory;
        Directory.CreateDirectory(modelStore);
        LocalResourceStorageCapacityGuard.EnsureAvailable(
            modelStore, ModelSizeUpperBoundBytes, 512L * 1024 * 1024);
        var endpoint = new Uri(new Uri(OllamaApiBaseUrl), "api/pull");
        if (!endpoint.IsLoopback)
            throw new InvalidOperationException("Ollama model downloads are restricted to the local loopback endpoint.");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { model = SelectedModelId, stream = true }),
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
                var remainingModelBytes = Math.Max(0, total.Value - completed.Value);
                LocalResourceStorageCapacityGuard.EnsureAvailable(
                    modelStore, remainingModelBytes, 512L * 1024 * 1024);
                var now = Stopwatch.GetTimestamp();
                var elapsed = Math.Max((now - lastSample) / (double)Stopwatch.Frequency, 0.001);
                var speed = completed.Value >= lastBytes && total == lastTotal
                    ? (completed.Value - lastBytes) / elapsed
                    : 0d;
                var remaining = speed > 0
                    ? Math.Max(0, total.Value - completed.Value) / speed
                    : (double?)null;

                var resourceProgress = new LocalResourceDownloadProgress(
                    LocalResourceIds.ChatModel,
                    completed.Value,
                    total.Value,
                    speed,
                    remaining);
                await _resourceCoordinator.ReportDownloadProgressAsync(
                    LocalResourceIds.ChatModel,
                    completed.Value,
                    total.Value,
                    speed,
                    remaining,
                    cancellationToken).ConfigureAwait(false);
                downloadProgress?.Report(resourceProgress);

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
        CancellationToken cancellationToken,
        string? modelId = null)
    {
        try
        {
            await RunCommandAsync(
                executable,
                new[] { "show", string.IsNullOrWhiteSpace(modelId) ? SelectedModelId : modelId },
                null,
                cancellationToken).ConfigureAwait(false);
            var digest = await GetInstalledModelDigestAsync(cancellationToken, modelId).ConfigureAwait(false);
            return OllamaModelDigestPolicy.IsValidSha256Digest(digest);
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
        DateTimeOffset VerifiedAtUtc,
        string? RegistryHost = null,
        string? ManifestUri = null,
        string? ManifestMediaType = null,
        long? ManifestSizeBytes = null,
        DateTimeOffset? RegistryVerifiedAtUtc = null,
        string? RegistryManifestDigest = null,
        string[]? RegistryModelDigests = null);

    private sealed record OllamaModelUpdateJournal(
        string RollbackModelId,
        OllamaModelDigestManifest PreviousPin,
        DateTimeOffset StartedAtUtc);

    private async Task<string?> GetInstalledModelDigestAsync(
        CancellationToken cancellationToken,
        string? modelId = null)
    {
        var targetModelId = string.IsNullOrWhiteSpace(modelId) ? SelectedModelId : modelId;
        if (!QwenModelVariantCatalog.TryGet(targetModelId, out var allowedModel) || allowedModel is null)
            throw new InvalidOperationException("The requested Qwen model is not allow-listed.");
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
            var modelName = TryReadString(model, "model");
            if (!string.Equals(name, targetModelId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(modelName, targetModelId, StringComparison.OrdinalIgnoreCase))
                continue;

            var digest = TryReadString(model, "digest");
            return string.IsNullOrWhiteSpace(digest) ? null : digest;
        }
        return null;
    }

    private string ModelDigestManifestPath => GetModelDigestManifestPath(SelectedModelId);

    // One family-wide recovery journal lets startup finish an interrupted rollback even
    // if the selected Qwen tag changed between application runs.
    private static string ModelUpdateJournalPath => Path.Combine(
        LocalVoiceService.DataRoot, "resource-manifests", "qwen3.5.update.json");

    // Older releases used a model-specific journal name. Keep recovery compatibility
    // so an interrupted 2B update from an earlier build is rolled back before reuse.
    private static string LegacyDefaultModelUpdateJournalPath => Path.Combine(
        LocalVoiceService.DataRoot, "resource-manifests", "qwen3.5-2b.json.update.json");

    private static string GetModelDigestManifestPath(string modelId)
    {
        if (!QwenModelVariantCatalog.TryGet(modelId, out var variant) || variant is null)
            throw new InvalidOperationException("Only an allow-listed Qwen3.5 model variant can have a digest pin.");
        var separator = variant.ModelId.IndexOf(':');
        var tag = variant.ModelId[(separator + 1)..];
        return Path.Combine(
            LocalVoiceService.DataRoot, "resource-manifests", "qwen3.5-" + tag + ".json");
    }

    private static bool IsValidRollbackModelId(string value)
    {
        const string prefix = "layal-rollback:qwen35-";
        if (value.Length != prefix.Length + 12 ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        for (var index = prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            var isDecimalDigit = character >= '0' && character <= '9';
            var isLowerHexDigit = character >= 'a' && character <= 'f';
            if (!isDecimalDigit && !isLowerHexDigit)
                return false;
        }

        return true;
    }

    private async Task WriteModelUpdateJournalAsync(
        string rollbackModelId,
        OllamaModelDigestManifest previousPin,
        CancellationToken cancellationToken)
    {
        if (!IsValidRollbackModelId(rollbackModelId) ||
            !IsRegistryTrustedPin(previousPin))
            throw new InvalidDataException("The Qwen update journal contains an invalid rollback reference or prior pin.");

        var path = Path.GetFullPath(ModelUpdateJournalPath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Qwen update journal has no parent directory.");
        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        if (File.Exists(path))
            throw new InvalidOperationException(
                "An earlier Qwen update recovery journal already exists. Recover it before starting another update.");

        var journal = new OllamaModelUpdateJournal(
            rollbackModelId, previousPin, DateTimeOffset.UtcNow);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    output, journal, cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task RecoverInterruptedModelUpdateAsync(
        string executable,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var familyPath = Path.GetFullPath(ModelUpdateJournalPath);
        var legacyPath = Path.GetFullPath(LegacyDefaultModelUpdateJournalPath);
        var directory = Path.GetDirectoryName(familyPath)
            ?? throw new InvalidOperationException("The Qwen update journal has no parent directory.");
        EnsureNoReparsePoints(directory);
        var hasFamilyJournal = File.Exists(familyPath);
        var hasLegacyJournal = File.Exists(legacyPath);
        if (hasFamilyJournal && hasLegacyJournal)
            throw new InvalidDataException(
                "Both current and legacy Qwen recovery journals exist. Resolve the ambiguous recovery state before using the model.");
        if (!hasFamilyJournal && !hasLegacyJournal)
            return;
        var path = hasFamilyJournal ? familyPath : legacyPath;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Qwen update journal must not be a reparse point.");

        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > 32 * 1024)
            throw new InvalidDataException("The Qwen update journal has an invalid size.");

        OllamaModelUpdateJournal? journal;
        try
        {
            await using (var input = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                journal = await JsonSerializer.DeserializeAsync<OllamaModelUpdateJournal>(
                    input, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Qwen update recovery journal contains invalid JSON.", exception);
        }

        if (journal is null ||
            string.IsNullOrWhiteSpace(journal.RollbackModelId) ||
            !IsValidRollbackModelId(journal.RollbackModelId) ||
            journal.PreviousPin is null ||
            !IsRegistryTrustedPin(journal.PreviousPin) ||
            journal.StartedAtUtc == default ||
            journal.StartedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("The Qwen update recovery journal is malformed or untrusted.");

        progress?.Report("اكتشاف تحديث Qwen غير مكتمل؛ استعادة النسخة السابقة تلقائيًا…");
        // Recovery is deliberately non-cancellable once the journal has been accepted:
        // leaving model files and their digest pin out of sync is less safe than finishing rollback.
        await RunCommandAsync(
            executable,
            new[] { "cp", journal.RollbackModelId, journal.PreviousPin.ModelId },
            null,
            CancellationToken.None).ConfigureAwait(false);
        await RestoreModelDigestManifestAsync(journal.PreviousPin, CancellationToken.None)
            .ConfigureAwait(false);

        var restoredDigest = await GetInstalledModelDigestAsync(
            CancellationToken.None, journal.PreviousPin.ModelId).ConfigureAwait(false);
        OllamaModelDigestPolicy.EnsureMatches(journal.PreviousPin.Digest, restoredDigest ?? string.Empty);
        await _chatService.RunModelSmokeTestAsync(
            LocalChatModelService.DefaultBaseUrl, journal.PreviousPin.ModelId, CancellationToken.None)
            .ConfigureAwait(false);

        File.Delete(path);
        try
        {
            await RunCommandAsync(
                executable,
                new[] { "rm", journal.RollbackModelId },
                null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            progress?.Report("تم استرداد Qwen، لكن بقيت نسخة رجوع قديمة يمكن تنظيفها لاحقًا.");
        }
    }

    private async Task RestoreModelDigestManifestAsync(
        OllamaModelDigestManifest previousPin,
        CancellationToken cancellationToken)
    {
        if (!IsRegistryTrustedPin(previousPin))
            throw new InvalidDataException("Cannot restore an untrusted Qwen Registry pin.");

        var path = Path.GetFullPath(GetModelDigestManifestPath(previousPin.ModelId));
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Qwen digest pin has no parent directory.");
        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Qwen digest pin must not be a reparse point.");

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".rollback.tmp";
        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    output, previousPin, cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsRegistryTrustedPin(OllamaModelDigestManifest pin) =>
        QwenModelVariantCatalog.TryGet(pin.ModelId, out _) &&
        string.Equals(
            pin.RegistryHost,
            OllamaRegistryManifestClient.RegistryHost,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            pin.ManifestUri,
            OllamaRegistryManifestClient.BuildManifestUri(pin.ModelId).AbsoluteUri,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(pin.ManifestMediaType) &&
        pin.ManifestSizeBytes is > 0 &&
        pin.RegistryVerifiedAtUtc.HasValue &&
        OllamaModelDigestPolicy.IsValidSha256Digest(pin.RegistryManifestDigest) &&
        pin.RegistryModelDigests is { Length: > 0 } &&
        pin.RegistryModelDigests.Any(candidate =>
            OllamaModelDigestPolicy.IsValidSha256Digest(candidate) &&
            OllamaModelDigestPolicy.Matches(candidate, pin.Digest));

    private Task<bool> VerifyPinnedModelAsync(
        string executable,
        CancellationToken cancellationToken) =>
        VerifyPinnedModelAsync(executable, null, cancellationToken);

    private async Task<bool> VerifyPinnedModelAsync(
        string executable,
        OllamaRegistryManifestSnapshot? registryManifest,
        CancellationToken cancellationToken)
    {
        if (!await VerifyChatModelAsync(executable, cancellationToken).ConfigureAwait(false))
            return false;

        var actualDigest = await GetInstalledModelDigestAsync(cancellationToken).ConfigureAwait(false);
        if (!OllamaModelDigestPolicy.IsValidSha256Digest(actualDigest))
            return false;

        var pin = await ReadModelDigestManifestAsync(cancellationToken).ConfigureAwait(false);
        if (pin is not null)
        {
            if (!string.Equals(pin.ModelId, SelectedModelId, StringComparison.Ordinal))
                throw new InvalidDataException("The saved Qwen digest pin belongs to a different model.");
            OllamaModelDigestPolicy.EnsureMatches(pin.Digest, actualDigest!);
        }

        if (registryManifest is not null)
        {
            if (!string.Equals(registryManifest.ModelId, SelectedModelId, StringComparison.Ordinal))
                throw new InvalidDataException("The Registry manifest belongs to a different Qwen model.");

            // /api/tags may report the selected child manifest for a manifest list,
            // not the parent list digest. Require the installed digest to be one of
            // the exact child digests advertised by the Registry response.
            if (!registryManifest.ContainsModelDigest(actualDigest!))
                throw new InvalidDataException(
                    "The installed Qwen digest is not present in the verified Ollama Registry manifest.");
        }
        else if (pin is null)
        {
            // Never recreate a missing pin from local TOFU alone. Offline cache
            // validation is permitted only when a prior pin already exists.
            return false;
        }

        await _chatService.RunModelSmokeTestAsync(
            LocalChatModelService.DefaultBaseUrl, SelectedModelId, cancellationToken).ConfigureAwait(false);

        if (registryManifest is not null &&
            (pin is null || !IsRegistryTrustedPin(pin)))
        {
            await SaveRegistryModelDigestManifestAsync(
                actualDigest!,
                registryManifest,
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<OllamaModelDigestManifest?> ReadModelDigestManifestAsync(
        CancellationToken cancellationToken,
        string? modelId = null)
    {
        var targetModelId = string.IsNullOrWhiteSpace(modelId) ? SelectedModelId : modelId;
        if (!QwenModelVariantCatalog.TryGet(targetModelId, out var allowedModel) || allowedModel is null)
            throw new InvalidOperationException("The requested Qwen model is not allow-listed.");
        var path = Path.GetFullPath(GetModelDigestManifestPath(targetModelId));
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Qwen digest manifest has no parent directory.");
        EnsureNoReparsePoints(directory);
        if (!File.Exists(path))
            return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Qwen digest manifest must not be a reparse point.");

        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > 16 * 1024)
            throw new InvalidDataException("The Qwen digest manifest has an invalid size.");

        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        OllamaModelDigestManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<OllamaModelDigestManifest>(
                input, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Qwen digest manifest contains invalid JSON.", exception);
        }

        if (manifest is null ||
            !string.Equals(manifest.ModelId, targetModelId, StringComparison.Ordinal) ||
            !OllamaModelDigestPolicy.IsValidSha256Digest(manifest.Digest))
            throw new InvalidDataException("The Qwen digest manifest is malformed or does not match the configured model.");

        var hasRegistryMetadata =
            manifest.RegistryHost is not null ||
            manifest.ManifestUri is not null ||
            manifest.ManifestMediaType is not null ||
            manifest.ManifestSizeBytes.HasValue ||
            manifest.RegistryVerifiedAtUtc.HasValue ||
            manifest.RegistryManifestDigest is not null ||
            manifest.RegistryModelDigests is not null;
        if (hasRegistryMetadata && !IsRegistryTrustedPin(manifest))
            throw new InvalidDataException("The Qwen Registry provenance metadata is incomplete or invalid.");

        return manifest with { Digest = manifest.Digest.ToLowerInvariant() };
    }

    private async Task SaveRegistryModelDigestManifestAsync(
        string digest,
        OllamaRegistryManifestSnapshot registryManifest,
        CancellationToken cancellationToken,
        string? expectedPreviousDigest = null)
    {
        if (!OllamaModelDigestPolicy.IsValidSha256Digest(digest))
            throw new InvalidDataException("Ollama did not return a valid full SHA-256 model digest.");
        if (!registryManifest.ContainsModelDigest(digest))
            throw new InvalidDataException(
                "The installed Qwen digest is not present in the verified Ollama Registry manifest.");

        if (!string.Equals(registryManifest.ModelId, SelectedModelId, StringComparison.Ordinal))
            throw new InvalidDataException("The Registry manifest does not match the selected Qwen model.");
        var targetPath = GetModelDigestManifestPath(registryManifest.ModelId);
        var directory = Path.GetDirectoryName(targetPath)!;
        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);

        var existing = await ReadModelDigestManifestAsync(cancellationToken, registryManifest.ModelId).ConfigureAwait(false);
        if (expectedPreviousDigest is not null)
        {
            if (existing is null || !IsRegistryTrustedPin(existing))
                throw new InvalidDataException("The expected previous Qwen Registry pin is missing or untrusted.");
            OllamaModelDigestPolicy.EnsureMatches(expectedPreviousDigest, existing.Digest);
        }
        else if (existing is not null)
        {
            OllamaModelDigestPolicy.EnsureMatches(existing.Digest, digest);
            if (IsRegistryTrustedPin(existing))
                return;
        }

        var manifest = new OllamaModelDigestManifest(
            registryManifest.ModelId,
            digest.ToLowerInvariant(),
            existing?.VerifiedAtUtc ?? DateTimeOffset.UtcNow,
            OllamaRegistryManifestClient.RegistryHost,
            registryManifest.ManifestUri,
            registryManifest.MediaType,
            registryManifest.ManifestSizeBytes,
            registryManifest.RetrievedAtUtc,
            registryManifest.ManifestDigest,
            registryManifest.ModelDigests.ToArray());
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

            // First install is create-only. A legacy TOFU pin is promoted only after
            // its installed digest appears in the independently retrieved Registry manifest.
            if (existing is null)
                File.Move(temporary, targetPath);
            else
                File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The Qwen digest path has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Qwen digest pin cannot be stored through a reparse-point directory.");
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

    private sealed record OllamaServerLease(int ProcessId, DateTimeOffset StartTimeUtc);

    private static string ServerLeasePath => Path.Combine(ModelsDirectory, ".layal-server-lease.json");

    private static void WriteConfiguredServerLease(Process server)
    {
        var directory = Path.GetFullPath(ModelsDirectory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        var lease = new OllamaServerLease(server.Id, server.StartTime.ToUniversalTime());
        var temporary = ServerLeasePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(lease));
            File.Move(temporary, ServerLeasePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static bool IsOwnedConfiguredServerRunning()
    {
        try
        {
            if (!File.Exists(ServerLeasePath) ||
                (File.GetAttributes(ServerLeasePath) & FileAttributes.ReparsePoint) != 0)
                return false;
            var lease = JsonSerializer.Deserialize<OllamaServerLease>(File.ReadAllText(ServerLeasePath));
            if (lease is null || lease.ProcessId <= 0)
                return false;
            using var process = Process.GetProcessById(lease.ProcessId);
            if (process.HasExited ||
                !string.Equals(process.ProcessName, "ollama", StringComparison.OrdinalIgnoreCase))
                return false;
            var actualStart = process.StartTime.ToUniversalTime();
            return Math.Abs((actualStart - lease.StartTimeUtc.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task EnsureServerReadyAsync(
        string executable,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var alreadyRunning = false;
        try
        {
            await _chatService.TestConnectionAsync(
                LocalChatModelService.DefaultBaseUrl, cancellationToken).ConfigureAwait(false);
            alreadyRunning = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Start the local daemon below, then poll only the loopback endpoint.
        }

        if (alreadyRunning)
        {
            if (UsesCustomModelsDirectory && !IsOwnedConfiguredServerRunning())
                throw new InvalidOperationException(
                    "An Ollama server is already running outside Layal's selected model directory. Exit Ollama completely, then restart Layal so it can start the server with the configured OLLAMA_MODELS path.");
            return;
        }

        progress?.Report("تشغيل خادم Ollama المحلي…");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("serve");
        start.Environment["OLLAMA_MODELS"] = ModelsDirectory;

        try
        {
            using var server = Process.Start(start);
            if (server is not null && UsesCustomModelsDirectory)
                WriteConfiguredServerLease(server);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
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
