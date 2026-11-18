using System;

namespace Layal.Services;

/// <summary>
/// Controls first-run wizard visibility while preserving the isolation guarantees of
/// interactive Windows acceptance sessions.
/// </summary>
public static class CompleteSetupStartupPolicy
{
    public const string AcceptanceOverrideEnvironmentVariable = "LAYAL_SHOW_COMPLETE_SETUP_FOR_ACCEPTANCE";

    public static bool ShouldShow(
        bool isScheduledReminderLaunch,
        bool isIsolatedAcceptance,
        string? acceptanceOverride)
    {
        if (isScheduledReminderLaunch)
            return false;

        if (!isIsolatedAcceptance)
            return true;

        // The acceptance override is intentionally effective only inside a separately
        // rooted test profile. Production launches cannot opt into it.
        return string.Equals(acceptanceOverride, "1", StringComparison.Ordinal);
    }
}
