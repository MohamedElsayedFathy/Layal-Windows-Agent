namespace Layal.Services;

/// <summary>
/// Fail-closed local data-access rules while a temporary private chat is active.
/// This policy does not control or promise retention behavior at external AI providers.
/// </summary>
public static class PrivateChatModePolicy
{
    public static bool CanAccessTaskWorkspace(bool privateChatActive) => !privateChatActive;

    public static bool CanStartAgentTask(bool privateChatActive) =>
        CanAccessTaskWorkspace(privateChatActive);

    public static bool CanAccessTaskDetails(bool privateChatActive) =>
        CanAccessTaskWorkspace(privateChatActive);

    public static bool CanCopyTaskData(bool privateChatActive) =>
        CanAccessTaskWorkspace(privateChatActive);

    public static bool CanAccessPersonalMemory(bool privateChatActive) => !privateChatActive;

    public static bool CanAccessSavedChatHistory(bool privateChatActive) => !privateChatActive;

    public static bool CanPersistChatMessages(bool privateChatActive) => !privateChatActive;

    public static bool CanUseQuickAsk(bool privateChatActive) => !privateChatActive;

    public static bool CanUseGmail(bool privateChatActive) => !privateChatActive;
}
