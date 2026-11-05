using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Services;

/// <summary>
/// Tracks a user-dismissed automatic HUD for the current foreground process. The dismissal
/// is cleared as soon as focus leaves that process or moves to another allowed process.
/// No process enumeration, window-title inspection, hooks, or input monitoring is involved.
/// </summary>
public sealed class GameModeAutoDismissalPolicy
{
    private string? _currentAllowedProcessName;
    private string? _dismissedProcessName;

    public bool IsAllowedForeground(
        string? foregroundProcessName,
        IReadOnlyCollection<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);

        if (!GameModeProcessAllowlist.TryNormalizeName(foregroundProcessName, out var normalized) ||
            !allowlist.Any(name =>
                GameModeProcessAllowlist.TryNormalizeName(name, out var allowed) &&
                string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            Reset();
            return false;
        }

        if (_currentAllowedProcessName is not null &&
            !string.Equals(_currentAllowedProcessName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            // A different foreground game starts a fresh HUD session.
            _dismissedProcessName = null;
        }

        _currentAllowedProcessName = normalized;
        return true;
    }

    public bool IsDismissedFor(string? processName) =>
        GameModeProcessAllowlist.TryNormalizeName(processName, out var normalized) &&
        _dismissedProcessName is not null &&
        string.Equals(_dismissedProcessName, normalized, StringComparison.OrdinalIgnoreCase);

    public void DismissCurrentForeground(string? processName)
    {
        if (!GameModeProcessAllowlist.TryNormalizeName(processName, out var normalized) ||
            _currentAllowedProcessName is null ||
            !string.Equals(_currentAllowedProcessName, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _dismissedProcessName = normalized;
    }

    public void Reset()
    {
        _currentAllowedProcessName = null;
        _dismissedProcessName = null;
    }
}
