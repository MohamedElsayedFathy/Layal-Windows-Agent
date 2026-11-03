using System;
using System.Reflection;
using System.Threading.Channels;
using System.Text.Json;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

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

    [TestMethod]
    public void CaptureQueueBuffersShortStallsAndNeverSilentlyDropsFrames()
    {
        var queue = InvokePrivateFactory<Channel<CapturedMicrophoneFrame>>("CreateCaptureQueue");

        for (var i = 0; i < VoiceConversationService.CaptureQueueCapacityFrames; i++)
            Assert.IsTrue(queue.Writer.TryWrite(new CapturedMicrophoneFrame(new byte[] { (byte)i, 0 }, 0)), $"Frame {i} should fit in the bounded queue.");

        Assert.IsFalse(queue.Writer.TryWrite(new CapturedMicrophoneFrame(new byte[] { 255, 0 }, 0)),
            "A full queue must report backpressure instead of silently dropping an older frame.");

        var drained = 0;
        while (queue.Reader.TryRead(out var frame))
        {
            Array.Clear(frame.PcmBytes, 0, frame.PcmBytes.Length);
            drained++;
        }

        Assert.AreEqual(VoiceConversationService.CaptureQueueCapacityFrames, drained,
            "All accepted microphone frames must remain available for sending.");
        Assert.IsTrue(drained >= 60,
            "The queue should absorb about three seconds of 50 ms microphone frames before failing closed.");
        queue.Writer.TryComplete();
    }

    [TestMethod]
    public void PlaybackBufferRejectsOverflowInsteadOfDiscardingAssistantAudio()
    {
        var playback = InvokePrivateFactory<BufferedWaveProvider>("CreatePlaybackBuffer");

        Assert.IsFalse(playback.DiscardOnBufferOverflow,
            "The assistant audio provider must not discard samples silently.");
        Assert.AreEqual(TimeSpan.FromSeconds(5), playback.BufferDuration);

        var tooMuchAudio = new byte[playback.BufferLength + 2];
        var overflowThrown = false;
        try { playback.AddSamples(tooMuchAudio, 0, tooMuchAudio.Length); }
        catch (InvalidOperationException) { overflowThrown = true; }
        Assert.IsTrue(overflowThrown, "The provider must raise an overflow instead of discarding assistant audio.");
        Assert.AreEqual(playback.BufferLength, playback.BufferedBytes,
            "Overflow is surfaced explicitly; the caller then aborts and clears the damaged response.");
        playback.ClearBuffer();
    }

    [TestMethod]
    public void CreateMicrophoneFrame_CopiesPcmWhenUnmuted()
    {
        byte[] source = [0, 1, 2, 3, 4, 5];
        var frame = VoiceConversationService.CreateMicrophoneFrame(source, source.Length, muted: false);

        CollectionAssert.AreEqual(source, frame);
        frame[0] = 255;
        Assert.AreEqual(0, source[0], "Queued frames must not alias NAudio's reusable capture buffer.");
    }

    [TestMethod]
    public void CreateMicrophoneFrame_ProducesSilenceWhenMuted()
    {
        byte[] source = [7, 9, 11, 13, 15, 17];
        var frame = VoiceConversationService.CreateMicrophoneFrame(source, source.Length, muted: true);

        CollectionAssert.AreEqual(new byte[source.Length], frame);
        Assert.AreNotEqual(source[0], frame[0]);
    }

    [TestMethod]
    public void CreateMicrophoneFrame_RejectsPartialPcmSamples()
    {
        byte[] source = [1, 2, 3];
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            VoiceConversationService.CreateMicrophoneFrame(source, 3, muted: false));
    }

    [TestMethod]
    public void CapturedMicrophoneFrame_MuteGenerationInvalidatesQueuedAudioAfterQuickUnmute()
    {
        byte[] source = [10, 20, 30, 40];
        var capturedBeforeMute = new CapturedMicrophoneFrame(
            VoiceConversationService.CreateMicrophoneFrame(source, source.Length, muted: false),
            MuteGeneration: 4);
        var generationAfterMuteAndUnmute = 6;

        Assert.IsTrue(VoiceConversationService.ShouldSendMicrophoneSilence(
            microphoneMuted: false,
            capturedBeforeMute.MuteGeneration,
            generationAfterMuteAndUnmute),
            "A frame from before mute must be converted to silence even after the user unmutes quickly.");
        Assert.IsTrue(VoiceConversationService.ShouldSendMicrophoneSilence(
            microphoneMuted: true,
            generationAfterMuteAndUnmute,
            generationAfterMuteAndUnmute));
        Assert.IsFalse(VoiceConversationService.ShouldSendMicrophoneSilence(
            microphoneMuted: false,
            generationAfterMuteAndUnmute,
            generationAfterMuteAndUnmute));
        Array.Clear(capturedBeforeMute.PcmBytes, 0, capturedBeforeMute.PcmBytes.Length);
        CollectionAssert.AreEqual(new byte[source.Length], capturedBeforeMute.PcmBytes);
    }

    private static T InvokePrivateFactory<T>(string methodName)
    {
        var method = typeof(VoiceConversationService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, $"Expected production factory {methodName} to exist.");
        var result = method!.Invoke(null, null);
        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result, typeof(T));
        return (T)result!;
    }

    private static void AssertInvalidOperation(Action action)
    {
        var thrown = false;
        try { action(); }
        catch (InvalidOperationException) { thrown = true; }
        Assert.IsTrue(thrown, "Expected an InvalidOperationException.");
    }
}
