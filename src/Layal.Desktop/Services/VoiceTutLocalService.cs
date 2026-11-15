using System;
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
    public const string ModelId = "mohammedaly22/VoiceTut-TTS";
    public const string ModelRevision = "b36bdf4416864d84f47ca77ce3decf34f6ab16cc";
    public const string OmniVoiceCommit = "08be0b4ccbac3e13e374e86fbfead4b4cac343e2";
    public const string VoiceTutCommit = "44cba00cd3ddb04b5213353e19e960045e332da7";
    public const string DefaultVoiceDesign = "female, child, very high pitch";

    private const string InstallMarker = "voicetut-install.json";
    private const int MaximumTextCharacters = 6000;
    private static readonly TimeSpan ProcessInstallTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan ModelStartupTimeout = TimeSpan.FromMinutes(20);
    private static readonly HttpClient ApiClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly object _processSync = new();
    private Process? _serverProcess;
    private string? _serverToken;
    private Uri? _serverBaseUri;
    private string? _pythonExecutable;
    private string _lastStartupLog = string.Empty;
    private bool _disposed;

    public bool IsReady => File.Exists(PythonExecutablePath) &&
                           File.Exists(Path.Combine(InstallRoot, InstallMarker)) &&
                           File.Exists(ModelWeightsPath);

    public string LastMetricsSummary { get; private set; } = string.Empty;
    public string RuntimeDirectory => InstallRoot;

    private static string Root => LocalVoiceService.DataRoot;
    private static string InstallRoot => Path.Combine(Root, "voicetut");
    private static string VirtualEnvironment => Path.Combine(InstallRoot, "venv");
    private static string PythonExecutablePath => Path.Combine(VirtualEnvironment, "Scripts", "python.exe");
    private static string ModelCacheRoot => Path.Combine(Root, "hf-cache");
    private static string ModelSnapshotRoot => Path.Combine(
        ModelCacheRoot, "hub", "models--mohammedaly22--VoiceTut-TTS", "snapshots", ModelRevision);
    private static string ModelWeightsPath => Path.Combine(ModelSnapshotRoot, "model.safetensors");
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
            string.Equals(await File.ReadAllTextAsync(manifestPath, cancellationToken), expectedManifest, StringComparison.Ordinal);

        if (!installMatches)
        {
            progress?.Report(new LocalSpeechProgress("فحص بيئة Python 3.11 وتجهيز بيئة VoiceTut المعزولة…", 5));
            var pythonLauncher = await FindPythonLauncherAsync(cancellationToken);
            if (pythonLauncher is null)
                throw new InvalidOperationException(
                    "يلزم تثبيت Python 3.10 أو 3.11 أو 3.12 (64-bit) مع Python Launcher. لم يتم تثبيت أي برنامج تلقائيًا.");

            Directory.CreateDirectory(InstallRoot);
            if (!File.Exists(PythonExecutablePath))
            {
                var venvArgs = pythonLauncher.Value.PrefixArguments.Concat(new[] { "-m", "venv", VirtualEnvironment }).ToArray();
                await RunCommandAsync(pythonLauncher.Value.Executable, venvArgs,
                    ProcessInstallTimeout, cancellationToken, "إنشاء بيئة Python المعزولة");
            }

            _pythonExecutable = PythonExecutablePath;
            progress?.Report(new LocalSpeechProgress("تثبيت PyTorch محليًا (CUDA إن كانت بطاقة NVIDIA متاحة، وإلا CPU)…", 18));
            await RunPythonAsync(new[]
            {
                "-m", "pip", "install", "--disable-pip-version-check", "--no-cache-dir",
                "--upgrade", "pip"
            }, ProcessInstallTimeout, cancellationToken, "تحديث pip");

            var cudaAvailable = await IsNvidiaRuntimeAvailableAsync(cancellationToken);
            var torchIndex = cudaAvailable
                ? "https://download.pytorch.org/whl/cu128"
                : "https://download.pytorch.org/whl/cpu";
            await RunPythonAsync(new[]
            {
                "-m", "pip", "install", "--disable-pip-version-check", "--no-cache-dir",
                "--index-url", torchIndex, "torch==2.8.0", "torchaudio==2.8.0"
            }, ProcessInstallTimeout, cancellationToken, "تثبيت PyTorch");

            progress?.Report(new LocalSpeechProgress("تثبيت OmniVoice وVoiceTut بإصدارات Git ثابتة…", 42));
            await RunPythonAsync(new[]
            {
                "-m", "pip", "install", "--disable-pip-version-check", "--no-cache-dir",
                $"git+https://github.com/k2-fsa/OmniVoice.git@{OmniVoiceCommit}",
                $"git+https://github.com/MohammedAly22/VoiceTuT-TTS.git@{VoiceTutCommit}"
            }, ProcessInstallTimeout, cancellationToken, "تثبيت VoiceTut وOmniVoice");

            await RunPythonAsync(new[]
            {
                "-c",
                "import torch, omnivoice, voicetut_tts; print('torch=' + torch.__version__); print('cuda=' + str(torch.cuda.is_available()))"
            }, TimeSpan.FromMinutes(2), cancellationToken, "التحقق من مكتبات VoiceTut");

            await File.WriteAllTextAsync(manifestPath, expectedManifest, new UTF8Encoding(false), cancellationToken);
        }
        else
        {
            _pythonExecutable = PythonExecutablePath;
        }

        progress?.Report(new LocalSpeechProgress("تحميل أوزان VoiceTut المثبتة والتحقق منها (نحو 2.5 جيجابايت)…", 62));
        await EnsureServerStartedAsync(cancellationToken, progress);
        if (!File.Exists(ModelWeightsPath))
            throw new InvalidOperationException("لم يؤكد VoiceTut وجود ملف الأوزان المثبت بالإصدار المحدد.");

        progress?.Report(new LocalSpeechProgress("VoiceTut جاهز محليًا: Voice Design فقط، دون أصوات مرجعية أو استنساخ.", 100));
    }

    public Task<bool> ValidateCachedResourcesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            return Task.FromResult(false);
        return Task.FromResult(IsReady);
    }

    public async Task SpeakAsync(
        string text,
        string? outputDeviceId,
        VoiceTutGenerationSettings settings,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var value = LocalVoiceService.NormalizeTextForSpeech(text);
        if (value.Length == 0) return;
        if (value.Length > MaximumTextCharacters)
            value = value[..MaximumTextCharacters];

        if (!await IsServerHealthyAsync(cancellationToken))
            await EnsureServerStartedAsync(cancellationToken, null);

        var hasArabic = value.Any(IsArabicLetter);
        var language = hasArabic ? "arz" : "en";
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
                    buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
                    {
                        BufferDuration = TimeSpan.FromSeconds(24),
                        DiscardOnBufferOverflow = false,
                        ReadFully = true
                    };
                    output = UsesWasapiForDefaultOutput(outputDeviceId)
                        ? new WasapiOut()
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
                        while (buffer.BufferedBytes + pcm.Length > buffer.BufferLength)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (stopped?.Task.IsCompleted == true)
                                throw new InvalidOperationException("Audio playback stopped before the stream finished.");
                            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                        }
                        buffer.AddSamples(pcm, 0, pcm.Length);
                        totalAudioSeconds += pcm.Length / (double)(buffer.WaveFormat.AverageBytesPerSecond);
                        if (!playbackStarted)
                        {
                            using var registration = cancellationToken.Register(() =>
                            {
                                try { output.Stop(); } catch { /* Output may already be stopped. */ }
                            });
                            output.Play();
                            playbackStarted = true;
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
        if (!IsReady)
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
            progress?.Report(new LocalSpeechProgress(e.Data));
        };
        process.Exited += (_, _) => Trace.TraceError("VoiceTut local server exited with code {0}.", TryGetExitCode(process));
        if (!process.Start())
            throw new InvalidOperationException("تعذر تشغيل خادم VoiceTut المحلي.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_processSync) _serverProcess = process;

        progress?.Report(new LocalSpeechProgress("تشغيل نموذج VoiceTut محليًا لأول مرة؛ يُجهّز النموذج ويُحمّل الأوزان…", 72));
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

        StopServer();
        throw new TimeoutException("لم يصبح خادم VoiceTut المحلي جاهزًا خلال المهلة المحددة. " + _lastStartupLog);
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
            return doc.RootElement.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True;
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
        string operation)
    {
        await RunCommandAsync(PythonExecutablePath, arguments, timeout, cancellationToken, operation);
    }

    private static async Task<(string Executable, string[] PrefixArguments)?> FindPythonLauncherAsync(
        CancellationToken cancellationToken)
    {
        var launchers = new[]
        {
            ("py", new[] { "-3.11" }),
            ("py", new[] { "-3.12" }),
            ("py", new[] { "-3.10" }),
            ("python", Array.Empty<string>())
        };
        foreach (var (executable, prefix) in launchers)
        {
            try
            {
                var args = prefix.Concat(new[]
                {
                    "-c",
                    "import sys; raise SystemExit(0 if (3,10) <= sys.version_info[:2] < (3,14) else 2)"
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

    private static async Task<bool> IsNvidiaRuntimeAvailableAsync(CancellationToken cancellationToken)
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

    private static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string operation)
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
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"تعذر بدء خطوة {operation}.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"تعذر العثور على الأداة المطلوبة لتنفيذ {operation}: {ex.Message}", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var error = await stderr.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{operation} failed ({process.ExitCode}): {Truncate(error + "\n" + output, 1800)}");
            return (process.ExitCode, output, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"انتهت المهلة أثناء {operation}.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static string ValidateVoiceInstruction(string? instruction)
    {
        var value = string.IsNullOrWhiteSpace(instruction) ? DefaultVoiceDesign : instruction.Trim();
        if (value.Length > 300 || !value.Contains("female", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("يجب أن يتضمن وصف الصوت كلمة female وألا يتجاوز 300 حرف.");
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
        return $"VoiceTut | TTFA: {Number(metrics, "timeToFirstAudioMs", "0")} ms | RTF: {Number(metrics, "realTimeFactor", "0.00")} | Duration: {Number(metrics, "audioDurationSeconds", "0.0")} s | Chunks: {GetInt32(metrics, "chunkCount")} | Device: {GetString(metrics, "device")}";
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
