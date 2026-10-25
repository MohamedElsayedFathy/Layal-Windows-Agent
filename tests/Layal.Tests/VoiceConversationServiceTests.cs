using System;
using System.Text.Json;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class VoiceConversationServiceTests
{
    [TestMethod]
    public void BuildSessionUpdate_EnablesServerVadAndAutomaticInterruption()
    {
        var update = VoiceConversationService.BuildSessionUpdate(
            "gpt-realtime-2.1-mini", "marin", "You are Layal. Speak naturally.");
        var session = update.GetProperty("session");
        var audio = session.GetProperty("audio");
        var input = audio.GetProperty("input");
        var output = audio.GetProperty("output");
        var vad = input.GetProperty("turn_detection");

        Assert.AreEqual("session.update", update.GetProperty("type").GetString());
        Assert.AreEqual("realtime", session.GetProperty("type").GetString());
        Assert.AreEqual("gpt-realtime-2.1-mini", session.GetProperty("model").GetString());
        Assert.AreEqual("audio", session.GetProperty("output_modalities")[0].GetString());
        Assert.AreEqual("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.AreEqual(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.AreEqual("gpt-4o-mini-transcribe", input.GetProperty("transcription").GetProperty("model").GetString());
        Assert.AreEqual("server_vad", vad.GetProperty("type").GetString());
        Assert.IsTrue(vad.GetProperty("interrupt_response").GetBoolean());
        Assert.IsTrue(vad.GetProperty("create_response").GetBoolean());
        Assert.AreEqual(300, vad.GetProperty("prefix_padding_ms").GetInt32());
        Assert.AreEqual(500, vad.GetProperty("silence_duration_ms").GetInt32());
        Assert.AreEqual("audio/pcm", output.GetProperty("format").GetProperty("type").GetString());
        Assert.AreEqual(24000, output.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.AreEqual("marin", output.GetProperty("voice").GetString());
    }

    [TestMethod]
    public void BuildSessionUpdate_RejectsBlankModelAndUnsupportedVoice()
    {
        AssertInvalidOperation(() =>
            VoiceConversationService.BuildSessionUpdate("", "marin", "Speak naturally."));
        AssertInvalidOperation(() =>
            VoiceConversationService.BuildSessionUpdate("gpt-realtime-2.1-mini", "untrusted-voice", "Speak naturally."));
        AssertInvalidOperation(() =>
            VoiceConversationService.BuildSessionUpdate("gpt-realtime-2.1-mini", "marin", " "));
    }

    private static void AssertInvalidOperation(Action action)
    {
        var thrown = false;
        try { action(); }
        catch (InvalidOperationException) { thrown = true; }
        Assert.IsTrue(thrown, "Expected an InvalidOperationException.");
    }
}
