using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Layal.Services;

/// <summary>
/// Registers one of a small set of user-selectable Quick Ask shortcuts. The in-app Quick Ask button
/// remains available if Windows reports that another application owns the selected combination.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4C59;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyS = 0x53;
    private const uint VirtualKeyQ = 0x51;
    private const uint VirtualKeySpace = 0x20;

    private readonly Window _window;
    private readonly Action _onHotkey;
    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private bool _disposed;

    public bool IsRegistered => _registered;
    public string Preset { get; }

    public GlobalHotkeyService(Window window, Action onHotkey, string? preset = "AltShiftS")
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _onHotkey = onHotkey ?? throw new ArgumentNullException(nameof(onHotkey));
        Preset = NormalizePreset(preset);
        (_modifiers, _virtualKey) = Resolve(Preset);
        _modifiers |= ModNoRepeat;

        _window.SourceInitialized += Window_SourceInitialized;
        _window.Closed += Window_Closed;

        // A user can change the shortcut after the window handle already exists.
        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
            Window_SourceInitialized(_window, EventArgs.Empty);
    }

    public static string NormalizePreset(string? preset) =>
        preset is "AltShiftS" or "AltShiftQ" or "CtrlAltShiftSpace" ? preset : "AltShiftS";

    public static string GetDisplayName(string? preset) => NormalizePreset(preset) switch
    {
        "AltShiftQ" => "Alt+Shift+Q",
        "CtrlAltShiftSpace" => "Ctrl+Alt+Shift+Space",
        _ => "Alt+Shift+S"
    };

    private static (uint Modifiers, uint VirtualKey) Resolve(string preset) => preset switch
    {
        "AltShiftQ" => (ModAlt | ModShift, VirtualKeyQ),
        "CtrlAltShiftSpace" => (ModControl | ModAlt | ModShift, VirtualKeySpace),
        _ => (ModAlt | ModShift, VirtualKeyS)
    };

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
        _registered = RegisterHotKey(_handle, HotkeyId, _modifiers, _virtualKey);
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
