using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Layal.Services;

public sealed record WindowTitleInfo(string Title, uint ProcessId);

public static class WindowInventory
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static IReadOnlyList<WindowTitleInfo> GetVisibleWindowTitles()
    {
        var results = new List<WindowTitleInfo>();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var length = GetWindowTextLength(hWnd);
            if (length <= 0 || length > 500) return true;
            var builder = new StringBuilder(length + 1);
            _ = GetWindowText(hWnd, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hWnd, out var processId);
            results.Add(new WindowTitleInfo(title, processId));
            return results.Count < 100;
        }, IntPtr.Zero);
        return results;
    }

    public static bool IsSameWindowTarget(UiWindowTarget approved, UiWindowTarget current) =>
        approved is not null && current is not null &&
        approved.Handle != IntPtr.Zero && approved.Handle == current.Handle &&
        approved.ProcessId != 0 && approved.ProcessId == current.ProcessId &&
        string.Equals(approved.Title, current.Title, StringComparison.Ordinal);

    public static bool ActivateWindowTarget(UiWindowTarget approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (!IsTargetStillSameWindow(approved))
            return false;
        _ = ShowWindow(approved.Handle, 9); // SW_RESTORE
        return IsTargetStillSameWindow(approved) && SetForegroundWindow(approved.Handle);
    }

    private static bool IsTargetStillSameWindow(UiWindowTarget approved)
    {
        if (approved.Handle == IntPtr.Zero || !IsWindow(approved.Handle) || !IsWindowVisible(approved.Handle))
            return false;
        GetWindowThreadProcessId(approved.Handle, out var processId);
        if (processId == 0 || processId != approved.ProcessId)
            return false;
        var length = GetWindowTextLength(approved.Handle);
        if (length <= 0 || length > 500)
            return false;
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(approved.Handle, builder, builder.Capacity);
        return string.Equals(builder.ToString().Trim(), approved.Title, StringComparison.Ordinal);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int command);
}
