using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Layal.Services;

public sealed record LocalSpeechProgress(string Message, double? Percent = null);

/// <summary>
/// Local speech recognition is performed by Whisper; synthesis is performed by the
/// isolated, loopback-only VoiceTut service using Voice Design without reference voices.
/// </summary>
public sealed class LocalVoiceService
{
    private const string WhisperArchiveUrl =
        "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip";
    private const long WhisperArchiveSize = 7_982_101;
    private const string WhisperArchiveSha256 =
        "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539";
    // Pin the model repository revision and its SHA-256 so a mutable "main" ref cannot
    // silently change the model bytes between installs.
    private const string WhisperModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin";
    private const string WhisperModelSha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    private const long WhisperModelSize = 147_951_465;

    private const long MaximumArchiveBytes = 250 * 1024 * 1024;
    private const long MaximumAudioBytes = 64 * 1024 * 1024;

    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string DataRoot
    {
        get
        {
            var paths = LayalDataPathPolicy.Resolve(
                Environment.GetEnvironmentVariable(LayalDataPathPolicy.AcceptanceRootEnvironmentVariable),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            // Keep the existing on-disk directory so previously downloaded speech resources remain usable.
            return Path.Combine(paths.DataRoot, "LocalAi");
        }
    }

    private static string Root => DataRoot;
    private static string ToolsRoot => Path.Combine(Root, "tools");
    private static string ModelsRoot => Path.Combine(Root, "speech-models");
    private static string TempRoot => Path.Combine(Root, "temp");
    private static readonly TimeSpan StaleTemporaryArtifactAge = TimeSpan.FromHours(1);
    private static string WhisperModelPath => Path.Combine(ModelsRoot, "ggml-base.bin");
    private readonly VoiceTutLocalService _voiceTutService = new();
    private string? _whisperExecutable;
    private int _ready;

    // A file's presence is not evidence that it still matches the verified resources. The
    // cache must be verified in this process before local speech is advertised as ready.
    public bool IsReady => Volatile.Read(ref _ready) == 1;

    private void InvalidateReadiness()
    {
        Volatile.Write(ref _ready, 0);
        _whisperExecutable = null;
    }

    public async Task EnsureReadyAsync(
        IProgress<LocalSpeechProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // A retry starts from an untrusted cache state. Never leave a prior success bit set
        // if revalidation, download, extraction or repair fails partway through this attempt.
        InvalidateReadiness();

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("Local voice setup currently requires 64-bit Windows.");

        Directory.CreateDirectory(ToolsRoot);
        Directory.CreateDirectory(ModelsRoot);
        Directory.CreateDirectory(TempRoot);
        CleanupStaleTemporaryArtifacts(TempRoot, DateTime.UtcNow);

        var whisperArchive = Path.Combine(Root, "whisper-bin-x64-v1.9.1.zip");
        await EnsureVerifiedFileAsync(
            WhisperArchiveUrl, whisperArchive, WhisperArchiveSize, WhisperArchiveSha256, null,
            "تنزيل محرك تحويل الكلام إلى نص (Whisper)…", progress, cancellationToken);

        var whisperRoot = Path.Combine(ToolsRoot, "whisper");
        _whisperExecutable = await FindVerifiedWhisperExecutableAsync(
            whisperArchive, whisperRoot, cancellationToken);
        if (_whisperExecutable is null)
            _whisperExecutable = await ExtractVerifiedWhisperArchiveAsync(
                whisperArchive, whisperRoot, cancellationToken);
        if (_whisperExecutable is null)
            throw new InvalidOperationException("تعذر التحقق من محرك Whisper وملفاته المستخرجة من الأرشيف الموثوق؛ لم يتم تشغيله.");

        await EnsureVerifiedFileAsync(
            WhisperModelUrl, WhisperModelPath, WhisperModelSize, WhisperModelSha256, null,
            "تنزيل نموذج فهم الكلام العربي والإنجليزي (Whisper)…", progress, cancellationToken);

        await _voiceTutService.EnsureReadyAsync(progress, cancellationToken);
        DeleteLegacyPiperResources();

        Volatile.Write(ref _ready, 1);
        progress?.Report(new LocalSpeechProgress("أصبح تحويل الكلام والنطق متاحين محليًا على جهازك."));
    }

    private static void DeleteLegacyPiperResources()
    {
        var modelsRoot = Path.GetFullPath(ModelsRoot);
        if (Directory.Exists(modelsRoot))
        {
            if ((File.GetAttributes(modelsRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("رفض التطبيق فحص موارد النطق القديمة عبر مسار معاد التوجيه.");

            foreach (var name in new[]
            {
                "ar_JO-kareem-medium.onnx", "ar_JO-kareem-medium.onnx.json",
                "en_US-lessac-medium.onnx", "en_US-lessac-medium.onnx.json"
            })
            {
                var path = Path.Combine(modelsRoot, name);
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("رفض التطبيق حذف ملف صوت قديم يمثّل رابطًا رمزيًا.");
                File.Delete(path);
            }
        }

        var piperArchive = Path.Combine(Root, "piper-windows-amd64-2023.11.14-2.zip");
        if (File.Exists(piperArchive))
        {
            if ((File.GetAttributes(piperArchive) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("رفض التطبيق حذف أرشيف Piper عبر مسار معاد التوجيه.");
            File.Delete(piperArchive);
        }

        var piperRoot = Path.Combine(ToolsRoot, "piper");
        if (Directory.Exists(piperRoot))
        {
            if ((File.GetAttributes(piperRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("رفض التطبيق حذف مجلد Piper يمثّل رابطًا رمزيًا.");
            DeleteDirectoryWithoutFollowingReparsePoints(piperRoot);
        }
    }

    public async Task<bool> ValidateCachedResourcesAsync(CancellationToken cancellationToken = default)
    {
        if (IsReady) return true;
        InvalidateReadiness();

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            return false;

        try
        {
            var whisperArchive = Path.Combine(Root, "whisper-bin-x64-v1.9.1.zip");
            _whisperExecutable = await FindVerifiedWhisperExecutableAsync(
                whisperArchive, Path.Combine(ToolsRoot, "whisper"), cancellationToken);
            if (_whisperExecutable is null)
                return false;

            if (!await _voiceTutService.ValidateCachedResourcesAsync(cancellationToken))
                return false;

            Volatile.Write(ref _ready, 1);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            // Treat unreadable or invalid cache entries as not ready. Setup will ask for consent
            // before attempting a repair or network download.
            Volatile.Write(ref _ready, 0);
            _whisperExecutable = null;
            return false;
        }
    }

    public async Task<string> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        if (!IsReady || string.IsNullOrWhiteSpace(_whisperExecutable))
            throw new InvalidOperationException("جهّز موارد الصوت المحلي أولًا من الإعدادات.");
        if (wavBytes.Length <= 44 || wavBytes.Length > MaximumAudioBytes)
            throw new InvalidOperationException("التسجيل الصوتي فارغ أو يتجاوز الحد المحلي المسموح.");

        string? verifiedWhisperExecutable;
        bool modelIsVerified;
        try
        {
            verifiedWhisperExecutable = await FindVerifiedWhisperExecutableAsync(
                Path.Combine(Root, "whisper-bin-x64-v1.9.1.zip"),
                Path.Combine(ToolsRoot, "whisper"),
                cancellationToken);
            modelIsVerified = await MatchesExpectedFileAsync(
                WhisperModelPath, WhisperModelSize, WhisperModelSha256, null, cancellationToken);
        }
        catch
        {
            // An I/O, ACL, reparse-point or cancellation failure means integrity could not
            // be established. Do not leave the service advertising stale readiness.
            InvalidateReadiness();
            throw;
        }

        if (verifiedWhisperExecutable is null ||
            !string.Equals(Path.GetFullPath(verifiedWhisperExecutable), Path.GetFullPath(_whisperExecutable), StringComparison.OrdinalIgnoreCase) ||
            !modelIsVerified)
        {
            InvalidateReadiness();
            throw new InvalidOperationException("تم تغيير محرك Whisper أو ملف النموذج منذ آخر تحقق؛ أعد تجهيز الصوت المحلي قبل استخدامه.");
        }

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

    public async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default,
        string? outputDeviceId = null,
        string? deliveryStyleId = null,
        VoiceTutGenerationSettings? generationSettings = null)
    {
        if (!IsReady)
            throw new InvalidOperationException("جهّز موارد الصوت المحلي أولًا من الإعدادات.");
        var settings = generationSettings ?? new VoiceTutGenerationSettings(
            VoiceTutLocalService.DefaultVoiceDesign,
            32,
            2.0,
            1.0,
            true);
        var effectiveSpeed = Math.Clamp(settings.Speed / VoiceDeliveryStylePolicy.GetLocalLengthScale(deliveryStyleId), 0.5, 2.0);
        var adjusted = settings with { Speed = effectiveSpeed };
        await _voiceTutService.SpeakAsync(
            NormalizeTextForSpeech(text), outputDeviceId, adjusted, cancellationToken).ConfigureAwait(false);
        VoiceMetricsUpdated?.Invoke(this, _voiceTutService.LastMetricsSummary);
    }

    public event EventHandler<string>? VoiceMetricsUpdated;
    public string LastVoiceMetricsSummary => _voiceTutService.LastMetricsSummary;

    public void Dispose() => _voiceTutService.Dispose();

    /// <summary>Detects Arabic/Latin runs for language-aware routing and evaluation helpers.</summary>
    public static IReadOnlyList<(string Text, bool IsArabic)> SplitTextByLanguage(string? text)
    {
        var normalized = NormalizeTextForSpeech(text);
        if (normalized.Length == 0)
            return Array.Empty<(string Text, bool IsArabic)>();

        var segments = new List<(string Text, bool IsArabic)>();
        var current = new StringBuilder();
        var currentLanguage = (bool?)null;
        var pending = new StringBuilder();

        foreach (Match token in Regex.Matches(normalized, @"[\u0600-\u06FF]+|[A-Za-z0-9]+|[^A-Za-z0-9\u0600-\u06FF]+"))
        {
            var value = token.Value;
            var isArabic = value.Any(character => character is >= '\u0600' and <= '\u06FF');
            var isLatinOrNumber = value.Any(character =>
                character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');

            if (!isArabic && !isLatinOrNumber)
            {
                if (currentLanguage is null) pending.Append(value);
                else current.Append(value);
                continue;
            }

            var nextLanguage = isArabic;
            if (currentLanguage is null)
            {
                currentLanguage = nextLanguage;
                current.Append(pending);
                pending.Clear();
            }
            else if (currentLanguage.Value != nextLanguage)
            {
                segments.Add((current.ToString().Trim(), currentLanguage.Value));
                current.Clear();
                currentLanguage = nextLanguage;
            }
            current.Append(value);
        }

        if (currentLanguage is not null && current.Length > 0)
            segments.Add((current.ToString().Trim(), currentLanguage.Value));
        else if (pending.Length > 0)
            segments.Add((pending.ToString().Trim(), false));

        return segments.Where(segment => segment.Text.Length > 0).ToArray();
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
        IProgress<LocalSpeechProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (await MatchesExpectedFileAsync(destination, expectedSize, expectedSha256, expectedSha1, cancellationToken))
            return;
        TryDeleteFile(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Give every download attempt its own staging file. A fixed ".download" path can be
        // held by a previous process, antivirus scanner, or another setup attempt, preventing
        // FileStream(FileShare.None) from opening it even though this attempt owns the download.
        // CreateNew also ensures a stale file can never be silently truncated and reused.
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            progress?.Report(new LocalSpeechProgress(progressMessage));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var response = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > MaximumArchiveBytes && expectedSize <= MaximumArchiveBytes)
                throw new InvalidOperationException("تجاوز حجم ملف الصوت الحد الآمن للتنزيل.");
            if (declaredLength.HasValue && declaredLength.Value > 0 && declaredLength.Value != expectedSize)
                throw new InvalidOperationException($"حجم ملف الصوت من المصدر تغيّر؛ أُلغي التنزيل للتحقق من النسخة ({declaredLength.Value:N0} بايت بدلًا من {expectedSize:N0}).");

            long downloaded = 0;
            // Close both streams before hashing or moving the staging file. The output stream
            // uses FileShare.None, so verifying or renaming it while its handle is still open
            // fails on Windows with "file is being used by another process".
            {
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
                var buffer = new byte[1024 * 128];
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
                        progress?.Report(new LocalSpeechProgress(progressMessage, Math.Clamp(downloaded * 100d / expectedSize, 0d, 100d)));
                    }
                }
                finally { Array.Clear(buffer); }
                await output.FlushAsync(timeout.Token);
            }

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

    private static async Task<string?> FindVerifiedWhisperExecutableAsync(
        string archivePath,
        string installRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installRoot) ||
            (File.GetAttributes(installRoot) & FileAttributes.ReparsePoint) != 0)
            return null;

        await using var archiveStream = await OpenVerifiedWhisperArchiveAsync(archivePath, cancellationToken);
        if (archiveStream is null)
            return null;

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        return await VerifyWhisperExtractedFilesAsync(archive, installRoot, cancellationToken);
    }

    private static Task<string?> ExtractVerifiedWhisperArchiveAsync(
        string archivePath,
        string installRoot,
        CancellationToken cancellationToken) =>
        ExtractVerifiedWhisperArchiveAsync(
            archivePath,
            installRoot,
            Path.Combine(ToolsRoot, "whisper"),
            WhisperArchiveSize,
            WhisperArchiveSha256,
            cancellationToken);

    private static async Task<string?> ExtractVerifiedWhisperArchiveAsync(
        string archivePath,
        string installRoot,
        string expectedInstallRoot,
        long expectedArchiveSize,
        string expectedArchiveSha256,
        CancellationToken cancellationToken)
    {
        var expected = Path.GetFullPath(expectedInstallRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actual = Path.GetFullPath(installRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رفض التطبيق فك أرشيف Whisper خارج مجلد التثبيت المعتمد.");

        // Hold the hash-verified archive open with FileShare.Read for the entire extraction
        // and comparison, so it cannot be replaced between verification and use.
        await using var archiveStream = await OpenVerifiedWhisperArchiveAsync(
            archivePath, expectedArchiveSize, expectedArchiveSha256, cancellationToken);
        if (archiveStream is null)
            return null;

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        DeleteWhisperInstallDirectory(actual, expected);
        SafeExtractZip(archive, actual);
        return await VerifyWhisperExtractedFilesAsync(archive, actual, cancellationToken);
    }

    private static Task<FileStream?> OpenVerifiedWhisperArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken) =>
        OpenVerifiedWhisperArchiveAsync(
            archivePath, WhisperArchiveSize, WhisperArchiveSha256, cancellationToken);

    private static async Task<FileStream?> OpenVerifiedWhisperArchiveAsync(
        string archivePath,
        long expectedArchiveSize,
        string expectedArchiveSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath) ||
            (File.GetAttributes(archivePath) & FileAttributes.ReparsePoint) != 0)
            return null;

        var stream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != expectedArchiveSize)
            {
                await stream.DisposeAsync();
                return null;
            }

            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
            var expectedHash = Convert.FromHexString(expectedArchiveSha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                await stream.DisposeAsync();
                return null;
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task<string?> VerifyWhisperExtractedFilesAsync(
        ZipArchive archive,
        string installRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installRoot) ||
            (File.GetAttributes(installRoot) & FileAttributes.ReparsePoint) != 0)
            return null;

        var expectedFiles = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var executablePaths = new List<string>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0)
                continue;

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative) ||
                Path.IsPathRooted(relative) ||
                relative.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or "..") ||
                relative.Contains(':') ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                return null;

            var fullPath = Path.GetFullPath(Path.Combine(installRoot, relative));
            var rootPrefix = Path.GetFullPath(installRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var normalized = Path.GetRelativePath(installRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            if (!expectedFiles.TryAdd(normalized, entry))
                return null;

            if (string.Equals(Path.GetFileName(fullPath), "whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
                executablePaths.Add(fullPath);
        }

        if (expectedFiles.Count is 0 or > 512 || executablePaths.Count != 1)
            return null;

        var actualFiles = EnumerateFilesWithoutFollowingReparsePoints(installRoot)
            .ToDictionary(
                path => Path.GetRelativePath(installRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                path => path,
                StringComparer.OrdinalIgnoreCase);
        if (actualFiles.Count != expectedFiles.Count ||
            !actualFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(expectedFiles.Keys))
            return null;

        foreach (var pair in expectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actualPath = actualFiles[pair.Key];
            var attributes = File.GetAttributes(actualPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return null;

            var info = new FileInfo(actualPath);
            if (info.Length != pair.Value.Length)
                return null;

            await using var archiveEntryStream = pair.Value.Open();
            await using var installedStream = new FileStream(
                actualPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var archiveHash = await SHA256.HashDataAsync(archiveEntryStream, cancellationToken);
            var installedHash = await SHA256.HashDataAsync(installedStream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(archiveHash, installedHash))
                return null;
        }

        return executablePaths[0];
    }


    private static void DeleteWhisperInstallDirectory(string directory, string expectedRoot)
    {
        var expected = Path.GetFullPath(expectedRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actual = Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رفض التطبيق حذف مسار خارج تثبيت Whisper المعتمد.");

        if (Directory.Exists(actual))
            DeleteDirectoryWithoutFollowingReparsePoints(actual);
    }

    private static IEnumerable<string> EnumerateFilesWithoutFollowingReparsePoints(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("رفض التطبيق استخدام رابط رمزي أو reparse point داخل مجلد أدوات الصوت.");

            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var nested in EnumerateFilesWithoutFollowingReparsePoints(entry))
                    yield return nested;
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("رفض التطبيق حذف مجلد تثبيت أدوات الصوت الذي يمثل رابطًا رمزيًا.");

        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            DeleteDirectoryWithoutFollowingReparsePoints(entry);
        Directory.Delete(path, recursive: false);
    }

    private static void SafeExtractZip(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        SafeExtractZip(archive, destination);
    }

    private static void SafeExtractZip(ZipArchive archive, string destination)
    {
        Directory.CreateDirectory(destination);
        var fullRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

            DateTime lastWriteUtc;
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "رُصد مسار معاد التوجيه ضمن ملفات الصوت المؤقتة؛ أوقفنا المعالجة لحماية الخصوصية.");
                }

                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "تعذر التحقق من ملف صوت محلي مؤقت قديم؛ أوقفنا المعالجة لحماية الخصوصية.", ex);
            }

            if (lastWriteUtc > cutoffUtc) continue;

            try { File.Delete(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
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
            return Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _);

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
