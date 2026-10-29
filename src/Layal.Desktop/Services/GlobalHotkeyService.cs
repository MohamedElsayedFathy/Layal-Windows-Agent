using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Layal.Services;

/// <summary>
/// Registers the non-invasive Alt+Shift+S Quick Ask shortcut for the lifetime of the main window.
/// If another application owns the combination, registration simply fails and the in-app button remains available.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4C59;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyS = 0x53;

    private readonly Window _window;
    private readonly Action _onHotkey;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private bool _disposed;

    public bool IsRegistered => _registered;

    public GlobalHotkeyService(Window window, Action onHotkey)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _onHotkey = onHotkey ?? throw new ArgumentNullException(nameof(onHotkey));
        _window.SourceInitialized += Window_SourceInitialized;
        _window.Closed += Window_Closed;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (_disposed || _source is not null)
            return;

        _handle = new WindowInteropHelper(_window).Handle;
        if (_handle == IntPtr.Zero)
            return;

        _source = HwndSource.FromHwnd(_handle);
        if (_source is null)
            return;

        _source.AddHook(WindowHook);
        _registered = RegisterHotKey(_handle, HotkeyId, ModAlt | ModShift | ModNoRepeat, VirtualKeyS);
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (!_disposed)
                _onHotkey();
        }

        return IntPtr.Zero;
    }

    private void Window_Closed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.SourceInitialized -= Window_SourceInitialized;
        _window.Closed -= Window_Closed;

        if (_source is not null)
        {
            if (_registered && _handle != IntPtr.Zero)
                UnregisterHotKey(_handle, HotkeyId);
            _registered = false;
            _source.RemoveHook(WindowHook);
            _source = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
