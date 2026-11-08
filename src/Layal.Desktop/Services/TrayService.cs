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
    private readonly Drawing.Font _menuFont;
    private readonly NotificationActionSlot _reminderClickAction = new();
    private readonly ReminderNotificationQueue _reminderNotificationQueue = new();

    public TrayService(Action showMain, Action showMascot, Action exit, string language, bool darkTheme = false, bool highContrast = false)
    {
        _menu = new Forms.ContextMenuStrip();
        _openItem = new Forms.ToolStripMenuItem();
        _mascotItem = new Forms.ToolStripMenuItem();
        _exitItem = new Forms.ToolStripMenuItem();
        _openItem.Click += (_, _) => showMain();
        _mascotItem.Click += (_, _) => showMascot();
        _exitItem.Click += (_, _) => exit();
        _menu.Items.AddRange(new Forms.ToolStripItem[] { _openItem, _mascotItem, new Forms.ToolStripSeparator(), _exitItem });
        _menu.ShowImageMargin = false;
        _menu.ShowCheckMargin = false;
        _menu.Padding = new Forms.Padding(6);
        _menu.DropShadowEnabled = true;
        _menuFont = new Drawing.Font("Segoe UI", 9F, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
        _menu.Font = _menuFont;

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
        SetTheme(darkTheme, highContrast);
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
        finally
        {
            // A shell click dismisses the balloon through NIN_BALLOONUSERCLICK; it does not
            // guarantee a separate BalloonTipClosed/NIN_BALLOONHIDE event.
            CompleteActiveReminderNotification();
        }
    }

    private void ReminderBalloonTipClosed(object? sender, EventArgs e) =>
        CompleteActiveReminderNotification();

    private void CompleteActiveReminderNotification()
    {
        if (_disposed)
            return;

        _reminderClickAction.Clear();
        if (_reminderNotificationQueue.CompleteActive())
            ShowNextReminderNotification();
    }

    public void SetLanguage(string language)
    {
        var ar = language == "ar";
        _menu.RightToLeft = ar ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
        _openItem.Text = ar ? "فتح ليال" : "Open Layal";
        _mascotItem.Text = ar ? "إظهار الشخصية" : "Show mascot";
        _exitItem.Text = ar ? "خروج" : "Exit";
    }

    /// <summary>Applies Layal's current theme to the Windows notification-area menu.</summary>
    public void SetTheme(bool dark, bool highContrast)
    {
        if (_disposed) return;

        var background = highContrast
            ? Drawing.SystemColors.Menu
            : dark ? Drawing.Color.FromArgb(31, 38, 56) : Drawing.Color.FromArgb(250, 252, 255);
        var foreground = highContrast
            ? Drawing.SystemColors.MenuText
            : dark ? Drawing.Color.FromArgb(237, 242, 255) : Drawing.Color.FromArgb(28, 41, 64);
        var border = highContrast
            ? Drawing.SystemColors.MenuText
            : dark ? Drawing.Color.FromArgb(73, 87, 112) : Drawing.Color.FromArgb(211, 223, 239);
        var selected = highContrast
            ? Drawing.SystemColors.Highlight
            : dark ? Drawing.Color.FromArgb(55, 72, 96) : Drawing.Color.FromArgb(226, 241, 247);

        _menu.BackColor = background;
        _menu.ForeColor = foreground;
        _menu.RenderMode = Forms.ToolStripRenderMode.Professional;
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(
            new LayalTrayMenuColorTable(background, border, selected));
        foreach (Forms.ToolStripItem item in _menu.Items)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
            item.Font = _menuFont;
            item.Padding = new Forms.Padding(10, 7, 10, 7);
        }
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
        _menuFont.Dispose();
        _ownedIcon?.Dispose();
    }
}

internal sealed class LayalTrayMenuColorTable : Forms.ProfessionalColorTable
{
    private readonly Drawing.Color _background;
    private readonly Drawing.Color _border;
    private readonly Drawing.Color _selected;

    public LayalTrayMenuColorTable(Drawing.Color background, Drawing.Color border, Drawing.Color selected)
    {
        _background = background;
        _border = border;
        _selected = selected;
        UseSystemColors = false;
    }

    public override Drawing.Color ToolStripDropDownBackground => _background;
    public override Drawing.Color ImageMarginGradientBegin => _background;
    public override Drawing.Color ImageMarginGradientMiddle => _background;
    public override Drawing.Color ImageMarginGradientEnd => _background;
    public override Drawing.Color MenuBorder => _border;
    public override Drawing.Color ToolStripBorder => _border;
    public override Drawing.Color MenuItemSelected => _selected;
    public override Drawing.Color MenuItemBorder => _border;
    public override Drawing.Color MenuItemSelectedGradientBegin => _selected;
    public override Drawing.Color MenuItemSelectedGradientEnd => _selected;
    public override Drawing.Color MenuItemPressedGradientBegin => _selected;
    public override Drawing.Color MenuItemPressedGradientMiddle => _selected;
    public override Drawing.Color MenuItemPressedGradientEnd => _selected;
    public override Drawing.Color SeparatorDark => _border;
    public override Drawing.Color SeparatorLight => _border;
}
