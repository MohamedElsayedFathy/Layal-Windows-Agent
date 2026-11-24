using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Layal.Services;

public sealed record VoiceTutGenerationSettings(
    string VoiceDesignInstruction,
    int QualitySteps,
    double GuidanceScale,
    double Speed,
    bool NormalizeText);

public sealed class VoiceTutLocalService : IDisposable
{
    private sealed class ResourceScopedProgress(
        IProgress<LocalSpeechProgress> inner,
        string resourceId) : IProgress<LocalSpeechProgress>
    {
        public void Report(LocalSpeechProgress value) =>
            inner.Report(value with { ResourceId = resourceId });
    }

    public const string ModelId = "mohammedaly22/VoiceTut-TTS";
    public static readonly string ModelRevision = "b36bdf4416864d84f47ca77ce3decf34f6ab16cc";
    public const string OmniVoiceCommit = "08be0b4ccbac3e13e374e86fbfead4b4cac343e2";
    public const string VoiceTutCommit = "44cba00cd3ddb04b5213353e19e960045e332da7";
    public static readonly string DefaultVoiceDesign = "female, child, very high pitch";

    private const string InstallMarker = "voicetut-install.json";
    private const string PythonEnvironmentFreeze = "python-packages.freeze.txt";
    private const int MaximumTextCharacters = 6000;
    private static readonly TimeSpan ProcessInstallTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan ModelStartupTimeout = TimeSpan.FromMinutes(20);
    private static readonly HttpClient ApiClient = new(CreateApiHttpHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static SocketsHttpHandler CreateApiHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    private readonly object _processSync = new();
    private readonly LocalResourceSetupCoordinator _resourceCoordinator;
    private Process? _serverProcess;
    private string? _serverToken;
    private Uri? _serverBaseUri;
    private string? _pythonExecutable;
    private string _lastStartupLog = string.Empty;
    private bool _disposed;

    public VoiceTutLocalService()
        : this(new LocalResourceSetupCoordinator(new LocalResourceManager()))
    {
    }

    public VoiceTutLocalService(LocalResourceSetupCoordinator resourceCoordinator) =>
        _resourceCoordinator = resourceCoordinator ?? throw new ArgumentNullException(nameof(resourceCoordinator));

    public bool IsReady => File.Exists(PythonExecutablePath) &&
                           File.Exists(Path.Combine(InstallRoot, InstallMarker)) &&
                           File.Exists(Path.Combine(InstallRoot, PythonEnvironmentFreeze)) &&
                           File.Exists(ModelWeightsPath) &&
                           File.Exists(ModelTokenizerPath);

    public string LastMetricsSummary { get; private set; } = string.Empty;
    public string RuntimeDirectory => InstallRoot;

    private static string Root => LocalVoiceService.DataRoot;
    private static string InstallRoot => Path.Combine(Root, "voicetut");
    private static string VirtualEnvironment => Path.Combine(InstallRoot, "venv");
    private static string PythonExecutablePath => Path.Combine(VirtualEnvironment, "Scripts", "python.exe");
    private const string ManagedPythonVersion = "3.13.17";
    private static string ManagedPythonRoot => Path.Combine(Root, "python-runtime", ManagedPythonVersion);
    private static string ManagedPythonExecutablePath => Path.Combine(ManagedPythonRoot, "python.exe");
    private static string ModelCacheRoot => Path.Combine(Root, "hf-cache");
    private static string ModelSnapshotRoot => Path.Combine(
        ModelCacheRoot, "hub", "models--mohammedaly22--VoiceTut-TTS", "snapshots", ModelRevision);
    private static string ModelWeightsPath => Path.Combine(ModelSnapshotRoot, "model.safetensors");
    private static string ModelTokenizerPath => Path.Combine(ModelSnapshotRoot, "tokenizer.json");
    private static string BridgeScriptPath => Path.Combine(AppContext.BaseDirectory, "VoiceTut", "voicetut_local_server.py");

    public async Task EnsureReadyAsync(
        IProgress<LocalSpeechProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("VoiceTut local runtime currently requires 64-bit Windows.");

        Directory.CreateDirectory(InstallRoot);
        Directory.CreateDirectory(ModelCacheRoot);
        if (!File.Exists(BridgeScriptPath))
            throw new FileNotFoundException("ملف تشغيل VoiceTut المحلي غير موجود ضمن نسخة Layal المنشورة.", BridgeScriptPath);

        var manifestPath = Path.Combine(InstallRoot, InstallMarker);
        var expectedManifest = $"model={ModelId}\nrevision={ModelRevision}\nomnivoice={OmniVoiceCommit}\nvoicetut={VoiceTutCommit}\ntorch=2.8.0\n";
        var installMatches = File.Exists(PythonExecutablePath) &&
            File.Exists(manifestPath) &&
            File.Exists(Path.Combine(InstallRoot, PythonEnvironmentFreeze)) &&
            string.Equals(await File.ReadAllTextAsync(manifestPath, cancellationToken), expectedManifest, StringComparison.Ordinal);

        // A marker and freeze file are not proof that the environment is still healthy.
        // Re-run the bounded import/version checks before trusting an existing venv.
        if (installMatches)
        {
            _pythonExecutable = PythonExecutablePath;
            installMatches =
                await VerifyPythonRuntimeAsync(cancellationToken).ConfigureAwait(false) &&
                await VerifyTorchRuntimeAsync(cancellationToken).ConfigureAwait(false) &&
                await VerifyOmniVoicePackageAsync(cancellationToken).ConfigureAwait(false) &&
                await VerifyVoiceTutPackageAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!installMatches)
        {
            progress?.Report(new LocalSpeechProgress(
                "فحص Python 3.11–3.13 وتجهيز بيئة VoiceTut المعزولة…",
                5,
                ResourceId: LocalResourceIds.PythonRuntime));
            if (File.Exists(PythonExecutablePath) &&
                !await VerifyPythonRuntimeAsync(cancellationToken).ConfigureAwait(false))
            {
                DeleteManagedPythonEnvironment();
            }
            else if (!File.Exists(PythonExecutablePath) && Directory.Exists(VirtualEnvironment))
            {
                // A prior cancelled venv creation may leave a partial app-managed directory.
                DeleteManagedPythonEnvironment();
            }

            if (!File.Exists(PythonExecutablePath))
            {
                var pythonLauncher = await FindPythonLauncherAsync(cancellationToken);
                if (pythonLauncher is null)
                {
                    var managedPython = await InstallManagedPythonRuntimeAsync(progress, cancellationToken)
                        .ConfigureAwait(false);
                    pythonLauncher = (managedPython, Array.Empty<string>());
                }

                var venvArgs = pythonLauncher.Value.PrefixArguments
                    .Concat(new[] { "-m", "venv", VirtualEnvironment }).ToArray();
                await RunCommandAsync(
                    pythonLauncher.Value.Executable,
                    venvArgs,
                    ProcessInstallTimeout,
                    cancellationToken,
                    "إنشاء بيئة Python المعزولة").ConfigureAwait(false);
            }

            _pythonExecutable = PythonExecutablePath;
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.PythonRuntime,
                VerifyPythonRuntimeAsync,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new LocalSpeechProgress(
                "تثبيت PyTorch محليًا (CUDA إن كان تشغيلها مدعومًا، وإلا CPU)…",
                18,
                ResourceId: LocalResourceIds.PyTorch));
            var cudaDeviceDetected = await IsNvidiaRuntimeAvailableAsync(cancellationToken).ConfigureAwait(false);
            await _resourceCoordinator.ExecuteAsync(
                LocalResourceIds.PyTorch,
                async token =>
                {
                    await RunPythonAsync(new[]
                    {
                        "-m", "pip", "install", "--disable-pip-version-check",
                        "--upgrade", "pip"
                    }, ProcessInstallTimeout, token, "تحديث pip", progress, LocalResourceIds.PyTorch).ConfigureAwait(false);

                    var torchIndex = cudaDeviceDetected
                        ? "https://download.pytorch.org/whl/cu128"
                        : "https://download.pytorch.org/whl/cpu";
                    await RunPythonAsync(new[]
                    {
                        "-m", "pip", "install", "--disable-pip-version-check",
                        "--index-url", torchIndex, "--extra-index-url", "https://pypi.org/simple",
                        "torch==2.8.0", "torchaudio==2.8.0"
                    }, ProcessInstallTimeout, token, "تثبيت PyTorch", progress, LocalResourceIds.PyTorch).ConfigureAwait(false);

                    if (cudaDeviceDetected)
                    {
                        var cudaProbe = await RunCommandAsync(
                            PythonExecutablePath,
                            new[] { "-c", "import torch; print('CUDA_AVAILABLE=' + str(torch.cuda.is_available()))" },
                            TimeSpan.FromMinutes(2), token, "فحص تشغيل CUDA").ConfigureAwait(false);
                        if (!cudaProbe.Output.Contains("CUDA_AVAILABLE=True", StringComparison.OrdinalIgnoreCase))
                        {
                            progress?.Report(new LocalSpeechProgress(
                                "لم يؤكد PyTorch قابلية تشغيل CUDA؛ جارٍ التحويل إلى إصدار CPU المتوافق دون تغيير تعريف بطاقة الرسوميات.",
                                ResourceId: LocalResourceIds.PyTorch));
                            await RunPythonAsync(new[]
                            {
                                "-m", "pip", "install", "--disable-pip-version-check", "--force-reinstall",
                                "--index-url", "https://download.pytorch.org/whl/cpu",
                                "--extra-index-url", "https://pypi.org/simple",
                                "torch==2.8.0", "torchaudio==2.8.0"
                            }, ProcessInstallTimeout, token, "تثبيت PyTorch CPU", progress, LocalResourceIds.PyTorch).ConfigureAwait(false);
                        }
                    }
                },
                VerifyTorchRuntimeAsync,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new LocalSpeechProgress(
                "تثبيت OmniVoice بإصدار Git ثابت…", 42,
                ResourceId: LocalResourceIds.OmniVoice));
            await _resourceCoordinator.ExecuteAsync(
                LocalResourceIds.OmniVoice,
                token => RunPythonAsync(new[]
                {
                    "-m", "pip", "install", "--disable-pip-version-check",
                    $"https://github.com/k2-fsa/OmniVoice/archive/{OmniVoiceCommit}.zip"
                }, ProcessInstallTimeout, token, "تثبيت OmniVoice", progress, LocalResourceIds.OmniVoice),
                VerifyOmniVoicePackageAsync,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new LocalSpeechProgress(
                "تثبيت VoiceTut بإصدار Git ثابت…",
                ResourceId: LocalResourceIds.VoiceTut));
            await _resourceCoordinator.ExecuteAsync(
                LocalResourceIds.VoiceTut,
                token => RunPythonAsync(new[]
                {
                    "-m", "pip", "install", "--disable-pip-version-check",
                    $"https://github.com/MohammedAly22/VoiceTuT-TTS/archive/{VoiceTutCommit}.zip"
                }, ProcessInstallTimeout, token, "تثبيت VoiceTut", progress, LocalResourceIds.VoiceTut),
                VerifyVoiceTutPackageAsync,
                cancellationToken).ConfigureAwait(false);

            await RunPythonAsync(
                new[] { "-m", "pip", "check" },
                TimeSpan.FromMinutes(3),
                cancellationToken,
                "فحص اعتماديات Python",
                progress).ConfigureAwait(false);

            var packageInventory = await RunCommandAsync(
                PythonExecutablePath,
                new[] { "-m", "pip", "freeze", "--all" },
                TimeSpan.FromMinutes(2),
                cancellationToken,
                "تسجيل إصدارات مكتبات Python").ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(InstallRoot, PythonEnvironmentFreeze),
                packageInventory.Output,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                manifestPath, expectedManifest, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _pythonExecutable = PythonExecutablePath;
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.PythonRuntime, VerifyPythonRuntimeAsync, cancellationToken).ConfigureAwait(false);
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.PyTorch, VerifyTorchRuntimeAsync, cancellationToken).ConfigureAwait(false);
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.OmniVoice, VerifyOmniVoicePackageAsync, cancellationToken).ConfigureAwait(false);
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.VoiceTut, VerifyVoiceTutPackageAsync, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new LocalSpeechProgress(
            "تحميل أوزان VoiceTut المثبتة والتحقق من نسخة النموذج…",
            62,
            ResourceId: LocalResourceIds.VoiceTutModel));
        var modelDefinition = LocalResourceCatalog.GetRequired(LocalResourceIds.VoiceTutModel);
        if (modelDefinition.ExpectedFiles is null)
            throw new InvalidDataException("VoiceTut model file integrity pins are missing from the resource catalog.");
        var missingModelBytes = modelDefinition.ExpectedFiles.Sum(file => file.ExpectedSizeBytes);
        try
        {
            await LocalResourceFileIntegrityVerifier.VerifyAsync(
                ModelSnapshotRoot, ModelCacheRoot, modelDefinition.ExpectedFiles, cancellationToken)
                .ConfigureAwait(false);
            missingModelBytes = 0;
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("outside", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("escapes", StringComparison.OrdinalIgnoreCase))
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Missing or corrupted pinned files will be repaired by snapshot_download below.
        }
        LocalResourceStorageCapacityGuard.EnsureAvailable(
            ModelCacheRoot, missingModelBytes, missingModelBytes);

        await _resourceCoordinator.ExecuteAsync(
            LocalResourceIds.VoiceTutModel,
            token => EnsureServerStartedAsync(token, progress),
            async token => File.Exists(ModelWeightsPath) && File.Exists(ModelTokenizerPath) &&
                await IsServerHealthyAsync(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (!IsReady)
            throw new InvalidOperationException("لم يؤكد VoiceTut اكتمال تثبيت الموارد المحلية المطلوبة.");

        progress?.Report(new LocalSpeechProgress(
            "VoiceTut جاهز محليًا: Voice Design فقط، دون أصوات مرجعية أو استنساخ.",
            100,
            ResourceId: LocalResourceIds.VoiceTutModel));
    }

    public async Task<bool> ValidateCachedResourcesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem || !IsReady)
            return false;

        try
        {
            _pythonExecutable = PythonExecutablePath;
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.PythonRuntime, VerifyPythonRuntimeAsync, cancellationToken).ConfigureAwait(false);
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.PyTorch, VerifyTorchRuntimeAsync, cancellationToken).ConfigureAwait(false);
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.OmniVoice, VerifyOmniVoicePackageAsync, cancellationToken).ConfigureAwait(false);
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.VoiceTut, VerifyVoiceTutPackageAsync, cancellationToken).ConfigureAwait(false);
            var modelDefinition = LocalResourceCatalog.GetRequired(LocalResourceIds.VoiceTutModel);
            if (modelDefinition.ExpectedFiles is null)
                throw new InvalidDataException("VoiceTut model file integrity pins are missing from the resource catalog.");
            await LocalResourceFileIntegrityVerifier.VerifyAsync(
                ModelSnapshotRoot, ModelCacheRoot, modelDefinition.ExpectedFiles, cancellationToken)
                .ConfigureAwait(false);
            await _resourceCoordinator.EnsureReadyAsync(
                LocalResourceIds.VoiceTutModel,
                _ => Task.FromResult(File.Exists(ModelWeightsPath) && File.Exists(ModelTokenizerPath)),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or TimeoutException)
        {
            return false;
        }
    }

    private static void DeleteManagedPythonEnvironment()
    {
        if (!Directory.Exists(VirtualEnvironment)) return;
        EnsureManagedEnvironmentHasNoReparsePoints(VirtualEnvironment);
        Directory.Delete(VirtualEnvironment, recursive: true);
        if (Directory.Exists(VirtualEnvironment))
            throw new IOException("The damaged application-managed Python environment could not be removed.");
    }

    private static void EnsureManagedEnvironmentHasNoReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Refusing to repair the Python environment because its directory is a reparse point.");

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Refusing to repair the Python environment because it contains a reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
                EnsureManagedEnvironmentHasNoReparsePoints(entry);
        }
    }

    private async Task<bool> VerifyPythonRuntimeAsync(CancellationToken cancellationToken) =>
        await VerifyPythonExecutableAsync(PythonExecutablePath, null, cancellationToken).ConfigureAwait(false);

    private async Task<bool> VerifyPythonExecutableAsync(
        string executable,
        string? requiredVersion,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable) ||
            (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            return false;
        try
        {
            var result = await RunCommandAsync(
                executable,
                new[]
                {
                    "-c",
                    "import struct,sys; print('PYTHON=' + '.'.join(map(str,sys.version_info[:3]))); print('BITS=' + str(struct.calcsize('P')*8)); raise SystemExit(0 if (3,11) <= sys.version_info[:2] < (3,14) and struct.calcsize('P') == 8 else 3)"
                },
                TimeSpan.FromSeconds(30),
                cancellationToken,
                "التحقق من Python x64").ConfigureAwait(false);
            if (!result.Output.Contains("BITS=64", StringComparison.Ordinal))
                return false;
            if (requiredVersion is not null &&
                !result.Output.Contains("PYTHON=" + requiredVersion, StringComparison.Ordinal))
                return false;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    private async Task<bool> VerifyTorchRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunCommandAsync(
                PythonExecutablePath,
                new[]
                {
                    "-c",
                    "import torch,torchaudio; print('TORCH=' + torch.__version__.split('+')[0]); print('TORCHAUDIO=' + torchaudio.__version__.split('+')[0]); raise SystemExit(0 if torch.__version__.split('+')[0] == '2.8.0' and torchaudio.__version__.split('+')[0] == '2.8.0' else 3)"
                },
                TimeSpan.FromMinutes(2),
                cancellationToken,
                "التحقق من PyTorch وTorchAudio").ConfigureAwait(false);
            return result.Output.Contains("TORCH=2.8.0", StringComparison.Ordinal) &&
                   result.Output.Contains("TORCHAUDIO=2.8.0", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    private async Task<bool> VerifyOmniVoicePackageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCommandAsync(
                PythonExecutablePath,
                new[] { "-c", "import omnivoice" },
                TimeSpan.FromMinutes(2),
                cancellationToken,
                "التحقق من OmniVoice").ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    private async Task<bool> VerifyVoiceTutPackageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCommandAsync(
                PythonExecutablePath,
                new[] { "-c", "import voicetut_tts" },
                TimeSpan.FromMinutes(2),
                cancellationToken,
                "التحقق من VoiceTut").ConfigureAwait(false);
            await RunCommandAsync(
                PythonExecutablePath,
                new[] { "-m", "pip", "check" },
                TimeSpan.FromMinutes(3),
                cancellationToken,
                "فحص اعتماديات Python").ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    public async Task SpeakAsync(
        string text,
        string? outputDeviceId,
        VoiceTutGenerationSettings settings,
        CancellationToken cancellationToken,
        string? languageCode = null)
    {
        ThrowIfDisposed();
        var value = LocalVoiceService.NormalizeTextForSpeech(text);
        if (value.Length == 0) return;
        if (value.Length > MaximumTextCharacters)
            value = value[..MaximumTextCharacters];

        if (!await IsServerHealthyAsync(cancellationToken))
            await EnsureServerStartedAsync(cancellationToken, null);

        var hasArabic = value.Any(IsArabicLetter);
        var fallbackLanguage = hasArabic ? "arz" : "en";
        var language = string.IsNullOrWhiteSpace(languageCode)
            ? fallbackLanguage
            : languageCode.Trim().ToLowerInvariant();
        if (language is not ("arz" or "ar" or "en"))
            language = fallbackLanguage;
        var instruction = ValidateVoiceInstruction(settings.VoiceDesignInstruction);
        var requestBody = JsonSerializer.Serialize(new
        {
            text = value,
            language,
            instruct = instruction,
            num_steps = Math.Clamp(settings.QualitySteps, 4, 64),
            guidance_scale = Math.Clamp(settings.GuidanceScale, 0.0, 6.0),
            speed = Math.Clamp(settings.Speed, 0.5, 2.0),
            normalize = settings.NormalizeText
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_serverBaseUri!, "/v1/stream"))
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Layal-Token", _serverToken);
        using var response = await ApiClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"VoiceTut local API returned {(int)response.StatusCode}: {body[..Math.Min(700, body.Length)]}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        BufferedWaveProvider? buffer = null;
        IWavePlayer? output = null;
        TaskCompletionSource<StoppedEventArgs>? stopped = null;
        EventHandler<StoppedEventArgs>? stoppedHandler = null;
        var playbackStarted = false;
        var completed = false;
        var totalAudioSeconds = 0.0;
        CancellationTokenRegistration playbackCancellationRegistration = default;
        var drainDeadline = DateTimeOffset.UtcNow.AddMinutes(8);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var kind = GetString(root, "type");
                if (kind == "meta")
                {
                    var sampleRate = GetInt32(root, "sampleRate");
                    if (sampleRate is < 8000 or > 48000)
                        throw new InvalidOperationException("VoiceTut returned an unsupported audio sample rate.");
                    // NAudio 3.x exposes a fixed five-second buffer. Feed it in partial
                    // writes below so a model chunk larger than the buffer never deadlocks.
                    buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
                    {
                        DiscardOnBufferOverflow = false,
                        ReadFully = true
                    };
                    output = UsesWasapiForDefaultOutput(outputDeviceId)
                        ? new WasapiPlayerBuilder().Build()
                        : new WaveOut { DeviceNumber = AudioDeviceService.ResolveOutputDeviceNumber(outputDeviceId) };
                    stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                    stoppedHandler = (_, args) => stopped.TrySetResult(args);
                    output.PlaybackStopped += stoppedHandler;
                    output.Init(buffer);
                }
                else if (kind == "chunk")
                {
                    if (buffer is null || output is null)
                        throw new InvalidOperationException("VoiceTut streamed audio before its audio metadata.");
                    var encoded = GetString(root, "pcm16");
                    var pcm = Convert.FromBase64String(encoded);
                    try
                    {
                        if (pcm.Length == 0 || pcm.Length > 4 * 1024 * 1024 || pcm.Length % 2 != 0)
                            throw new InvalidOperationException("VoiceTut returned an invalid audio chunk.");
                        var offset = 0;
                        while (offset < pcm.Length)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (stopped?.Task.IsCompleted == true)
                                throw new InvalidOperationException("Audio playback stopped before the stream finished.");

                            var availableBytes = buffer.BufferLength - buffer.BufferedBytes;
                            if (availableBytes < 2)
                            {
                                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            // Keep PCM16 sample boundaries intact and never overrun NAudio's
                            // fixed circular buffer, even if one model chunk is longer than 5s.
                            var writeCount = Math.Min(availableBytes, pcm.Length - offset);
                            writeCount -= writeCount % 2;
                            if (writeCount <= 0)
                            {
                                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            buffer.AddSamples(pcm, offset, writeCount);
                            offset += writeCount;
                            totalAudioSeconds += writeCount / (double)(buffer.WaveFormat.AverageBytesPerSecond);

                            if (!playbackStarted)
                            {
                                playbackCancellationRegistration = cancellationToken.Register(() =>
                                {
                                    try { output.Stop(); } catch { /* Output may already be stopped. */ }
                                });
                                output.Play();
                                playbackStarted = true;
                            }
                        }
                    }
                    finally
                    {
                        Array.Clear(pcm, 0, pcm.Length);
                    }
                }
                else if (kind == "done")
                {
                    completed = true;
                    if (root.TryGetProperty("metrics", out var metrics))
                        LastMetricsSummary = FormatMetrics(metrics);
                }
                else if (kind == "error")
                {
                    throw new InvalidOperationException(GetString(root, "message"));
                }
            }

            if (!completed)
                throw new InvalidOperationException("انقطع بث VoiceTut قبل اكتماله.");

            if (playbackStarted && buffer is not null && output is not null && stopped is not null)
            {
                while (buffer.BufferedBytes > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow > drainDeadline)
                        throw new TimeoutException("انتهى وقت انتظار تشغيل مقاطع الصوت المتبقية.");
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
                output.Stop();
                var stopResult = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (stopResult.Exception is not null)
                    throw new InvalidOperationException("تعذر تشغيل صوت VoiceTut على جهاز الإخراج المحدد.", stopResult.Exception);
            }
            _ = totalAudioSeconds;
        }
        catch
        {
            try { output?.Stop(); } catch { /* Best-effort stop on cancellation/failure. */ }
            throw;
        }
        finally
        {
            playbackCancellationRegistration.Dispose();
            if (output is not null)
            {
                if (stoppedHandler is not null)
                    output.PlaybackStopped -= stoppedHandler;
                output.Dispose();
            }
        }
    }

    private async Task EnsureServerStartedAsync(
        CancellationToken cancellationToken,
        IProgress<LocalSpeechProgress>? progress)
    {
        if (await IsServerHealthyAsync(cancellationToken)) return;
        if (!File.Exists(PythonExecutablePath) || !File.Exists(Path.Combine(InstallRoot, InstallMarker)))
            throw new InvalidOperationException("جهّز VoiceTut من إعدادات الصوت المحلي أولًا.");

        StopServer();
        var port = GetFreeLoopbackPort();
        _serverToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _serverBaseUri = new Uri($"http://127.0.0.1:{port}/");
        var start = new ProcessStartInfo(PythonExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = InstallRoot
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(BridgeScriptPath);
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.Environment["LAYAL_VOICETUT_TOKEN"] = _serverToken;
        start.Environment["HF_HOME"] = ModelCacheRoot;
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["TOKENIZERS_PARALLELISM"] = "false";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            AppendStartupLog(e.Data);
            Trace.WriteLine("[VoiceTut] " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            AppendStartupLog(e.Data);
            Trace.WriteLine("[VoiceTut] " + e.Data);
            progress?.Report(new LocalSpeechProgress(
                e.Data, TryParsePercent(e.Data), ResourceId: LocalResourceIds.VoiceTutModel));
        };
        process.Exited += (_, _) => Trace.TraceError("VoiceTut local server exited with code {0}.", TryGetExitCode(process));
        if (!process.Start())
            throw new InvalidOperationException("تعذر تشغيل خادم VoiceTut المحلي.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_processSync) _serverProcess = process;

        progress?.Report(new LocalSpeechProgress(
            "تشغيل نموذج VoiceTut محليًا لأول مرة؛ يُجهّز النموذج ويُحمّل الأوزان…",
            72,
            ResourceId: LocalResourceIds.VoiceTutModel));
        try
        {
            var deadline = DateTimeOffset.UtcNow.Add(ModelStartupTimeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException("توقّف خادم VoiceTut أثناء بدء التشغيل. " + _lastStartupLog);
                if (await IsServerHealthyAsync(cancellationToken))
                    return;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("لم يصبح خادم VoiceTut المحلي جاهزًا خلال المهلة المحددة. " + _lastStartupLog);
        }
        catch
        {
            StopServer();
            throw;
        }
    }

    private async Task<bool> IsServerHealthyAsync(CancellationToken cancellationToken)
    {
        if (_serverBaseUri is null || string.IsNullOrWhiteSpace(_serverToken) ||
            _serverProcess is null || _serverProcess.HasExited)
            return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serverBaseUri, "healthz"));
            request.Headers.TryAddWithoutValidation("X-Layal-Token", _serverToken);
            using var response = await ApiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True &&
                   doc.RootElement.TryGetProperty("integrityVerified", out var integrity) &&
                   integrity.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task RunPythonAsync(
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string operation,
        IProgress<LocalSpeechProgress>? progress = null,
        string? resourceId = null)
    {
        IProgress<LocalSpeechProgress>? scopedProgress = progress;
        if (progress is not null && !string.IsNullOrWhiteSpace(resourceId))
            scopedProgress = new ResourceScopedProgress(progress, resourceId);

        await RunCommandAsync(
            PythonExecutablePath, arguments, timeout, cancellationToken, operation, scopedProgress)
            .ConfigureAwait(false);
    }

    private async Task<string> InstallManagedPythonRuntimeAsync(
        IProgress<LocalSpeechProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resource = LocalResourceCatalog.GetRequired(LocalResourceIds.PythonRuntime);
        var downloadsDirectory = Path.Combine(Root, "downloads");
        Directory.CreateDirectory(downloadsDirectory);
        EnsureNoReparsePoints(downloadsDirectory);
        var installerPath = Path.Combine(downloadsDirectory, "python-3.13.17-amd64.exe");
        var installRoot = ManagedPythonRoot;
        if (!await MatchesPinnedResourceFileAsync(installerPath, resource, cancellationToken).ConfigureAwait(false))
            LocalResourceStorageCapacityGuard.EnsureAvailable(
                downloadsDirectory,
                resource.ExpectedSizeBytes!.Value,
                checked(resource.ExpectedSizeBytes.Value * 4));

        progress?.Report(new LocalSpeechProgress(
            "لم يتم العثور على Python x64 متوافق؛ جارٍ تنزيل Python 3.13.17 الرسمي إلى مجلد ليال…",
            6,
            ResourceId: LocalResourceIds.PythonRuntime));

        var downloadProgress = new Progress<LocalResourceDownloadProgress>(state =>
        {
            var percent = state.TotalBytes > 0
                ? Math.Clamp(state.DownloadedBytes * 100d / state.TotalBytes, 0d, 100d)
                : (double?)null;
            var speed = state.BytesPerSecond > 0
                ? (state.BytesPerSecond / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB/s"
                : "—";
            progress?.Report(new LocalSpeechProgress(
                $"تنزيل Python: {state.DownloadedBytes:N0} / {state.TotalBytes:N0} bytes — {speed}",
                percent,
                state.DownloadedBytes,
                state.TotalBytes,
                state.BytesPerSecond,
                state.RemainingSeconds,
                LocalResourceIds.PythonRuntime));
        });

        await _resourceCoordinator.DownloadAsync(
            resource,
            installerPath,
            downloadProgress,
            cancellationToken,
            async token =>
            {
                if (!await VerifyPythonInstallerAuthenticodeAsync(installerPath, token).ConfigureAwait(false))
                    return false;

                EnsureManagedPythonRootSafe();
                if (Directory.Exists(installRoot))
                    DeleteManagedPythonRuntime();

                Directory.CreateDirectory(installRoot);
                EnsureNoReparsePoints(installRoot);

                var arguments = new[]
                {
                    "/quiet",
                    "InstallAllUsers=0",
                    "TargetDir=" + installRoot,
                    "PrependPath=0",
                    "AppendPath=0",
                    "Include_launcher=0",
                    "Include_pip=1",
                    "Include_tcltk=0",
                    "Include_test=0",
                    "Include_doc=0",
                    "Shortcuts=0",
                    "AssociateFiles=0"
                };
                progress?.Report(new LocalSpeechProgress(
                    "تم التحقق من ناشر Python؛ جارٍ تثبيت البيئة الخاصة بليال دون تعديل PATH…",
                    96,
                    ResourceId: LocalResourceIds.PythonRuntime));
                await RunCommandAsync(
                    installerPath,
                    arguments,
                    TimeSpan.FromMinutes(10),
                    token,
                    "تثبيت Python الرسمي داخل مجلد ليال",
                    progress).ConfigureAwait(false);

                return await VerifyPythonExecutableAsync(
                    ManagedPythonExecutablePath, ManagedPythonVersion, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (!await VerifyPythonExecutableAsync(
                ManagedPythonExecutablePath, ManagedPythonVersion, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("لم يجتز Python المثبّت داخل مجلد ليال فحص الإصدار والمعمارية.");

        progress?.Report(new LocalSpeechProgress(
            "أصبح Python 3.13.17 x64 جاهزًا داخل مجلد بيانات ليال.",
            100,
            ResourceId: LocalResourceIds.PythonRuntime));
        return ManagedPythonExecutablePath;
    }

    private static async Task<bool> MatchesPinnedResourceFileAsync(
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
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var expected = Convert.FromHexString(resource.ExpectedSha256!);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void EnsureManagedPythonRootSafe()
    {
        var root = Path.GetFullPath(Root);
        var managed = Path.GetFullPath(ManagedPythonRoot);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!managed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The managed Python runtime path escaped Layal's LocalAi directory.");
        EnsureNoReparsePoints(root);
        if (Directory.Exists(managed) &&
            (File.GetAttributes(managed) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to replace the managed Python runtime through a reparse point.");
    }

    private static void DeleteManagedPythonRuntime()
    {
        EnsureManagedPythonRootSafe();
        var directory = ManagedPythonRoot;
        if (!Directory.Exists(directory)) return;
        EnsureManagedTreeHasNoReparsePoints(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static void EnsureManagedTreeHasNoReparsePoints(string directory)
    {
        var info = new DirectoryInfo(directory);
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Refusing to repair the managed Python runtime because it contains a reparse point.");
            if ((entry.Attributes & FileAttributes.Directory) != 0)
                EnsureManagedTreeHasNoReparsePoints(entry.FullName);
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The managed resource path has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to use a managed Python path containing a reparse point.");
        }
    }

    private static async Task<bool> VerifyPythonInstallerAuthenticodeAsync(
        string installerPath,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(installerPath))
            return false;

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return false;

        var literalPath = "'" + installerPath.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script =
            "$ErrorActionPreference='Stop'; " +
            "$signature=Get-AuthenticodeSignature -LiteralPath " + literalPath + "; " +
            "if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) { exit 17 }; " +
            "$subject=$signature.SignerCertificate.Subject; " +
            "if ($subject -notmatch '(^|, )O=Python Software Foundation(,|$)') { exit 18 }; " +
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
            exception is InvalidOperationException or Win32Exception)
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
                   subject.Contains("O=Python Software Foundation", StringComparison.OrdinalIgnoreCase);
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

    private async Task<(string Executable, string[] PrefixArguments)?> FindPythonLauncherAsync(
        CancellationToken cancellationToken)
    {
        var launchers = new[]
        {
            (ManagedPythonExecutablePath, Array.Empty<string>()),
            ("py", new[] { "-3.13" }),
            ("py", new[] { "-3.12" }),
            ("py", new[] { "-3.11" }),
            ("python", Array.Empty<string>())
        };
        foreach (var (executable, prefix) in launchers)
        {
            try
            {
                var args = prefix.Concat(new[]
                {
                    "-c",
                    "import struct,sys; raise SystemExit(0 if (3,11) <= sys.version_info[:2] < (3,14) and struct.calcsize('P') == 8 else 2)"
                }).ToArray();
                var result = await RunCommandAsync(executable, args, TimeSpan.FromSeconds(15), cancellationToken, "فحص Python");
                if (result.ExitCode == 0)
                    return (executable, prefix);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
            {
                // Try the next installed Python launcher/version.
            }
        }
        return null;
    }

    private async Task<bool> IsNvidiaRuntimeAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunCommandAsync("nvidia-smi", new[] { "-L" },
                TimeSpan.FromSeconds(8), cancellationToken, "فحص بطاقة NVIDIA");
            return result.ExitCode == 0 && result.Output.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string operation,
        IProgress<LocalSpeechProgress>? progress = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var sharedTemporary = Path.Combine(Root, "temp");
        Directory.CreateDirectory(sharedTemporary);
        EnsureNoReparsePoints(sharedTemporary);
        start.Environment["TEMP"] = sharedTemporary;
        start.Environment["TMP"] = sharedTemporary;

        var isLayalPython = string.Equals(
            Path.GetFullPath(executable),
            Path.GetFullPath(PythonExecutablePath),
            StringComparison.OrdinalIgnoreCase);
        var isPipInstall = isLayalPython && arguments.Length >= 3 &&
            arguments[0] == "-m" && arguments[1] == "pip" && arguments[2] == "install";
        string? pipCapacityDirectory = null;
        if (isLayalPython)
        {
            var pipCache = Path.Combine(Root, "pip-cache");
            Directory.CreateDirectory(pipCache);
            EnsureNoReparsePoints(pipCache);
            start.Environment["PIP_CACHE_DIR"] = pipCache;
            if (isPipInstall)
                pipCapacityDirectory = Root;
        }
        if (isPipInstall)
        {
            start.ArgumentList.Add("-m");
            start.ArgumentList.Add("pip");
            start.ArgumentList.Add("install");
            start.ArgumentList.Add("--progress-bar");
            start.ArgumentList.Add("raw");
            foreach (var argument in arguments.Skip(3))
                start.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"تعذر بدء خطوة {operation}.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"تعذر العثور على الأداة المطلوبة لتنفيذ {operation}: {exception.Message}", exception);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdoutTask = PumpCommandOutputAsync(process.StandardOutput, progress, pipCapacityDirectory);
        var stderrTask = PumpCommandOutputAsync(process.StandardError, progress, pipCapacityDirectory);

        try
        {
            var exitTask = process.WaitForExitAsync(timeoutSource.Token);
            var firstCompleted = await Task.WhenAny(exitTask, stdoutTask, stderrTask).ConfigureAwait(false);
            if (firstCompleted.IsFaulted)
            {
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
                await firstCompleted.ConfigureAwait(false);
            }
            await exitTask.ConfigureAwait(false);
            var output = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var error = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = Truncate(error + "\n" + output, 1800);
                throw new InvalidOperationException(
                    $"{operation} failed ({process.ExitCode}): {detail}");
            }

            return (process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the cancellation or timeout that caused termination. */ }

            string output = string.Empty;
            string error = string.Empty;
            try { output = await stdoutTask.ConfigureAwait(false); }
            catch { }
            try { error = await stderrTask.ConfigureAwait(false); }
            catch { }

            if (cancellationToken.IsCancellationRequested)
                throw;

            throw new TimeoutException(
                $"انتهت المهلة أثناء {operation}. {Truncate(error + "\n" + output, 700)}");
        }
        catch
        {
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original command error. */ }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { /* Preserve the original command error. */ }
            throw;
        }
    }

    private static async Task<string> PumpCommandOutputAsync(
        StreamReader reader,
        IProgress<LocalSpeechProgress>? progress,
        string? pipCapacityDirectory = null)
    {
        var captured = new StringBuilder();
        const int maximumCaptureCharacters = 256 * 1024;

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (pipCapacityDirectory is not null &&
                PipDownloadSizeParser.TryParseAnnouncedBytes(line, out var announcedBytes))
                LocalResourceStorageCapacityGuard.EnsureAvailable(
                    pipCapacityDirectory,
                    announcedBytes,
                    checked(announcedBytes * 2));

            if (captured.Length < maximumCaptureCharacters)
            {
                var remaining = maximumCaptureCharacters - captured.Length;
                captured.AppendLine(Truncate(line, Math.Min(remaining, 2400)));
            }

            var clean = line.Trim();
            if (clean.Length > 0)
            {
                var message = clean.Length <= 240 ? clean : clean[..240] + "…";
                if (PipDownloadSizeParser.TryParseProgressLine(clean, out var pipProgress))
                {
                    progress?.Report(new LocalSpeechProgress(
                        message,
                        pipProgress.TotalBytes > 0
                            ? pipProgress.DownloadedBytes * 100d / pipProgress.TotalBytes
                            : null,
                        pipProgress.DownloadedBytes,
                        pipProgress.TotalBytes,
                        pipProgress.BytesPerSecond,
                        pipProgress.RemainingSeconds));
                }
                else
                {
                    progress?.Report(new LocalSpeechProgress(message, TryParsePercent(clean)));
                }
            }
        }

        return captured.ToString();
    }

    private static double? TryParsePercent(string text)
    {
        var percentIndex = text.IndexOf('%');
        if (percentIndex <= 0) return null;

        var start = percentIndex - 1;
        while (start >= 0 &&
               (char.IsDigit(text[start]) || text[start] is '.' or ','))
            start--;
        var number = text[(start + 1)..percentIndex].Replace(',', '.');
        if (!double.TryParse(
                number,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var percent) ||
            !double.IsFinite(percent) ||
            percent is < 0 or > 100)
            return null;
        return percent;
    }

    private static string ValidateVoiceInstruction(string? instruction)
    {
        var value = string.IsNullOrWhiteSpace(instruction) ? DefaultVoiceDesign : instruction.Trim();
        if (value.Length > 500 || !value.Contains("female", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("يجب أن يتضمن وصف الصوت كلمة female وألا يتجاوز 500 حرف.");
        if (new[] { "ref_audio", "reference audio", "speaker=", "clone", "voice cloning" }
            .Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("يدعم تكامل ليال تصميم الصوت فقط ولا يقبل استنساخ صوت أو ملفًا مرجعيًا.");
        return value;
    }

    private static bool IsArabicLetter(char value) =>
        value is >= '\u0621' and <= '\u063A' or >= '\u0641' and <= '\u064A' or
            >= '\u066E' and <= '\u06D3' or >= '\u06FA' and <= '\u06FC' or
            >= '\u0750' and <= '\u077F' or >= '\u08A0' and <= '\u08FF';

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool UsesWasapiForDefaultOutput(string? outputDeviceId) =>
        string.IsNullOrWhiteSpace(outputDeviceId) ||
        string.Equals(outputDeviceId.Trim(), AudioDeviceService.WindowsDefaultDeviceId, StringComparison.Ordinal);

    private static string FormatMetrics(JsonElement metrics)
    {
        static string Number(JsonElement root, string key, string format)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number)
                return "—";
            return value.GetDouble().ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
        var vram = Number(metrics, "peakVramMb", "0");
        var vramText = vram == "—" ? "n/a" : vram + " MB";
        return $"VoiceTut | TTFA: {Number(metrics, "timeToFirstAudioMs", "0")} ms | RTF: {Number(metrics, "realTimeFactor", "0.00")} | Duration: {Number(metrics, "audioDurationSeconds", "0.0")} s | Chunks: {GetInt32(metrics, "chunkCount")} | Peak: {Number(metrics, "peakLevel", "0.000")} | RMS: {Number(metrics, "rmsLevel", "0.000")} | VRAM: {vramText} | Device: {GetString(metrics, "device")}";
    }

    private static string GetString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) ? number : 0;

    private void AppendStartupLog(string value)
    {
        lock (_processSync)
            _lastStartupLog = Truncate(_lastStartupLog + " " + value, 2200);
    }

    private static int TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* Best-effort process cleanup. */ }
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[^maximum..];

    private void StopServer()
    {
        Process? process;
        lock (_processSync)
        {
            process = _serverProcess;
            _serverProcess = null;
            _serverBaseUri = null;
            _serverToken = null;
        }
        if (process is null) return;
        TryKill(process);
        process.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VoiceTutLocalService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}
