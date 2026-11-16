using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public sealed record LocalResourceDefinition(
    string Id,
    string DisplayName,
    string Version,
    string Source,
    LocalResourceKind Kind,
    bool Required,
    long? ExpectedSizeBytes,
    string? ExpectedSha256,
    string VerificationPolicy)
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
                "3.10–3.12",
                "https://www.python.org/downloads/windows/",
                LocalResourceKind.Runtime,
                true,
                null,
                null,
                "Verify Authenticode signature, x64 architecture, and supported Python version."),

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
                "Verify the immutable Hugging Face revision, required model files, and local health endpoint."),

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
                "Verify the packaged file exists and matches the application resource manifest."),

            new LocalResourceDefinition(
                LocalResourceIds.OcrEnglish,
                "English OCR language data",
                "Bundled with Layal",
                "app:tessdata/eng.traineddata",
                LocalResourceKind.EmbeddedData,
                true,
                null,
                null,
                "Verify the packaged file exists and matches the application resource manifest.")
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
