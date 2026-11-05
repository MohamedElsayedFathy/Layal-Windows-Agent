using System;
using System.Linq;

namespace Layal.Services;

/// <summary>
/// Classifies explicit tool-result failures that must stop a saved routine.
/// Exact, tool-owned prefixes are used instead of substring matching so untrusted
/// page text or file content cannot manufacture a decline signal.
/// </summary>
public static class RoutineToolOutcomePolicy
{
    private static readonly string[] FailurePrefixes =
    {
        "User declined",
        "WEB SEARCH NOT SHARED",
        "The claim search was completed, but the separate contrary-evidence search was declined.",
        "The claim search was completed, but the separate contrary-evidence search could not be completed or shared.",
        "Fact-check could not retrieve the required contrary source.",
        "No direct source opened:",
        "No eligible direct HTTPS source candidate",
        "The approved source page was opened, but its bounded evidence snapshot could not be parsed;"
    };

    public static bool ShouldStopRoutine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return true;

        return FailurePrefixes.Any(prefix =>
            output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
