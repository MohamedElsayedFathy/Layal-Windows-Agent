using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Layal.Services;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GameModeHudWindowTests
{
    [TestMethod]
    public void HudIsTopmostAndDoesNotActivateWhenShown()
    {
        RunOnSta(() =>
        {
            var hud = new GameModeHudWindow(false, () => "Layal is ready.");
            try
            {
                Assert.IsTrue(hud.Topmost);
                Assert.IsFalse(hud.ShowActivated);
                Assert.IsFalse(hud.ShowInTaskbar);
                Assert.IsNull(hud.Owner, "The HUD must not be owned by the main window so minimizing Layal does not hide it.");
                Assert.AreEqual(WindowStyle.ToolWindow, hud.WindowStyle);
                hud.Show();
                Assert.IsTrue(hud.IsVisible);
                var handle = new WindowInteropHelper(hud).Handle;
                var extendedStyle = GetWindowLongPtr(handle, -20).ToInt64();
                Assert.AreNotEqual(0L, extendedStyle & 0x08000000L,
                    "The HUD must use WS_EX_NOACTIVATE so showing or clicking it does not take focus from a game.");
            }
            finally
            {
                hud.CloseFromDetection();
            }
        });
    }

    [TestMethod]
    public void UserDismissalAndProgrammaticDetectionCloseAreDistinct()
    {
        RunOnSta(() =>
        {
            var manuallyDismissed = new GameModeHudWindow(false, () => string.Empty);
            manuallyDismissed.Show();
            Assert.IsTrue(manuallyDismissed.IsVisible, "The test HUD must be visible before user dismissal.");
            manuallyDismissed.CloseByUser();
            Assert.IsTrue(manuallyDismissed.WasDismissedByUser);

            var closedByDetection = new GameModeHudWindow(false, () => string.Empty, automaticallyLaunched: true);
            closedByDetection.Show();
            closedByDetection.CloseFromDetection();
            Assert.IsFalse(closedByDetection.WasDismissedByUser);
        });
    }


    [TestMethod]
    public void HudDisplaysTheConfiguredQuickAskShortcutInsteadOfAlwaysUsingTheDefault()
    {
        RunOnSta(() =>
        {
            var hud = new GameModeHudWindow(
                false,
                () => "Layal is ready.",
                shortcutDisplay: GlobalHotkeyService.GetDisplayName("CtrlAltShiftSpace"));
            try
            {
                Assert.AreEqual("Ctrl+Alt+Shift+Space", hud.ShortcutDisplay);
                hud.UpdateShortcutDisplay(GlobalHotkeyService.GetDisplayName("AltShiftQ"));
                Assert.AreEqual("Alt+Shift+Q", hud.ShortcutDisplay,
                    "The HUD must update its shortcut text when the user changes the Quick Ask setting while the HUD remains open.");
                hud.Show();
                Assert.IsTrue(hud.IsVisible);
            }
            finally
            {
                hud.CloseFromDetection();
            }
        });
    }

    [TestMethod]
    public void WindowChromeCloseCountsAsUserDismissalAndExitEventIsRaisedOnce()
    {
        RunOnSta(() =>
        {
            var exitCount = 0;
            var hud = new GameModeHudWindow(false, () => string.Empty);
            hud.ExitRequested += (_, _) => exitCount++;
            hud.Show();
            hud.Close();
            Assert.IsTrue(hud.WasDismissedByUser);
            Assert.AreEqual(1, exitCount);
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The Game HUD WPF test timed out.");
        if (failure is not null)
            Assert.Fail("Game HUD WPF test failed: " + failure);
    }
}
