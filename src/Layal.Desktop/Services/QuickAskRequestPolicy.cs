namespace Layal.Services;

public enum QuickAskBlockReason
{
    None,
    PrivateChat,
    RequestBusy,
    PendingAttachment
}

/// <summary>Fail-closed policy for keeping typed Quick Ask requests separate from private chats and pending attachments.</summary>
public static class QuickAskRequestPolicy
{
    public static QuickAskBlockReason Evaluate(
        bool isPrivateChat,
        bool chatRequestBusy,
        bool voiceConversationBusy,
        bool hasPendingDocument,
        bool hasPendingImage)
    {
        if (isPrivateChat)
            return QuickAskBlockReason.PrivateChat;
        if (chatRequestBusy || voiceConversationBusy)
            return QuickAskBlockReason.RequestBusy;
        if (hasPendingDocument || hasPendingImage)
            return QuickAskBlockReason.PendingAttachment;
        return QuickAskBlockReason.None;
    }

    public static bool ShouldPrepareVoiceResources(bool suppressVoiceReply, bool voiceRepliesEnabled) =>
        !suppressVoiceReply && voiceRepliesEnabled;
}
