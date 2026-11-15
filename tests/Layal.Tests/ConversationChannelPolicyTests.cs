using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ConversationChannelPolicyTests
{
    [TestMethod]
    public void TextChatWritesToVisibleHistoryAndNeverSpeaksAutomatically()
    {
        Assert.IsTrue(ConversationChannelPolicy.WritesMessagesToVisibleChat(ConversationChannel.Text));
        Assert.IsTrue(ConversationChannelPolicy.PersistsConversationHistory(ConversationChannel.Text));
        Assert.IsFalse(ConversationChannelPolicy.SpeaksAssistantReply(ConversationChannel.Text, voiceRepliesEnabled: true));
    }

    [TestMethod]
    public void VoiceConversationSpeaksButNeverWritesIntoTextChatOrSavedHistory()
    {
        Assert.IsFalse(ConversationChannelPolicy.WritesMessagesToVisibleChat(ConversationChannel.Voice));
        Assert.IsFalse(ConversationChannelPolicy.PersistsConversationHistory(ConversationChannel.Voice));
        Assert.IsTrue(ConversationChannelPolicy.SpeaksAssistantReply(ConversationChannel.Voice, voiceRepliesEnabled: true));
        Assert.IsFalse(ConversationChannelPolicy.SpeaksAssistantReply(ConversationChannel.Voice, voiceRepliesEnabled: false));
    }

    [TestMethod]
    public void DefaultLocalChatModelIsQwen35TwoB()
    {
        Assert.AreEqual("qwen3.5:2b", OllamaLocalModelSetupService.DefaultModelId);
        Assert.AreEqual("2.7–3.1 GB", OllamaLocalModelSetupService.ModelSizeDescription);
    }
}
