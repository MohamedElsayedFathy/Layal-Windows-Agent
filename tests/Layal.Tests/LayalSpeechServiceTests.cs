using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LayalSpeechServiceTests
{
    [TestMethod]
    public void VoiceCharacterDescription_BalancesChildlikeToneWithNaturalConversationalDelivery()
    {
        StringAssert.Contains(LayalSpeechService.VoiceCharacterDescription, "distinctly childlike and feminine");
        StringAssert.Contains(LayalSpeechService.VoiceCharacterDescription, "young-girl character");
        StringAssert.Contains(LayalSpeechService.VoiceCharacterDescription, "vary naturally");
        StringAssert.Contains(LayalSpeechService.VoiceCharacterDescription, "sing-song delivery");
        StringAssert.Contains(LayalSpeechService.VoiceCharacterDescription, "robotic, evenly timed cadence");
        StringAssert.Contains(LayalSpeechService.VoiceCharacterDescription, "mature adult woman");
    }

    [TestMethod]
    public void BuildRealtimeUri_UsesSecureRealtimeEndpointAndConfiguredModel()
    {
        var uri = LayalSpeechService.BuildRealtimeUri(
            "https://api.example/v1/", "gpt-realtime-2.1-mini");

        Assert.AreEqual("wss", uri.Scheme);
        Assert.AreEqual("/v1/realtime", uri.AbsolutePath);
        Assert.AreEqual("model=gpt-realtime-2.1-mini", uri.Query.TrimStart('?'));
    }

    [TestMethod]
    public void BuildRealtimeUri_AllowsLoopbackHttpForLocalProviders()
    {
        var uri = LayalSpeechService.BuildRealtimeUri(
            "http://127.0.0.1:31415/v1", "local-realtime");

        Assert.AreEqual("ws", uri.Scheme);
        Assert.AreEqual("127.0.0.1", uri.Host);
        Assert.AreEqual("/v1/realtime", uri.AbsolutePath);
    }

    [TestMethod]
    public void BuildRealtimeUri_RejectsRemoteHttpAndEmbeddedCredentials()
    {
        AssertInvalidOperation(() =>
            LayalSpeechService.BuildRealtimeUri("http://speech.example/v1"));

        AssertInvalidOperation(() =>
            LayalSpeechService.BuildRealtimeUri("https://user:password@api.example/v1"));

        AssertInvalidOperation(() =>
            LayalSpeechService.BuildRealtimeUri("https://api.example/v1?token=secret"));
    }

    private static void AssertInvalidOperation(Action action)
    {
        var thrown = false;
        try { action(); }
        catch (InvalidOperationException) { thrown = true; }
        Assert.IsTrue(thrown, "Expected an InvalidOperationException.");
    }

    [TestMethod]
    public void PrepareTextForSpeech_RemovesMarkupAndLinksButKeepsVisibleText()
    {
        var fence = new string((char)96, 3);
        var text = "# أهلًا بكم\n\nهذه **ليال**. [افتح الدليل](https://example.com)\n\n" +
                   fence + "csharp\nsecret code\n" + fence;

        var prepared = LayalSpeechService.PrepareTextForSpeech(text);

        StringAssert.Contains(prepared, "أهلًا بكم");
        StringAssert.Contains(prepared, "هذه ليال.");
        StringAssert.Contains(prepared, "افتح الدليل");
        Assert.IsFalse(prepared.Contains("https://", StringComparison.Ordinal));
        Assert.IsFalse(prepared.Contains("secret code", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SplitTextForSpeech_ProducesBoundedNonEmptyChunks()
    {
        var text = string.Join(" ", Enumerable.Repeat("جملة ليال الهادئة.", 500));

        var chunks = LayalSpeechService.SplitTextForSpeech(text, 400);

        Assert.IsTrue(chunks.Length > 1);
        Assert.IsTrue(chunks.All(chunk => chunk.Length is > 0 and <= 400));
        Assert.AreEqual(text.Replace(" ", string.Empty, StringComparison.Ordinal),
            string.Join(" ", chunks).Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [TestMethod]
    public void IsSupportedVoice_OnlyAcceptsKnownVoicePresets()
    {
        Assert.IsTrue(LayalSpeechService.IsSupportedVoice(LayalSpeechService.DefaultVoiceId));
        Assert.IsTrue(LayalSpeechService.IsSupportedVoice("shimmer"));
        Assert.IsTrue(LayalSpeechService.IsSupportedVoice("MARIN"));
        Assert.IsFalse(LayalSpeechService.IsSupportedVoice("untrusted-voice"));
        Assert.IsFalse(LayalSpeechService.IsSupportedVoice(" "));
    }
}
