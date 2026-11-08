using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Layal.Services;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Layal.Views;

/// <summary>
/// Applies Layal's shared glass surface and the best available Windows backdrop to auxiliary windows.
/// On unsupported systems and in High Contrast, it falls back to the solid accessible panel surface.
/// </summary>
internal static class LayalWindowChrome
{
    private static readonly HashSet<Window> ManagedWindows = new();

    public static void ApplyGlass(Window window, bool isArabic)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.FlowDirection = isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ManagedWindows.Add(window);
        ApplyBackdropBrush(window);

        window.SourceInitialized += (_, _) => ApplyBackdropBrush(window);
        UserPreferenceChangedEventHandler preferenceChanged = (_, _) =>
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
                return;

            try
            {
                window.Dispatcher.BeginInvoke(new Action(RefreshCurrent));
            }
            catch (InvalidOperationException)
            {
                // The window is already closing; no further visual work is required.
            }
        };
        SystemEvents.UserPreferenceChanged += preferenceChanged;
        window.Closed += (_, _) =>
        {
            ManagedWindows.Remove(window);
            SystemEvents.UserPreferenceChanged -= preferenceChanged;
        };
    }

    /// <summary>Refreshes every currently open auxiliary window after an app or OS theme change.</summary>
    public static void RefreshAll(bool dark, bool highContrast)
    {
        foreach (var window in ManagedWindows.ToArray())
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
                continue;

            void Refresh() => ApplyBackdropBrush(window, dark, highContrast);
            if (window.Dispatcher.CheckAccess())
                Refresh();
            else
            {
                try { window.Dispatcher.BeginInvoke(new Action(Refresh)); }
                catch (InvalidOperationException) { /* the window is already closing */ }
            }
        }
    }

    private static void RefreshCurrent() =>
        RefreshAll(
            string.Equals(AppRuntime.Settings.ThemeMode, "dark", StringComparison.OrdinalIgnoreCase),
            SystemParameters.HighContrast);

    private static void ApplyBackdropBrush(Window window)
    {
        ApplyBackdropBrush(
            window,
            string.Equals(AppRuntime.Settings.ThemeMode, "dark", StringComparison.OrdinalIgnoreCase),
            SystemParameters.HighContrast);
    }

    private static void ApplyBackdropBrush(Window window, bool dark, bool highContrast)
    {
        if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
            return;

        var handle = new WindowInteropHelper(window).Handle;
        var backdropApplied = handle != IntPtr.Zero &&
            WindowBackdropService.TryApply(handle, dark, highContrast);

        window.SetResourceReference(
            Window.BackgroundProperty,
            backdropApplied && !highContrast ? "Layal.GlassFill" : "Layal.Panel");
    }
}
