using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Layal.Services;

public static class LocalResourceIds
{
    public const string PythonRuntime = "python-runtime";
    public const string PyTorch = "pytorch";
    public const string OmniVoice = "omnivoice";
    public const string VoiceTut = "voicetut";
    public const string VoiceTutModel = "voicetut-model";
    public const string WhisperRuntime = "whisper-runtime";
    public const string WhisperModel = "whisper-model";
    public const string OllamaRuntime = "ollama-runtime";
    public const string OllamaRuntimeInstaller = "ollama-runtime-installer";
    public const string ChatModel = "chat-model-qwen35-2b";
    public const string OcrArabic = "ocr-ara";
    public const string OcrEnglish = "ocr-eng";
}

public enum LocalResourceKind
{
    Runtime,
    PythonPackage,
    SpeechModel,
    ChatModel,
    EmbeddedData
}

public sealed record LocalResourceFilePin(
    string RelativePath,
    long ExpectedSizeBytes,
    string ExpectedSha256)
{
    public bool HasPinnedSha256 =>
        ExpectedSha256 is { Length: 64 } &&
        ExpectedSha256.All(Uri.IsHexDigit);
}

public sealed record LocalResourceDefinition(
    string Id,
    string DisplayName,
    string Version,
    string Source,
    LocalResourceKind Kind,
    bool Required,
    long? ExpectedSizeBytes,
    string? ExpectedSha256,
    string VerificationPolicy,
    long? ExpectedGitBlobSizeBytes = null,
    string? ExpectedGitBlobSha1 = null,
    IReadOnlyList<LocalResourceFilePin>? ExpectedFiles = null)
{
    public bool HasPinnedSha256 =>
        ExpectedSha256 is { Length: 64 } &&
        ExpectedSha256.All(Uri.IsHexDigit);
}

/// <summary>
/// Single inventory for the local components used by full first-run setup.
/// Unknown sizes and hashes are intentionally left null; they must never be guessed.
/// </summary>
public static class LocalResourceCatalog
{
    private static readonly ReadOnlyCollection<LocalResourceDefinition> Definitions =
        Array.AsReadOnly(new[]
        {
            new LocalResourceDefinition(
                LocalResourceIds.PythonRuntime,
                "Python x64",
                "3.11–3.13 (managed fallback 3.13.17)",
                "https://www.python.org/ftp/python/3.13.17/python-3.13.17-amd64.exe",
                LocalResourceKind.Runtime,
                true,
                29_941_280,
                "5da012101ba2e17113a9a1cd901e9ce1441086f2b7ecb5de06ec94163899a190",
                "Verify the pinned installer SHA-256 and Python Software Foundation Authenticode publisher; install per-user under Layal LocalAi without changing PATH; verify Python x64 and version 3.13.17."),

            new LocalResourceDefinition(
                LocalResourceIds.PyTorch,
                "PyTorch and TorchAudio",
                "2.8.0",
                "https://download.pytorch.org/whl/",
                LocalResourceKind.PythonPackage,
                true,
                null,
                null,
                "Verify exact installed versions, pip check, and import/runtime health."),

            new LocalResourceDefinition(
                LocalResourceIds.OmniVoice,
                "OmniVoice",
                "08be0b4ccbac3e13e374e86fbfead4b4cac343e2",
                "https://github.com/k2-fsa/OmniVoice/archive/08be0b4ccbac3e13e374e86fbfead4b4cac343e2.zip",
                LocalResourceKind.PythonPackage,
                true,
                null,
                null,
                "Install from the immutable Git commit and record the resolved package inventory."),

            new LocalResourceDefinition(
                LocalResourceIds.VoiceTut,
                "VoiceTut",
                "44cba00cd3ddb04b5213353e19e960045e332da7",
                "https://github.com/MohammedAly22/VoiceTuT-TTS/archive/44cba00cd3ddb04b5213353e19e960045e332da7.zip",
                LocalResourceKind.PythonPackage,
                true,
                null,
                null,
                "Install from the immutable Git commit and record the resolved package inventory."),

            new LocalResourceDefinition(
                LocalResourceIds.VoiceTutModel,
                "VoiceTut Egyptian Arabic and English model",
                "b36bdf4416864d84f47ca77ce3decf34f6ab16cc",
                "https://huggingface.co/mohammedaly22/VoiceTut-TTS/tree/b36bdf4416864d84f47ca77ce3decf34f6ab16cc",
                LocalResourceKind.SpeechModel,
                true,
                null,
                null,
                "Verify the immutable Hugging Face revision, SHA-256 and exact size of each downloaded binary file before model loading, then require the integrity-aware local health endpoint.",
                ExpectedFiles: Array.AsReadOnly(new[]
                {
                    new LocalResourceFilePin(
                        "model.safetensors",
                        2_450_344_144,
                        "172ac1268fa98cb6253deb9f66fce1115e74e9b66012847b8483addad07a2b78"),
                    new LocalResourceFilePin(
                        "tokenizer.json",
                        11_423_986,
                        "408f669b7e2b045fdf54201d815bd364e6667dbd845115da81239c40bc6dcfd1")
                })),

            new LocalResourceDefinition(
                LocalResourceIds.WhisperRuntime,
                "Whisper Windows runtime",
                "1.9.1",
                "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip",
                LocalResourceKind.Runtime,
                true,
                7_982_101,
                "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539",
                "Verify pinned archive size and SHA-256, then verify every extracted file against the archive."),

            new LocalResourceDefinition(
                LocalResourceIds.WhisperModel,
                "Whisper base speech recognition model",
                "5359861c739e955e79d9a303bcbc70fb988958b1",
                "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin",
                LocalResourceKind.SpeechModel,
                true,
                147_951_465,
                "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
                "Verify immutable repository revision, exact size, and SHA-256."),

            new LocalResourceDefinition(
                LocalResourceIds.OllamaRuntime,
                "Ollama Windows runtime",
                "Managed stable installer; exact version recorded at install time",
                "https://ollama.com/download/windows",
                LocalResourceKind.Runtime,
                true,
                null,
                null,
                "Verify the Authenticode signature and executable version before first use."),

            new LocalResourceDefinition(
                LocalResourceIds.OllamaRuntimeInstaller,
                "Ollama Windows official installer fallback",
                "0.40.16",
                "https://github.com/ollama/ollama/releases/download/v0.40.16/OllamaSetup.exe",
                LocalResourceKind.Runtime,
                false,
                1_857_663_944,
                "84db4f756ddc9e091142a85cdc0f7ae5db68bc9b3e075f114388b326f8ed912d",
                "Verify the pinned official release archive SHA-256 and Ollama Inc. Authenticode publisher before a silent per-user install; then verify the installed runtime."),

            new LocalResourceDefinition(
                LocalResourceIds.ChatModel,
                "Qwen3.5 local chat model",
                "qwen3.5:2b",
                "https://ollama.com/library/qwen3.5",
                LocalResourceKind.ChatModel,
                true,
                null,
                null,
                "Record the resolved Ollama model digest and verify a loopback-only chat health check."),

            new LocalResourceDefinition(
                LocalResourceIds.OcrArabic,
                "Arabic OCR language data",
                "Bundled with Layal",
                "app:tessdata/ara.traineddata",
                LocalResourceKind.EmbeddedData,
                true,
                null,
                null,
                "Verify the packaged file length and Git blob SHA-1 from the pinned tessdata_fast commit.",
                1_432_056,
                "c8d129c67821c1592cac46836686b057ecc203dc"),

            new LocalResourceDefinition(
                LocalResourceIds.OcrEnglish,
                "English OCR language data",
                "Bundled with Layal",
                "app:tessdata/eng.traineddata",
                LocalResourceKind.EmbeddedData,
                true,
                null,
                null,
                "Verify the packaged file length and Git blob SHA-1 from the pinned tessdata_fast commit.",
                4_113_088,
                "bbef4675053b5b468cdb477053e28b1c698ba08e")
        });

    static LocalResourceCatalog()
    {
        var duplicate = Definitions
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException("Duplicate local resource catalog id: " + duplicate.Key);

        foreach (var item in Definitions)
        {
            if (string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.DisplayName) ||
                string.IsNullOrWhiteSpace(item.Version) ||
                string.IsNullOrWhiteSpace(item.Source) ||
                string.IsNullOrWhiteSpace(item.VerificationPolicy))
                throw new InvalidOperationException("A local resource catalog entry contains an empty required field.");

            if (item.ExpectedSha256 is not null && !item.HasPinnedSha256)
                throw new InvalidOperationException("Invalid SHA-256 in local resource catalog entry: " + item.Id);

            if (item.ExpectedSizeBytes is <= 0)
                throw new InvalidOperationException("Invalid expected size in local resource catalog entry: " + item.Id);

            if ((item.ExpectedSizeBytes is null) != (item.ExpectedSha256 is null))
                throw new InvalidOperationException("A directly downloadable catalog resource must pin both size and SHA-256: " + item.Id);

            if ((item.ExpectedGitBlobSizeBytes is null) != (item.ExpectedGitBlobSha1 is null))
                throw new InvalidOperationException("An embedded Git-verified resource must pin both size and Git blob SHA-1: " + item.Id);
            if (item.ExpectedGitBlobSizeBytes is <= 0 ||
                (item.ExpectedGitBlobSha1 is not null &&
                 (item.ExpectedGitBlobSha1.Length != 40 || !item.ExpectedGitBlobSha1.All(Uri.IsHexDigit))))
                throw new InvalidOperationException("Invalid pinned Git blob identity in local resource catalog entry: " + item.Id);

            if (item.ExpectedFiles is not null)
            {
                if (item.ExpectedFiles.Count == 0)
                    throw new InvalidOperationException("A pinned file collection must not be empty: " + item.Id);

                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in item.ExpectedFiles)
                {
                    var segments = file.RelativePath.Replace('\\', '/').Split('/');
                    if (string.IsNullOrWhiteSpace(file.RelativePath) ||
                        Path.IsPathRooted(file.RelativePath) ||
                        segments.Any(segment => segment is "" or "." or "..") ||
                        file.ExpectedSizeBytes <= 0 ||
                        !file.HasPinnedSha256 ||
                        !paths.Add(file.RelativePath))
                        throw new InvalidOperationException("Invalid or duplicate pinned file in resource catalog: " + item.Id);
                }
            }
        }
    }

    public static IReadOnlyList<LocalResourceDefinition> All => Definitions;

    public static LocalResourceDefinition GetRequired(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Definitions.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("Unknown local resource id: " + id);
    }

    public static bool TryGet(string id, out LocalResourceDefinition? definition)
    {
        definition = Definitions.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        return definition is not null;
    }
}
