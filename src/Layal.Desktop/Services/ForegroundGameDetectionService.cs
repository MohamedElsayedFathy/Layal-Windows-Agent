using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Layal.Services;

/// <summary>
/// Reads only the process name associated with the current foreground window, and only
/// when the caller has explicitly enabled an allowlist. It never enumerates processes,
/// reads window titles/command lines/memory, installs hooks, or injects input.
/// </summary>
public sealed class ForegroundGameDetectionService
{
    public string? GetForegroundProcessName()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return null;

            _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
            if (processId == 0 || processId == (uint)Environment.ProcessId)
                return null;

            using var process = Process.GetProcessById(checked((int)processId));
            if (process.HasExited)
                return null;

            return GameModeProcessAllowlist.TryNormalizeName(process.ProcessName, out var normalized)
                ? normalized
                : null;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or
            NotSupportedException or OverflowException)
        {
            // The foreground window can disappear or its process can exit between the Win32 calls.
            return null;
        }
    }

    public bool IsAllowedForeground(System.Collections.Generic.IReadOnlyCollection<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        if (allowlist.Count == 0)
            return false;

        var processName = GetForegroundProcessName();
        return processName is not null && allowlist.Contains(processName, StringComparer.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
