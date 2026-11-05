using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using System.Windows;

namespace Layal.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private bool _disposed;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _mascotItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Drawing.Icon? _ownedIcon;
    private readonly NotificationActionSlot _reminderClickAction = new();
    private readonly ReminderNotificationQueue _reminderNotificationQueue = new();

    public TrayService(Action showMain, Action showMascot, Action exit, string language)
    {
        _menu = new Forms.ContextMenuStrip();
        _openItem = new Forms.ToolStripMenuItem();
        _mascotItem = new Forms.ToolStripMenuItem();
        _exitItem = new Forms.ToolStripMenuItem();
        _openItem.Click += (_, _) => showMain();
        _mascotItem.Click += (_, _) => showMascot();
        _exitItem.Click += (_, _) => exit();
        _menu.Items.AddRange(new Forms.ToolStripItem[] { _openItem, _mascotItem, new Forms.ToolStripSeparator(), _exitItem });

        Drawing.Icon trayIcon;
        try
        {
            // Explicit assembly identity avoids resolving the pack URI against a test host
            // or another entry assembly when the WPF app is rendered by automation.
            var iconUri = new Uri("pack://application:,,,/Layal;component/Assets/layal-tray.ico", UriKind.Absolute);
            var iconResource = Application.GetResourceStream(iconUri);
            if (iconResource is not null)
            {
                using (iconResource.Stream) _ownedIcon = new Drawing.Icon(iconResource.Stream);
                trayIcon = _ownedIcon!;
            }
            else
            {
                trayIcon = Drawing.SystemIcons.Application;
            }
        }
        catch (System.IO.IOException)
        {
            // Tray creation must not prevent the desktop agent from starting if a package
            // was built without its optional branded icon resource.
            trayIcon = Drawing.SystemIcons.Application;
        }

        _icon = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Layal | ليال",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => showMain();
        _icon.BalloonTipClicked += ReminderBalloonTipClicked;
        _icon.BalloonTipClosed += ReminderBalloonTipClosed;
        SetLanguage(language);
    }

    public void ShowReminderNotification(string reminderTitle, bool isArabic, Action? onClick = null)
    {
        if (_disposed || !_icon.Visible)
            return;

        var safeTitle = (reminderTitle ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (safeTitle.Length > 220)
            safeTitle = safeTitle[..220] + "…";
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = isArabic ? "تذكير محلي" : "Local reminder";

        if (!_reminderNotificationQueue.Enqueue(new ReminderNotification(safeTitle, isArabic, onClick)))
        {
            System.Diagnostics.Trace.TraceWarning("The Layal reminder notification queue is full; the new notification was not displayed.");
            return;
        }

        ShowNextReminderNotification();
    }

    /// <summary>Shows one actionable digest when several reminders become due together.</summary>
    public void ShowReminderSummary(int reminderCount, bool isArabic, Action? onClick = null)
    {
        if (reminderCount <= 0)
            return;
        var summary = isArabic
            ? $"لديك {reminderCount} تذكيرات مستحقة. افتح ليال لمراجعتها."
            : $"You have {reminderCount} reminders due. Open Layal to review them.";
        ShowReminderNotification(summary, isArabic, onClick);
    }

    private void ShowNextReminderNotification()
    {
        while (!_disposed && _icon.Visible &&
               _reminderNotificationQueue.TryBeginNext(out var notification))
        {
            var pending = notification!;
            _reminderClickAction.Arm(pending.OnClick, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(7));
            try
            {
                _icon.ShowBalloonTip(
                    6000,
                    pending.IsArabic ? "حان موعد التذكير" : "Reminder is due",
                    pending.Title,
                    Forms.ToolTipIcon.Info);
                return;
            }
            catch (Exception ex)
            {
                _reminderClickAction.Clear();
                _reminderNotificationQueue.CompleteActive();
                System.Diagnostics.Trace.TraceWarning("Reminder notification could not be displayed: {0}", ex.Message);
            }
        }
    }

    private void ReminderBalloonTipClicked(object? sender, EventArgs e)
    {
        try { _reminderClickAction.Take(DateTimeOffset.UtcNow)?.Invoke(); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Reminder notification action failed: {0}", ex.Message);
        }
    }

    private void ReminderBalloonTipClosed(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        _reminderClickAction.Clear();
        _reminderNotificationQueue.CompleteActive();
        ShowNextReminderNotification();
    }

    public void SetLanguage(string language)
    {
        var ar = language == "ar";
        _openItem.Text = ar ? "فتح ليال" : "Open Layal";
        _mascotItem.Text = ar ? "إظهار الشخصية" : "Show mascot";
        _exitItem.Text = ar ? "خروج" : "Exit";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reminderNotificationQueue.Clear();
        _reminderClickAction.Clear();
        _icon.BalloonTipClicked -= ReminderBalloonTipClicked;
        _icon.BalloonTipClosed -= ReminderBalloonTipClosed;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
