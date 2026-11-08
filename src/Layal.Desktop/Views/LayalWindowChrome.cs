using System;
using System.Windows;
using System.Windows.Interop;

namespace Layal.Views;

/// <summary>
/// Applies Layal's shared glass surface and the best available Windows backdrop to auxiliary windows.
/// On unsupported systems and in High Contrast, it falls back to the solid accessible panel surface.
/// </summary>
internal static class LayalWindowChrome
{
    public static void ApplyGlass(Window window, bool isArabic)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.FlowDirection = isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        window.SetResourceReference(Window.BackgroundProperty, "Layal.GlassFill");
        window.SetResourceReference(Window.ForegroundProperty, "Layal.Text");
        window.SourceInitialized += (_, _) =>
        {
            var highContrast = SystemParameters.HighContrast;
            var dark = string.Equals(AppRuntime.Settings.ThemeMode, "dark", StringComparison.OrdinalIgnoreCase);
            var handle = new WindowInteropHelper(window).Handle;
            var backdropApplied = WindowBackdropService.TryApply(handle, dark, highContrast);
            if (!backdropApplied || highContrast)
                window.SetResourceReference(Window.BackgroundProperty, "Layal.Panel");
        };
    }
}
