using System;

namespace Layal.Services;

/// <summary>
/// Pure due-time policy for optional local check-in notifications. It never inspects apps,
/// windows, files, the screen, the network, or an AI provider.
/// </summary>
public static class ProactiveCheckInPolicy
{
    public static bool ShouldNotify(
        bool enabled,
        bool privacyShieldEnabled,
        bool gameModeActive,
        bool applicationExiting,
        DateTimeOffset? lastNotifiedAt,
        DateTimeOffset now,
        int intervalMinutes)
    {
        if (!enabled || privacyShieldEnabled || gameModeActive || applicationExiting ||
            lastNotifiedAt is null || now < lastNotifiedAt.Value)
            return false;

        if (intervalMinutes is not (15 or 30 or 60 or 120))
            return false;

        return now - lastNotifiedAt.Value >= TimeSpan.FromMinutes(intervalMinutes);
    }
}
