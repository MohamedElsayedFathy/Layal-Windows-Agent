using System;
using System.Runtime.InteropServices;

namespace Layal.Services;

/// <summary>
/// Configures native frame details while keeping the main window surface fully app-owned.
/// System backdrops are deliberately disabled so desktop composition cannot leak through
/// the light or dark WPF client area.
/// </summary>
public static class WindowBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmSystemBackdropTypeNone = 1;
    private const int DwmWindowCornerPreferenceRound = 2;

    /// <summary>
    /// Disables the native system backdrop and applies theme-aware frame details.
    /// Returns false for invalid handles, unsupported Windows builds, or a DWM rejection.
    /// The application-owned WPF surface remains the visible background on every platform.
    /// </summary>
    public static bool TryApply(IntPtr windowHandle, bool dark, bool highContrast)
    {
        if (windowHandle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        try
        {
            var darkMode = dark && !highContrast ? 1 : 0;
            var backdropType = DwmSystemBackdropTypeNone;
            var cornerPreference = DwmWindowCornerPreferenceRound;

            var darkResult = DwmSetWindowAttribute(
                windowHandle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            var backdropResult = DwmSetWindowAttribute(
                windowHandle, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            var cornerResult = DwmSetWindowAttribute(
                windowHandle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            return darkResult >= 0 && backdropResult >= 0 && cornerResult >= 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
