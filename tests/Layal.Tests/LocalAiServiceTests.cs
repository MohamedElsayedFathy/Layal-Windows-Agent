using System.IO;
using System.Net;
using System.Reflection;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalAiServiceTests
{
    [TestMethod]
    public void LocalChatDefaultsToAnOllamaLoopbackEndpointAndSmallModel()
    {
        Assert.AreEqual("qwen3.5:2b", LocalAiService.DefaultModelId);
        Assert.AreEqual("http://127.0.0.1:11434/", LocalAiService.ApiBaseUri.AbsoluteUri);
        Assert.IsTrue(IPAddress.IsLoopback(IPAddress.Parse(LocalAiService.ApiBaseUri.Host)));
        Assert.AreEqual("qwen3.5:2b", LocalAiService.ValidateModelId("qwen3.5:2b"));
    }

    [TestMethod]
    public void ValidateModelIdAcceptsNamespacedModelTagsButRejectsUnsafeValues()
    {
        Assert.AreEqual("qwen3.5:2b", LocalAiService.ValidateModelId("  qwen3.5:2b  "));
        Assert.AreEqual("library/llama3:8b", LocalAiService.ValidateModelId("library/llama3:8b"));

        foreach (var invalid in new string?[] { null, "", " ", "../admin", "model..tag", "https://remote.test", "qwen3:2b;curl", "bad model" })
            Assert.ThrowsExactly<InvalidOperationException>(() => LocalAiService.ValidateModelId(invalid));
    }

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
            StringAssert.Contains(exception.InnerException!.Message, "الخصوصية");
            Assert.IsTrue(File.Exists(staleWav), "The failed cleanup must not pretend the artifact was removed.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static MethodInfo GetTemporaryArtifactCleanupMethod()
    {
        var method = typeof(LocalVoiceService).GetMethod(
            "CleanupStaleTemporaryArtifacts",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "Expected the stale voice-artifact cleanup method to exist.");
        return method!;
    }

    private static void InvokeTemporaryArtifactCleanup(string directory, DateTime nowUtc) =>
        GetTemporaryArtifactCleanupMethod().Invoke(null, new object[] { directory, nowUtc });

}
