namespace Layal.Services;

public enum ConversationChannel
{
    Text,
    Voice
}

/// <summary>Enforces channel isolation between the visible text chat and turn-based voice conversation.</summary>
public static class ConversationChannelPolicy
{
    public static bool WritesMessagesToVisibleChat(ConversationChannel channel) =>
        channel == ConversationChannel.Text;

    public static bool SpeaksAssistantReply(ConversationChannel channel, bool voiceRepliesEnabled) =>
        channel == ConversationChannel.Voice && voiceRepliesEnabled;

    public static bool PersistsConversationHistory(ConversationChannel channel) =>
        channel == ConversationChannel.Text;
}
