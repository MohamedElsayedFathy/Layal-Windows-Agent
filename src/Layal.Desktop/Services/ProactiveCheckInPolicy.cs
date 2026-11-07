using System;

namespace Layal.Services;

/// <summary>
/// Pure due-time policy for optional local check-in notifications. It never inspects apps,
/// windows, files, the screen, the network, or an AI provider.
/// </summary>
public static class ProactiveCheckInPolicy
{
    /// <summary>
    /// Returns the next local check-in instant only when the feature is enabled and every
    /// suppression gate is clear. Invalid intervals and unrepresentable timestamps fail closed.
    /// </summary>
    public static DateTimeOffset? GetNextDueAt(
        bool enabled,
        bool privacyShieldEnabled,
        bool gameModeActive,
        bool applicationExiting,
        DateTimeOffset? lastNotifiedAt,
        int intervalMinutes,
        bool privateChatActive = false)
    {
        if (!enabled || privacyShieldEnabled || gameModeActive || applicationExiting ||
            privateChatActive || lastNotifiedAt is null || intervalMinutes is not (15 or 30 or 60 or 120))
            return null;

        try
        {
            return lastNotifiedAt.Value.AddMinutes(intervalMinutes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static bool ShouldNotify(
        bool enabled,
        bool privacyShieldEnabled,
        bool gameModeActive,
        bool applicationExiting,
        DateTimeOffset? lastNotifiedAt,
        DateTimeOffset now,
        int intervalMinutes,
        bool privateChatActive = false)
    {
        if (!enabled || privacyShieldEnabled || gameModeActive || applicationExiting ||
            privateChatActive || lastNotifiedAt is null || now < lastNotifiedAt.Value)
            return false;

        if (intervalMinutes is not (15 or 30 or 60 or 120))
            return false;

        return now - lastNotifiedAt.Value >= TimeSpan.FromMinutes(intervalMinutes);
    }
}
