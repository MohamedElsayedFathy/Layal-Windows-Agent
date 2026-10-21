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
        var iconResource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/layal-tray.ico", UriKind.Absolute));
        if (iconResource is not null)
        {
            using (iconResource.Stream) _ownedIcon = new Drawing.Icon(iconResource.Stream);
            trayIcon = _ownedIcon!;
        }
        else
        {
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
