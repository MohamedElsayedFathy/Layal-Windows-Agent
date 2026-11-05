namespace Layal.Services;

/// <summary>
/// Holds a single-use notification click action for the lifetime of its visible toast.
/// The slot is intentionally local and never persists notification callbacks.
/// </summary>
public sealed class NotificationActionSlot
{
    private readonly object _sync = new();
    private Action? _action;
    private DateTimeOffset _expiresAt;

    public void Arm(Action? action, DateTimeOffset now, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "The notification action lifetime must be positive.");

        lock (_sync)
        {
            _action = action;
            _expiresAt = now + lifetime;
        }
    }

    /// <summary>Returns the action once, only while the notification is still valid.</summary>
    public Action? Take(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_action is null || now >= _expiresAt)
            {
                _action = null;
                _expiresAt = default;
                return null;
            }

            var action = _action;
            _action = null;
            _expiresAt = default;
            return action;
        }
    }

    /// <summary>Clears the active action when its matching tray notification closes.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _action = null;
            _expiresAt = default;
        }
    }
}
