using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

/// <summary>
/// Retained under the historical coverage-map filename. The former Ollama/Qwen
/// tests have been replaced with assertions for Layal's model-independent core.
/// </summary>
[TestClass]
public sealed class LocalAiServiceTests
{
    [TestMethod]
    public void NativeConversationRepliesWithoutAnAiModelService()
    {
        using var temp = new TestTempDirectory();
        var learning = new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi"));
        var parser = new NativeLanguageParser(learning);
        var memory = new MemoryService(Path.Combine(temp.Path, "memory.dpapi"));
        var conversation = new NativeConversationEngine(parser, learning, memory);

        var reply = conversation.Respond("hello", "en");

        StringAssert.Contains(reply, "native local core");
    }

    [TestMethod]
    public void MicrophoneCaptureHasBoundedRecordingAndNoNetworkTranscriptionApi()
    {
        Assert.AreEqual(120, MicrophoneCaptureService.MaximumRecordingSeconds);
        Assert.IsNotNull(typeof(MicrophoneCaptureService).GetMethod("StartRecording"));
        Assert.IsNotNull(typeof(MicrophoneCaptureService).GetMethod("StopRecordingAsync"));
        Assert.IsNull(typeof(MicrophoneCaptureService).GetMethod("TranscribeAsync"));
    }
}
