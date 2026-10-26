using System.Net;
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
}
