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
        SetLanguage(language);
    }

    public void ShowReminderNotification(string reminderTitle, bool isArabic)
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

        _icon.ShowBalloonTip(
            6000,
            isArabic ? "حان موعد التذكير" : "Reminder is due",
            safeTitle,
            Forms.ToolTipIcon.Info);
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
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
