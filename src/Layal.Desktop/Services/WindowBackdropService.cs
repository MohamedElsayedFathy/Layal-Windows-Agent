using System;
using System.Runtime.InteropServices;

namespace Layal.Services;

/// <summary>
/// Requests the Windows 11 system backdrop for the main WPF window.
/// Failure is expected on unsupported Windows builds and is handled by the app's translucent
/// WPF wash and glass surfaces, so this service never prevents the app from opening.
/// </summary>
public static class WindowBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmSystemBackdropTypeNone = 1;
    private const int DwmSystemBackdropTypeTransientWindow = 3;
    private const int DwmWindowCornerPreferenceRound = 2;

    /// <summary>
    /// Applies Windows' native system backdrop when the OS supports it.
    /// Returns false for invalid handles, unsupported Windows builds, or a DWM rejection.
    /// </summary>
    public static bool TryApply(IntPtr windowHandle, bool dark, bool highContrast)
    {
        if (windowHandle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        try
        {
            var darkMode = dark ? 1 : 0;
            var backdropType = highContrast
                ? DwmSystemBackdropTypeNone
                : DwmSystemBackdropTypeTransientWindow;
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
