using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
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
    // Pin the model repository revision and its SHA-256 so a mutable "main" ref cannot
    // silently change the model bytes between installs.
    private const string WhisperModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin";
    private const string WhisperModelSha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    private const long WhisperModelSize = 147_951_465;

    private const string PiperArchiveUrl =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const long PiperArchiveSize = 22_477_236;
    // The official release metadata does not publish an asset digest. This SHA-256 is pinned
    // for the exact official 2023.11.14-2 Windows x64 asset and checked on every reuse. 
    private const string PiperArchiveSha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea";

    private const string ArabicVoiceBase =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/ar/ar_JO/kareem/medium/ar_JO-kareem-medium";
    private const string EnglishVoiceBase =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium";
    private const long PiperVoiceSize = 63_201_294;
    private const string ArabicVoiceSha256 =
        "9e95cab07b679da603bba17c4dec7ab3111320571964ee95c0379603c086491e";
    private const string EnglishVoiceSha256 =
        "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f";
    private const long ArabicVoiceConfigSize = 5_024;
    private const long EnglishVoiceConfigSize = 4_885;
    private const string ArabicVoiceConfigSha256 =
        "ea6d9b9d9076dbdb6bf5c98c6a141ef154959d2359709b37855727964e7d6c4d";
    private const string EnglishVoiceConfigSha256 =
        "efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0";

    // Runtime code and model assets from the verified Piper archive. The installed tree is
    // checked against these pins so replacing an executable or side-loaded DLL is rejected.
    private static readonly (string RelativePath, long Size, string Sha256)[] PiperRuntimeFileHashes =
    [
        ("piper/espeak-ng.dll", 380_928, "9588480f8197df62fd8461a8431f8eaec6e8e7749c5ffcbe7fee656fe40a2189"),
        ("piper/libtashkeel_model.ort", 10_261_536, "9f27090af3e0f661913af048a739632a3f577b3233512551d8c90580ccad4aa8"),
        ("piper/onnxruntime_providers_shared.dll", 22_424, "71705b9bd76baced583eb37fe5ec2101946b4de50f1e9bdb156109f7e8082980"),
        ("piper/onnxruntime.dll", 9_271_704, "a630f67f4a82b6689e4178bf81d362d945522dd907b2a92cb43cebf72c83a06f"),
        ("piper/piper_phonemize.dll", 407_040, "4b5f344b2f31204a8a94a0bf485f93e4971671e81188a0d67f326e113bfb0b2e"),
        ("piper/piper.exe", 509_952, "96f3da3811151580073e40bb4dd20eb0fb8115f5f5f76e2fb54282b3edfa5c1f")
    ];
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

    // A file's presence is not evidence that it still matches the verified resources. The
    // cache must be verified in this process before local speech is advertised as ready.
    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public async Task EnsureReadyAsync(
        IProgress<LocalAiProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // A retry starts from an untrusted cache state. Never leave a prior success bit set
        // if revalidation, download, extraction or repair fails partway through this attempt.
        Volatile.Write(ref _ready, 0);
        _whisperExecutable = null;
        _piperExecutable = null;

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

        var piperArchive = Path.Combine(Root, "piper-windows-amd64-2023.11.14-2.zip");
        await EnsureVerifiedFileAsync(
            PiperArchiveUrl, piperArchive, PiperArchiveSize, PiperArchiveSha256, null,
            "تنزيل محرك النطق المحلي (Piper)…", progress, cancellationToken);

        var piperDestination = Path.Combine(ToolsRoot, "piper");
        if (!await MatchesExpectedPiperInstallAsync(piperDestination, cancellationToken))
        {
            // Never keep using a damaged installed copy. Rebuild it only from the archive whose
            // pinned SHA-256 was checked above, and refuse to recursively follow reparse points.
            DeletePiperInstallDirectory(piperDestination);
            SafeExtractZip(piperArchive, piperDestination);
        }

        // The archive's signed-off layout is fixed. Use the exact manifest path instead of
        // recursively searching a potentially modified directory for an executable.
        _piperExecutable = Path.Combine(piperDestination, "piper", "piper.exe");
        if (!File.Exists(_piperExecutable) ||
            !await MatchesExpectedPiperInstallAsync(piperDestination, cancellationToken))
            throw new InvalidOperationException("تعذر التحقق من ملفات محرك النطق Piper؛ لم يتم تشغيلها.");

        await EnsureVerifiedFileAsync(
            ArabicVoiceBase + ".onnx", ArabicModelPath, PiperVoiceSize, ArabicVoiceSha256, null,
            "تنزيل الصوت العربي المحلي…", progress, cancellationToken);
        await EnsureVerifiedFileAsync(
            ArabicVoiceBase + ".onnx.json", ArabicConfigPath, ArabicVoiceConfigSize, ArabicVoiceConfigSha256, null,
            "تنزيل إعدادات الصوت العربي…", progress, cancellationToken);
        await EnsureVerifiedFileAsync(
            EnglishVoiceBase + ".onnx", EnglishModelPath, PiperVoiceSize, EnglishVoiceSha256, null,
            "تنزيل الصوت الإنجليزي المحلي…", progress, cancellationToken);
        await EnsureVerifiedFileAsync(
            EnglishVoiceBase + ".onnx.json", EnglishConfigPath, EnglishVoiceConfigSize, EnglishVoiceConfigSha256, null,
            "تنزيل إعدادات الصوت الإنجليزي…", progress, cancellationToken);

        ValidateVoiceConfig(ArabicConfigPath, "ar_JO-kareem-medium");
        ValidateVoiceConfig(EnglishConfigPath, "en_US-lessac-medium");
        Volatile.Write(ref _ready, 1);
        progress?.Report(new LocalAiProgress("أصبح تحويل الكلام والنطق متاحين محليًا على جهازك."));
    }

    public async Task<bool> ValidateCachedResourcesAsync(CancellationToken cancellationToken = default)
    {
        if (IsReady) return true;
        Volatile.Write(ref _ready, 0);
        _whisperExecutable = null;
        _piperExecutable = null;

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            return false;

        try
        {
            var whisperArchive = Path.Combine(Root, "whisper-bin-x64-v1.9.1.zip");
            _whisperExecutable = await FindVerifiedWhisperExecutableAsync(
                whisperArchive, Path.Combine(ToolsRoot, "whisper"), cancellationToken);
            if (_whisperExecutable is null)
                return false;

            var piperRoot = Path.Combine(ToolsRoot, "piper");
            if (!await MatchesExpectedPiperInstallAsync(piperRoot, cancellationToken))
                return false;
            _piperExecutable = Path.Combine(piperRoot, "piper", "piper.exe");
            if (!File.Exists(_piperExecutable))
                return false;

            // The retained, hash-pinned archive is the trust anchor for the extracted Whisper
            // executable and sidecar DLLs. Legacy installs without it remain not-ready until the
            // next user-approved setup downloads the archive for verification.
            if (!await MatchesExpectedFileAsync(WhisperModelPath, WhisperModelSize, WhisperModelSha256, null, cancellationToken) ||
                !await MatchesExpectedFileAsync(ArabicModelPath, PiperVoiceSize, ArabicVoiceSha256, null, cancellationToken) ||
                !await MatchesExpectedFileAsync(ArabicConfigPath, ArabicVoiceConfigSize, ArabicVoiceConfigSha256, null, cancellationToken) ||
                !await MatchesExpectedFileAsync(EnglishModelPath, PiperVoiceSize, EnglishVoiceSha256, null, cancellationToken) ||
                !await MatchesExpectedFileAsync(EnglishConfigPath, EnglishVoiceConfigSize, EnglishVoiceConfigSha256, null, cancellationToken))
                return false;

            ValidateVoiceConfig(ArabicConfigPath, "ar_JO-kareem-medium");
            ValidateVoiceConfig(EnglishConfigPath, "en_US-lessac-medium");
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
            _piperExecutable = null;
            return false;
        }
    }

    public async Task<string> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        if (!IsReady || string.IsNullOrWhiteSpace(_whisperExecutable))
            throw new InvalidOperationException("جهّز موارد الصوت المحلي أولًا من الإعدادات.");
        var verifiedWhisperExecutable = await FindVerifiedWhisperExecutableAsync(
            Path.Combine(Root, "whisper-bin-x64-v1.9.1.zip"),
            Path.Combine(ToolsRoot, "whisper"),
            cancellationToken);
        if (verifiedWhisperExecutable is null ||
            !string.Equals(Path.GetFullPath(verifiedWhisperExecutable), Path.GetFullPath(_whisperExecutable), StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref _ready, 0);
            _whisperExecutable = null;
            throw new InvalidOperationException("تم تغيير محرك Whisper أو أحد ملفاته منذ آخر تحقق؛ أعد تجهيز الصوت المحلي قبل استخدامه.");
        }
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

    public async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default,
        string? outputDeviceId = null,
        string? deliveryStyleId = null)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(_piperExecutable))
            throw new InvalidOperationException("جهّز موارد الصوت المحلي أولًا من الإعدادات.");

        var normalized = NormalizeTextForSpeech(text);
        if (normalized.Length == 0) return;
        var segments = SplitTextByLanguage(normalized);
        if (segments.Count == 0) return;

        if (!await MatchesExpectedPiperInstallAsync(Path.Combine(ToolsRoot, "piper"), cancellationToken))
        {
            Volatile.Write(ref _ready, 0);
            throw new InvalidOperationException("اكتُشف تغيير في ملفات محرك النطق المحلي؛ أعد تجهيز الصوت قبل استخدامه.");
        }

        // Validate every required voice before playing the first segment. This avoids speaking
        // half a bilingual answer and then failing because the second language model is missing.
        foreach (var isArabic in segments.Select(segment => segment.IsArabic).Distinct())
        {
            var modelPath = isArabic ? ArabicModelPath : EnglishModelPath;
            var configPath = isArabic ? ArabicConfigPath : EnglishConfigPath;
            var modelHash = isArabic ? ArabicVoiceSha256 : EnglishVoiceSha256;
            var configHash = isArabic ? ArabicVoiceConfigSha256 : EnglishVoiceConfigSha256;
            var configSize = isArabic ? ArabicVoiceConfigSize : EnglishVoiceConfigSize;
            if (!await MatchesExpectedFileAsync(modelPath, PiperVoiceSize, modelHash, null, cancellationToken) ||
                !await MatchesExpectedFileAsync(configPath, configSize, configHash, null, cancellationToken))
            {
                Volatile.Write(ref _ready, 0);
                throw new InvalidOperationException("اكتُشف تغيير في موارد النطق المحلية؛ أعد تجهيز الصوت المحلي قبل استخدامه.");
            }
        }

        var lengthScale = VoiceDeliveryStylePolicy.GetLocalLengthScale(deliveryStyleId);
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SpeakSegmentAsync(segment.Text, segment.IsArabic, outputDeviceId, cancellationToken, lengthScale);
        }
    }

    /// <summary>Splits mixed Arabic/Latin text so each language uses its matching local Piper voice.</summary>
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

    private async Task SpeakSegmentAsync(
        string text,
        bool isArabic,
        string? outputDeviceId,
        CancellationToken cancellationToken,
        double lengthScale)
    {
        var modelPath = isArabic ? ArabicModelPath : EnglishModelPath;
        var configPath = isArabic ? ArabicConfigPath : EnglishConfigPath;
        Directory.CreateDirectory(TempRoot);
        CleanupStaleTemporaryArtifacts(TempRoot, DateTime.UtcNow);
        var outputPath = Path.Combine(TempRoot, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var start = new ProcessStartInfo(_piperExecutable!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_piperExecutable!)!
            };
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(modelPath);
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(configPath);
            start.ArgumentList.Add("--output_file");
            start.ArgumentList.Add(outputPath);
            start.ArgumentList.Add("--length_scale");
            start.ArgumentList.Add(lengthScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

            var result = await RunProcessAsync(start, text, TimeSpan.FromMinutes(2), cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("تعذر توليد النطق المحلي. " + Truncate(result.Error, 700));
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 44)
                throw new InvalidOperationException("لم يُنشئ محرك النطق ملفًا صوتيًا صالحًا.");

            await PlayWavAsync(outputPath, cancellationToken, outputDeviceId);
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

    private static async Task<string?> ExtractVerifiedWhisperArchiveAsync(
        string archivePath,
        string installRoot,
        CancellationToken cancellationToken)
    {
        // Hold the hash-verified archive open with FileShare.Read for the entire extraction
        // and comparison, so it cannot be replaced between verification and use.
        await using var archiveStream = await OpenVerifiedWhisperArchiveAsync(archivePath, cancellationToken);
        if (archiveStream is null)
            return null;

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        DeleteWhisperInstallDirectory(installRoot);
        SafeExtractZip(archive, installRoot);
        return await VerifyWhisperExtractedFilesAsync(archive, installRoot, cancellationToken);
    }

    private static async Task<FileStream?> OpenVerifiedWhisperArchiveAsync(
        string archivePath,
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
            if (stream.Length != WhisperArchiveSize)
            {
                await stream.DisposeAsync();
                return null;
            }

            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
            var expectedHash = Convert.FromHexString(WhisperArchiveSha256);
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


    private static void DeleteWhisperInstallDirectory(string directory)
    {
        var expected = Path.GetFullPath(Path.Combine(ToolsRoot, "whisper")).TrimEnd(Path.DirectorySeparatorChar);
        var actual = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رفض التطبيق حذف مسار خارج تثبيت Whisper المعتمد.");

        if (Directory.Exists(actual))
            DeleteDirectoryWithoutFollowingReparsePoints(actual);
    }

    private static async Task<bool> MatchesExpectedPiperInstallAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return false;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("رفض التطبيق استخدام مجلد Piper الذي يمثل رابطًا رمزيًا أو reparse point.");

        var actualRuntimePaths = EnumerateFilesWithoutFollowingReparsePoints(root)
            .Where(IsPiperRuntimeCodeFile)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedRuntimePaths = PiperRuntimeFileHashes
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualRuntimePaths.SetEquals(expectedRuntimePaths)) return false;

        foreach (var item in PiperRuntimeFileHashes)
        {
            var path = Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!await MatchesExpectedFileAsync(path, item.Size, item.Sha256, null, cancellationToken))
                return false;
        }

        return true;
    }

    private static bool IsPiperRuntimeCodeFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ort", StringComparison.OrdinalIgnoreCase);
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

    private static void DeletePiperInstallDirectory(string directory)
    {
        var expected = Path.GetFullPath(Path.Combine(ToolsRoot, "piper")).TrimEnd(Path.DirectorySeparatorChar);
        var actual = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رفض التطبيق حذف مسار خارج تثبيت Piper المعتمد.");

        if (Directory.Exists(actual))
            DeleteDirectoryWithoutFollowingReparsePoints(actual);
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("رفض التطبيق حذف مجلد تثبيت Piper الذي يمثل رابطًا رمزيًا.");

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

    private static async Task PlayWavAsync(string path, CancellationToken cancellationToken, string? outputDeviceId)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(path);
        using var output = new WaveOut { DeviceNumber = AudioDeviceService.ResolveOutputDeviceNumber(outputDeviceId) };
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
