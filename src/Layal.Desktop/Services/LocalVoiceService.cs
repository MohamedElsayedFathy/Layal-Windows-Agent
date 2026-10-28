using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace Layal.Services;

/// <summary>
/// Local-only speech recognition and speech synthesis. Whisper and Piper execute as local
/// processes; temporary microphone and generated audio files are deleted after use.
/// </summary>
public sealed class LocalVoiceService
{
    private const string WhisperArchiveUrl =
        "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip";
    private const long WhisperArchiveSize = 7_982_101;
    private const string WhisperArchiveSha256 =
        "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539";
    private const string WhisperModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin?download=true";
    private const string WhisperModelSha1 = "465707469ff3a37a2b9b8d8f89f2f99de7299dac";
    private const long WhisperModelSize = 147_951_465;

    private const string PiperArchiveUrl =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const long PiperArchiveSize = 22_477_236;

    private const string ArabicVoiceBase =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/ar/ar_JO/kareem/medium/ar_JO-kareem-medium";
    private const string EnglishVoiceBase =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium";
    private const long PiperVoiceSize = 63_201_294;
    private const string ArabicVoiceSha256 =
        "9e95cab07b679da603bba17c4dec7ab3111320571964ee95c0379603c086491e";
    private const string EnglishVoiceSha256 =
        "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f";
    private const long MaximumArchiveBytes = 250 * 1024 * 1024;
    private const long MaximumAudioBytes = 64 * 1024 * 1024;

    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static string Root => LocalAiService.DataRoot;
    private static string ToolsRoot => Path.Combine(Root, "tools");
    private static string ModelsRoot => Path.Combine(Root, "speech-models");
    private static string TempRoot => Path.Combine(Root, "temp");
    private static readonly TimeSpan StaleTemporaryArtifactAge = TimeSpan.FromHours(1);
    private static string WhisperModelPath => Path.Combine(ModelsRoot, "ggml-base.bin");
    private static string ArabicModelPath => Path.Combine(ModelsRoot, "ar_JO-kareem-medium.onnx");
    private static string ArabicConfigPath => Path.Combine(ModelsRoot, "ar_JO-kareem-medium.onnx.json");
    private static string EnglishModelPath => Path.Combine(ModelsRoot, "en_US-lessac-medium.onnx");
    private static string EnglishConfigPath => Path.Combine(ModelsRoot, "en_US-lessac-medium.onnx.json");

    private string? _whisperExecutable;
    private string? _piperExecutable;
    private int _ready;

    public bool IsReady
    {
        get
        {
            if (Volatile.Read(ref _ready) == 1) return true;

            // Persisted downloads are reusable across application restarts; re-discover
            // their executables and model files rather than forcing a second large download.
            _whisperExecutable ??= FindExecutable(Path.Combine(ToolsRoot, "whisper"), "whisper-cli.exe");
            _piperExecutable ??= FindExecutable(Path.Combine(ToolsRoot, "piper"), "piper.exe");
            var ready = _whisperExecutable is not null && _piperExecutable is not null &&
                        File.Exists(WhisperModelPath) &&
                        File.Exists(ArabicModelPath) && File.Exists(ArabicConfigPath) &&
                        File.Exists(EnglishModelPath) && File.Exists(EnglishConfigPath);
            if (ready) Volatile.Write(ref _ready, 1);
            return ready;
        }
    }

    public async Task EnsureReadyAsync(
        IProgress<LocalAiProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("Local voice setup currently requires 64-bit Windows.");

        Directory.CreateDirectory(ToolsRoot);
        Directory.CreateDirectory(ModelsRoot);
        Directory.CreateDirectory(TempRoot);
        CleanupStaleTemporaryArtifacts(TempRoot, DateTime.UtcNow);

        _whisperExecutable = FindExecutable(Path.Combine(ToolsRoot, "whisper"), "whisper-cli.exe");
        if (_whisperExecutable is null)
        {
            var archive = Path.Combine(Root, "whisper-bin-x64-v1.9.1.zip");
            await EnsureVerifiedFileAsync(
                WhisperArchiveUrl, archive, WhisperArchiveSize, WhisperArchiveSha256, null,
                "تنزيل محرك تحويل الكلام إلى نص (Whisper)…", progress, cancellationToken);
            var destination = Path.Combine(ToolsRoot, "whisper");
            SafeExtractZip(archive, destination);
            _whisperExecutable = FindExecutable(destination, "whisper-cli.exe");
            if (_whisperExecutable is null)
                throw new InvalidOperationException("لم يحتوِ أرشيف Whisper الذي تم التحقق منه على whisper-cli.exe.");
            TryDeleteFile(archive);
        }

        await EnsureVerifiedFileAsync(
            WhisperModelUrl, WhisperModelPath, WhisperModelSize, null, WhisperModelSha1,
            "تنزيل نموذج فهم الكلام العربي والإنجليزي (Whisper base)…", progress, cancellationToken);

        _piperExecutable = FindExecutable(Path.Combine(ToolsRoot, "piper"), "piper.exe");
        if (_piperExecutable is null)
        {
            var archive = Path.Combine(Root, "piper-windows-amd64-2023.11.14-2.zip");
            await EnsureVerifiedFileAsync(
                PiperArchiveUrl, archive, PiperArchiveSize, null, null,
                "تنزيل محرك النطق المحلي (Piper)…", progress, cancellationToken);
            var destination = Path.Combine(ToolsRoot, "piper");
            SafeExtractZip(archive, destination);
            _piperExecutable = FindExecutable(destination, "piper.exe");
            if (_piperExecutable is null)
                throw new InvalidOperationException("لم يحتوِ أرشيف Piper على piper.exe.");
            TryDeleteFile(archive);
        }

        await EnsureVerifiedFileAsync(
            ArabicVoiceBase + ".onnx", ArabicModelPath, PiperVoiceSize, ArabicVoiceSha256, null,
            "تنزيل الصوت العربي المحلي…", progress, cancellationToken);
        await EnsureVerifiedFileAsync(
            ArabicVoiceBase + ".onnx.json", ArabicConfigPath, 5_024, null, null,
            "تنزيل إعدادات الصوت العربي…", progress, cancellationToken);
        await EnsureVerifiedFileAsync(
            EnglishVoiceBase + ".onnx", EnglishModelPath, PiperVoiceSize, EnglishVoiceSha256, null,
            "تنزيل الصوت الإنجليزي المحلي…", progress, cancellationToken);
        await EnsureVerifiedFileAsync(
            EnglishVoiceBase + ".onnx.json", EnglishConfigPath, 4_885, null, null,
            "تنزيل إعدادات الصوت الإنجليزي…", progress, cancellationToken);

        ValidateVoiceConfig(ArabicConfigPath, "ar_JO-kareem-medium");
        ValidateVoiceConfig(EnglishConfigPath, "en_US-lessac-medium");
        Volatile.Write(ref _ready, 1);
        progress?.Report(new LocalAiProgress("أصبح تحويل الكلام والنطق متاحين محليًا على جهازك."));
    }

    public async Task<string> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        if (!IsReady || string.IsNullOrWhiteSpace(_whisperExecutable))
            throw new InvalidOperationException("جهّز موارد الصوت المحلي أولًا من الإعدادات.");
        if (wavBytes.Length <= 44 || wavBytes.Length > MaximumAudioBytes)
            throw new InvalidOperationException("التسجيل الصوتي فارغ أو يتجاوز الحد المحلي المسموح.");

        Directory.CreateDirectory(TempRoot);
        CleanupStaleTemporaryArtifacts(TempRoot, DateTime.UtcNow);
        var id = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(TempRoot, id + ".wav");
        var outputPrefix = Path.Combine(TempRoot, id + "-transcription");
        var jsonPath = outputPrefix + ".json";
        var textPath = outputPrefix + ".txt";
        try
        {
            await File.WriteAllBytesAsync(inputPath, wavBytes, cancellationToken);
            var start = new ProcessStartInfo(_whisperExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_whisperExecutable)!
            };
            start.ArgumentList.Add("-m");
            start.ArgumentList.Add(WhisperModelPath);
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(inputPath);
            start.ArgumentList.Add("-l");
            start.ArgumentList.Add("auto");
            start.ArgumentList.Add("-oj");
            start.ArgumentList.Add("-otxt");
            start.ArgumentList.Add("-of");
            start.ArgumentList.Add(outputPrefix);
            start.ArgumentList.Add("-np");
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add(Math.Clamp(Environment.ProcessorCount - 1, 1, 8).ToString(System.Globalization.CultureInfo.InvariantCulture));

            var result = await RunProcessAsync(start, null, TimeSpan.FromMinutes(5), cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("تعذر تحويل التسجيل محليًا. " + Truncate(result.Error, 700));
            if (File.Exists(jsonPath))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, cancellationToken));
                if (document.RootElement.TryGetProperty("transcription", out var segments) && segments.ValueKind == JsonValueKind.Array)
                {
                    var pieces = segments.EnumerateArray()
                        .Select(segment => GetString(segment, "text").Trim())
                        .Where(part => part.Length > 0);
                    var transcript = string.Join(" ", pieces).Trim();
                    if (transcript.Length > 0) return transcript;
                }
            }

            if (File.Exists(textPath))
            {
                var transcript = (await File.ReadAllTextAsync(textPath, cancellationToken)).Trim();
                if (transcript.Length > 0) return transcript;
            }
            throw new InvalidOperationException("لم يتعرف النموذج المحلي على كلام واضح. جرّب تسجيلًا أوضح وتقليل الضوضاء.");
        }
        finally
        {
            TryDeleteFile(inputPath);
            TryDeleteFile(jsonPath);
            TryDeleteFile(textPath);
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(_piperExecutable))
            throw new InvalidOperationException("جهّز موارد الصوت المحلي أولًا من الإعدادات.");
        var normalized = NormalizeTextForSpeech(text);
        if (normalized.Length == 0) return;
        var arabic = normalized.Any(c => c is >= '\u0600' and <= '\u06FF');
        var modelPath = arabic ? ArabicModelPath : EnglishModelPath;
        var configPath = arabic ? ArabicConfigPath : EnglishConfigPath;

        Directory.CreateDirectory(TempRoot);
        CleanupStaleTemporaryArtifacts(TempRoot, DateTime.UtcNow);
        var outputPath = Path.Combine(TempRoot, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var start = new ProcessStartInfo(_piperExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_piperExecutable)!
            };
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(modelPath);
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(configPath);
            start.ArgumentList.Add("--output_file");
            start.ArgumentList.Add(outputPath);

            var result = await RunProcessAsync(start, normalized, TimeSpan.FromMinutes(2), cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("تعذر توليد النطق محليًا. " + Truncate(result.Error, 700));
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 44)
                throw new InvalidOperationException("لم يُنشئ محرك النطق ملفًا صوتيًا صالحًا.");

            await PlayWavAsync(outputPath, cancellationToken);
        }
        finally
        {
            TryDeleteFile(outputPath);
        }
    }

    public static string NormalizeTextForSpeech(string? text)
    {
        var result = text ?? string.Empty;
        result = Regex.Replace(result, @"(?s)\x60\x60\x60.*?\x60\x60\x60", " ");
        result = Regex.Replace(result, @"\x60([^\x60]*)\x60", "$1");
        result = Regex.Replace(result, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        result = Regex.Replace(result, @"\[([^\]]+)\]\([^)]+\)", "$1");
        result = Regex.Replace(result, @"https?://\S+", " ");
        result = Regex.Replace(result, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
        result = Regex.Replace(result, @"(?m)^\s*[-*+]\s+", string.Empty);
        result = Regex.Replace(result, @"(?m)^\s*\d+[.)]\s+", string.Empty);
        result = Regex.Replace(result, @"[*_~]", string.Empty);
        result = Regex.Replace(result, @"\s+", " ");
        result = result.Trim();
        return result.Length > 6000 ? result[..6000] : result;
    }

    private static async Task EnsureVerifiedFileAsync(
        string url,
        string destination,
        long expectedSize,
        string? expectedSha256,
        string? expectedSha1,
        string progressMessage,
        IProgress<LocalAiProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (await MatchesExpectedFileAsync(destination, expectedSize, expectedSha256, expectedSha1, cancellationToken))
            return;
        TryDeleteFile(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download";
        try
        {
            progress?.Report(new LocalAiProgress(progressMessage));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var response = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > MaximumArchiveBytes && expectedSize <= MaximumArchiveBytes)
                throw new InvalidOperationException("تجاوز حجم ملف الصوت الحد الآمن للتنزيل.");
            if (declaredLength.HasValue && declaredLength.Value > 0 && declaredLength.Value != expectedSize)
                throw new InvalidOperationException($"حجم ملف الصوت من المصدر تغيّر؛ أُلغي التنزيل للتحقق من النسخة ({declaredLength.Value:N0} بايت بدلًا من {expectedSize:N0}).");

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
            var buffer = new byte[1024 * 128];
            long downloaded = 0;
            try
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(), timeout.Token);
                    if (count == 0) break;
                    downloaded += count;
                    if (downloaded > expectedSize)
                        throw new InvalidOperationException("تجاوز الملف الذي يتم تنزيله الحجم المتوقع؛ أُلغي لتجنب تخزين ملف غير موثوق.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress?.Report(new LocalAiProgress(progressMessage, Math.Clamp(downloaded * 100d / expectedSize, 0d, 100d)));
                }
            }
            finally { Array.Clear(buffer); }
            await output.FlushAsync(timeout.Token);
            if (downloaded != expectedSize)
                throw new InvalidOperationException($"اكتمل تنزيل غير صحيح الحجم ({downloaded:N0} بايت بدلًا من {expectedSize:N0}). أعد المحاولة.");

            if (!await MatchesExpectedFileAsync(temporary, expectedSize, expectedSha256, expectedSha1, timeout.Token))
                throw new InvalidOperationException("فشل التحقق من بصمة ملف الصوت؛ لم نستخدم الملف.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task<bool> MatchesExpectedFileAsync(
        string path, long expectedSize, string? expectedSha256, string? expectedSha1, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != expectedSize) return false;
        if (expectedSha256 is null && expectedSha1 is null) return true;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        string actual;
        if (expectedSha256 is not null)
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        else
            actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        var expected = expectedSha256 ?? expectedSha1!;
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void SafeExtractZip(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var fullRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("رفض التطبيق مسارًا غير آمن داخل أرشيف أداة الصوت.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                throw new InvalidOperationException("رفض التطبيق رابطًا رمزيًا داخل أرشيف أداة الصوت.");
        }
        archive.ExtractToDirectory(destination, overwriteFiles: true);
    }

    private static string? FindExecutable(string root, string name)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
    }

    private static void ValidateVoiceConfig(string path, string expectedName)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("audio", out var audio) ||
                !audio.TryGetProperty("sample_rate", out var rate) ||
                rate.ValueKind != JsonValueKind.Number ||
                !rate.TryGetInt32(out var sampleRate) || sampleRate is < 8000 or > 48000)
                throw new InvalidOperationException();
        }
        catch
        {
            throw new InvalidOperationException($"ملف إعدادات الصوت {expectedName} غير صالح؛ أعد تجهيز الصوت المحلي.");
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo start,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("تعذر تشغيل محرك الصوت المحلي.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteLineAsync(standardInput);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var error = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return (process.ExitCode, output, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw new TimeoutException("استغرقت معالجة الصوت المحلي وقتًا أطول من المتوقع؛ أوقفنا العملية مؤقتًا.");
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static async Task PlayWavAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(path);
        using var output = new WaveOut();
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> handler = (_, args) => stopped.TrySetResult(args);
        output.PlaybackStopped += handler;
        try
        {
            output.Init(reader);
            using var registration = cancellationToken.Register(() =>
            {
                try { output.Stop(); } catch { /* The output may already be stopped. */ }
            });
            output.Play();
            var result = await stopped.Task.WaitAsync(cancellationToken);
            if (result.Exception is not null)
                throw new InvalidOperationException("تعذر تشغيل الصوت المحلي على جهاز الإخراج الافتراضي.", result.Exception);
        }
        finally
        {
            output.PlaybackStopped -= handler;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* Best-effort cleanup for cancelled child processes. */ }
    }

    private static void CleanupStaleTemporaryArtifacts(string directory, DateTime nowUtc)
    {
        if (!Directory.Exists(directory)) return;

        var cutoffUtc = nowUtc - StaleTemporaryArtifactAge;
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "تعذر فحص بقايا الصوت المحلي المؤقتة؛ أوقفنا معالجة الصوت لحماية الخصوصية.", ex);
        }

        foreach (var path in paths)
        {
            if (!IsGeneratedTemporaryArtifactName(Path.GetFileName(path)))
                continue;

            FileAttributes attributes;
            DateTime lastWriteUtc;
            try
            {
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "رُصد مسار معاد التوجيه ضمن ملفات الصوت المؤقتة؛ أوقفنا المعالجة لحماية الخصوصية.");
                }

                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "تعذر التحقق من ملف صوت محلي مؤقت قديم؛ أوقفنا المعالجة لحماية الخصوصية.", ex);
            }

            if (lastWriteUtc > cutoffUtc)
                continue;

            try
            {
                File.Delete(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "تعذر حذف بقايا ملف صوت محلي مؤقت؛ أوقفنا المعالجة بدل ترك ملف حساس دون معالجة.", ex);
            }

            if (File.Exists(path))
            {
                throw new InvalidOperationException(
                    "تعذر تأكيد حذف ملف صوت محلي مؤقت قديم؛ أوقفنا المعالجة لحماية الخصوصية.");
            }
        }
    }

    private static bool IsGeneratedTemporaryArtifactName(string fileName)
    {
        if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _);
        }

        foreach (var extension in new[] { ".json", ".txt" })
        {
            var suffix = "-transcription" + extension;
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return Guid.TryParseExact(fileName[..^suffix.Length], "N", out _);
        }

        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* The temp file is no longer in active use; cleanup remains best effort. */ }
    }

    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}
