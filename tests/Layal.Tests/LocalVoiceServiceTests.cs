using System.IO;
using System.Reflection;
using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalVoiceServiceTests
{
    [TestMethod]
    public void LocalVoiceSpeechPreparationRemovesMarkupAndUrlsWithoutChangingVisibleWords()
    {
        var normalized = LocalVoiceService.NormalizeTextForSpeech(
            "## أهلًا **ليال** [الموقع](https://example.test) نموذج");

        Assert.AreEqual("أهلًا ليال الموقع نموذج", normalized);
        Assert.IsEmpty(LocalVoiceService.NormalizeTextForSpeech("   "));
    }

    [TestMethod]
    public void LocalVoiceSpeechPreparationBoundsLongOutput()
    {
        var normalized = LocalVoiceService.NormalizeTextForSpeech(new string('ا', 7000));

        Assert.AreEqual(6000, normalized.Length);
    }

    [TestMethod]
    public void VoiceTutUsesAStableFemaleChildLikeVoiceDesignPrompt()
    {
        Assert.AreEqual("female, child, very high pitch", VoiceTutLocalService.DefaultVoiceDesign);
        StringAssert.Contains(VoiceTutLocalService.DefaultVoiceDesign, "female");
        StringAssert.Contains(VoiceTutLocalService.DefaultVoiceDesign, "child");
        Assert.AreEqual(40, VoiceTutLocalService.ModelRevision.Length);
        Assert.IsTrue(VoiceTutLocalService.ModelRevision.All(Uri.IsHexDigit));
    }

    [TestMethod]
    public void VoiceTutVoiceDesignRejectsReferenceAudioAndCloningPrompts()
    {
        var method = typeof(VoiceTutLocalService).GetMethod(
            "ValidateVoiceInstruction",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "Voice Design prompt validation must be present.");

        var accepted = (string)method!.Invoke(null, new object?[] { "female, child, very high pitch" })!;
        Assert.AreEqual("female, child, very high pitch", accepted);
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            method.Invoke(null, new object?[] { "female, use ref_audio for voice cloning" }));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            method.Invoke(null, new object?[] { "male, adult" }));
    }

    [TestMethod]
    public void WindowsDefaultOutputUsesWasapiSharedMode()
    {
        var method = typeof(VoiceTutLocalService).GetMethod(
            "UsesWasapiForDefaultOutput",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "Expected the default-output backend policy.");

        Assert.IsTrue((bool)method!.Invoke(null, new object?[] { AudioDeviceService.WindowsDefaultDeviceId })!);
        Assert.IsTrue((bool)method.Invoke(null, new object?[] { null })!);
        Assert.IsFalse((bool)method.Invoke(null, new object?[] { "winmm:custom" })!);
    }

    [TestMethod]
    public void LocalVoiceCleanupDeletesOnlyOldGeneratedAudioAndTranscriptFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "layal-voice-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nowUtc = DateTime.UtcNow;
        try
        {
            var staleWav = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
            var staleJson = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-transcription.json");
            var staleText = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-transcription.txt");
            var recentWav = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
            var unrelatedWav = Path.Combine(directory, "recording.wav");
            var unrelatedText = Path.Combine(directory, "notes.txt");

            foreach (var path in new[] { staleWav, staleJson, staleText, recentWav, unrelatedWav, unrelatedText })
                File.WriteAllText(path, "temporary test data");

            foreach (var path in new[] { staleWav, staleJson, staleText, unrelatedWav, unrelatedText })
                File.SetLastWriteTimeUtc(path, nowUtc.AddHours(-2));
            File.SetLastWriteTimeUtc(recentWav, nowUtc.AddMinutes(-5));

            InvokeTemporaryArtifactCleanup(directory, nowUtc);

            Assert.IsFalse(File.Exists(staleWav), "Old generated WAV artifacts must be removed.");
            Assert.IsFalse(File.Exists(staleJson), "Old Whisper JSON artifacts must be removed.");
            Assert.IsFalse(File.Exists(staleText), "Old Whisper text artifacts must be removed.");
            Assert.IsTrue(File.Exists(recentWav), "Recent audio may belong to an active voice operation.");
            Assert.IsTrue(File.Exists(unrelatedWav), "Files without Layal's generated name must be preserved.");
            Assert.IsTrue(File.Exists(unrelatedText), "Unrelated text files must be preserved.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void LocalVoiceCleanupFailsClosedWhenItCannotDeleteAStaleAudioArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), "layal-voice-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nowUtc = DateTime.UtcNow;
        try
        {
            var staleWav = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllText(staleWav, "sensitive audio placeholder");
            File.SetLastWriteTimeUtc(staleWav, nowUtc.AddHours(-2));

            using var lockedFile = new FileStream(staleWav, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var exception = Assert.ThrowsExactly<TargetInvocationException>(
                () => GetTemporaryArtifactCleanupMethod().Invoke(null, new object[] { directory, nowUtc }));

            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            StringAssert.Contains(exception.InnerException!.Message, "أوقفنا المعالجة");
            Assert.IsTrue(File.Exists(staleWav), "The failed cleanup must not pretend the artifact was removed.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void VoiceResourcesUseTheIsolatedLayalDataRoot()
    {
        var variable = LayalDataPathPolicy.AcceptanceRootEnvironmentVariable;
        var previousValue = Environment.GetEnvironmentVariable(variable);
        var acceptanceRoot = Path.Combine(Path.GetTempPath(), "LayalSpeechAcceptance", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(variable, acceptanceRoot);
            var expected = Path.GetFullPath(Path.Combine(acceptanceRoot, "LocalAppData", "Layal", "LocalAi"));
            Assert.AreEqual(expected, LocalVoiceService.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousValue);
        }
    }

    [TestMethod]
    public void SuccessfulVoiceTutMigrationRemovesOnlyKnownLegacyPiperAssets()
    {
        var variable = LayalDataPathPolicy.AcceptanceRootEnvironmentVariable;
        var previousValue = Environment.GetEnvironmentVariable(variable);
        var acceptanceRoot = Path.Combine(Path.GetTempPath(), "LayalPiperMigration", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(acceptanceRoot, "LocalAppData", "Layal", "LocalAi");
        var modelsRoot = Path.Combine(dataRoot, "speech-models");
        var toolsRoot = Path.Combine(dataRoot, "tools");
        var piperRoot = Path.Combine(toolsRoot, "piper");
        try
        {
            Directory.CreateDirectory(modelsRoot);
            Directory.CreateDirectory(piperRoot);
            foreach (var name in new[]
            {
                "ar_JO-kareem-medium.onnx",
                "ar_JO-kareem-medium.onnx.json",
                "en_US-lessac-medium.onnx",
                "en_US-lessac-medium.onnx.json",
                "other-voice.onnx",
                "ggml-base.bin"
            })
                File.WriteAllText(Path.Combine(modelsRoot, name), "test");

            File.WriteAllText(Path.Combine(dataRoot, "piper-windows-amd64-2023.11.14-2.zip"), "archive");
            File.WriteAllText(Path.Combine(piperRoot, "piper.exe"), "binary");
            var keepFile = Path.Combine(toolsRoot, "whisper", "whisper-cli.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(keepFile)!);
            File.WriteAllText(keepFile, "whisper");

            Environment.SetEnvironmentVariable(variable, acceptanceRoot);
            var method = typeof(LocalVoiceService).GetMethod(
                "DeleteLegacyPiperResources",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "The explicit Piper migration cleanup must exist.");
            method!.Invoke(null, null);

            foreach (var name in new[]
            {
                "ar_JO-kareem-medium.onnx",
                "ar_JO-kareem-medium.onnx.json",
                "en_US-lessac-medium.onnx",
                "en_US-lessac-medium.onnx.json"
            })
                Assert.IsFalse(File.Exists(Path.Combine(modelsRoot, name)), name + " must be removed.");

            Assert.IsTrue(File.Exists(Path.Combine(modelsRoot, "other-voice.onnx")));
            Assert.IsTrue(File.Exists(Path.Combine(modelsRoot, "ggml-base.bin")));
            Assert.IsTrue(File.Exists(keepFile), "Whisper files must not be removed.");
            Assert.IsFalse(File.Exists(Path.Combine(dataRoot, "piper-windows-amd64-2023.11.14-2.zip")));
            Assert.IsFalse(Directory.Exists(piperRoot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousValue);
            if (Directory.Exists(acceptanceRoot))
                Directory.Delete(acceptanceRoot, recursive: true);
        }
    }

    private static MethodInfo GetTemporaryArtifactCleanupMethod()
    {
        var method = typeof(LocalVoiceService).GetMethod(
            "CleanupStaleTemporaryArtifacts",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "Expected stale voice-artifact cleanup method to exist.");
        return method!;
    }

    private static void InvokeTemporaryArtifactCleanup(string directory, DateTime nowUtc) =>
        GetTemporaryArtifactCleanupMethod().Invoke(null, new object[] { directory, nowUtc });
}
