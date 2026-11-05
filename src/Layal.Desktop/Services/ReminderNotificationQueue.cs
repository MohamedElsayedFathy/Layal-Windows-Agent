using System;
using System.Collections.Generic;

namespace Layal.Services;

public sealed record ReminderNotification(string Title, bool IsArabic, Action? OnClick);

/// <summary>
/// Serializes tray balloons so the single-use click callback always belongs to the
/// notification currently displayed by Windows NotifyIcon.
/// </summary>
public sealed class ReminderNotificationQueue
{
    public const int MaximumPendingNotifications = 200;

    private readonly object _sync = new();
    private readonly Queue<ReminderNotification> _pending = new();
    private bool _hasActiveNotification;

    public bool Enqueue(ReminderNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_sync)
        {
            if (_pending.Count >= MaximumPendingNotifications)
                return false;
            _pending.Enqueue(notification);
            return true;
        }
    }

    public bool TryBeginNext(out ReminderNotification? notification)
    {
        lock (_sync)
        {
            if (_hasActiveNotification || _pending.Count == 0)
            {
                notification = null;
                return false;
            }

            _hasActiveNotification = true;
            notification = _pending.Dequeue();
            return true;
        }
    }

    public bool CompleteActive()
    {
        lock (_sync)
        {
            if (!_hasActiveNotification)
                return false;
            _hasActiveNotification = false;
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _pending.Clear();
            _hasActiveNotification = false;
        }
    }
}
