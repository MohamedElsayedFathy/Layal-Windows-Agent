using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Layal.Services;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GlobalHotkeyServiceTests
{
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyUp = 0x0002;
    private const int ConflictHotkeyId = 0x4C58;

    [TestMethod]
    public void SupportedHotkeyPresetsRemainStableAndReadable()
    {
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset("AltShiftS"));
        Assert.AreEqual("AltShiftQ", GlobalHotkeyService.NormalizePreset("AltShiftQ"));
        Assert.AreEqual("CtrlAltShiftSpace", GlobalHotkeyService.NormalizePreset("CtrlAltShiftSpace"));

        Assert.AreEqual("Alt+Shift+S", GlobalHotkeyService.GetDisplayName("AltShiftS"));
        Assert.AreEqual("Alt+Shift+Q", GlobalHotkeyService.GetDisplayName("AltShiftQ"));
        Assert.AreEqual("Ctrl+Alt+Shift+Space", GlobalHotkeyService.GetDisplayName("CtrlAltShiftSpace"));
    }

    [TestMethod]
    public void UnknownOrMalformedHotkeyPresetFallsBackToDefault()
    {
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset(null));
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset(string.Empty));
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset("CtrlN"));
        Assert.AreEqual("Alt+Shift+S", GlobalHotkeyService.GetDisplayName("unexpected"));
    }

    [TestMethod]
    public void GlobalShortcutOpensAndFocusesQuickAskFromAnotherForegroundWindow()
    {
        RunOnSta(() =>
        {
            var layalWindow = new Window { Title = "Layal hotkey owner", Width = 320, Height = 180 };
            var externalWindow = new Window { Title = "External application", Width = 320, Height = 180, Topmost = true };
            QuickAskWindow? quickAskWindow = null;
            GlobalHotkeyService? hotkeyService = null;
            var triggerCount = 0;

            try
            {
                layalWindow.Show();
                externalWindow.Show();
                hotkeyService = new GlobalHotkeyService(layalWindow, () =>
                {
                    triggerCount++;
                    if (quickAskWindow is null || !quickAskWindow.IsVisible)
                    {
                        quickAskWindow = new QuickAskWindow(
                            isArabic: false,
                            askAsync: _ => Task.FromResult(QuickAskResult.Failure("Integration test only.")),
                            cancelRequest: () => { })
                        {
                            Owner = layalWindow
                        };
                        quickAskWindow.Show();
                    }

                    quickAskWindow.Activate();
                    quickAskWindow.FocusInput();
                }, "CtrlAltShiftSpace");

                Assert.IsTrue(hotkeyService.IsRegistered,
                    "The selected global shortcut must register against the Layal window handle.");

                externalWindow.Activate();
                externalWindow.Focus();
                SendHotkey("CtrlAltShiftSpace");

                WaitForCondition(
                    () => quickAskWindow is { IsVisible: true } && triggerCount == 1,
                    TimeSpan.FromSeconds(3));

                var openedQuickAsk = quickAskWindow
                    ?? throw new InvalidOperationException("The global shortcut did not create the Quick Ask window.");
                Assert.IsTrue(openedQuickAsk.IsActive,
                    "A global shortcut pressed while another app is foreground must activate Quick Ask.");
                var questionInput = (TextBox?)openedQuickAsk.FindName("QuestionInput")
                    ?? throw new InvalidOperationException("Quick Ask question input was not found.");
                Assert.IsTrue(questionInput.IsKeyboardFocused,
                    "The global shortcut must place keyboard focus directly in the Quick Ask input.");
            }
            finally
            {
                hotkeyService?.Dispose();
                quickAskWindow?.Close();
                externalWindow.Close();
                layalWindow.Close();
            }
        });
    }

    [TestMethod]
    public void ChangingShortcutReleasesThePreviousCombinationImmediately()
    {
        RunOnSta(() =>
        {
            var layalWindow = new Window { Title = "Layal hotkey owner", Width = 320, Height = 180 };
            var externalWindow = new Window { Title = "External application", Width = 320, Height = 180, Topmost = true };
            GlobalHotkeyService? previousService = null;
            GlobalHotkeyService? replacementService = null;
            var previousInvocations = 0;
            var replacementInvocations = 0;

            try
            {
                layalWindow.Show();
                externalWindow.Show();
                previousService = new GlobalHotkeyService(
                    layalWindow, () => previousInvocations++, "AltShiftQ");
                Assert.IsTrue(previousService.IsRegistered);

                externalWindow.Activate();
                SendHotkey("AltShiftQ");
                WaitForCondition(() => previousInvocations == 1, TimeSpan.FromSeconds(3));

                previousService.Dispose();
                replacementService = new GlobalHotkeyService(
                    layalWindow, () => replacementInvocations++, "AltShiftS");
                Assert.IsTrue(replacementService.IsRegistered,
                    "A replacement shortcut must register immediately without restarting Layal.");

                externalWindow.Activate();
                SendHotkey("AltShiftQ");
                PumpDispatcherFor(TimeSpan.FromMilliseconds(300));
                Assert.AreEqual(0, replacementInvocations,
                    "The previous shortcut must stop invoking the replacement callback as soon as it is changed.");

                SendHotkey("AltShiftS");
                WaitForCondition(() => replacementInvocations == 1, TimeSpan.FromSeconds(3));
            }
            finally
            {
                replacementService?.Dispose();
                previousService?.Dispose();
                externalWindow.Close();
                layalWindow.Close();
            }
        });
    }

    [TestMethod]
    public void ConflictingGlobalShortcutFailsSafelyAndRegistersAfterTheConflictIsReleased()
    {
        RunOnSta(() =>
        {
            var layalWindow = new Window { Title = "Layal hotkey owner", Width = 320, Height = 180 };
            var reservingWindow = new Window { Title = "Shortcut owner", Width = 320, Height = 180 };
            GlobalHotkeyService? blockedService = null;
            GlobalHotkeyService? recoveredService = null;
            var reserved = false;

            try
            {
                layalWindow.Show();
                reservingWindow.Show();
                var reservingHandle = new WindowInteropHelper(reservingWindow).Handle;
                reserved = RegisterHotKey(
                    reservingHandle,
                    ConflictHotkeyId,
                    ModAlt | ModShift | ModNoRepeat,
                    (uint)'Q');
                Assert.IsTrue(reserved,
                    $"The test process could not reserve the conflict shortcut (Win32 error {Marshal.GetLastWin32Error()}).");

                blockedService = new GlobalHotkeyService(layalWindow, () => { }, "AltShiftQ");
                Assert.IsFalse(blockedService.IsRegistered,
                    "Layal must report the shortcut as unavailable when another application already owns it.");

                Assert.IsTrue(UnregisterHotKey(reservingHandle, ConflictHotkeyId),
                    $"The test process could not release its shortcut reservation (Win32 error {Marshal.GetLastWin32Error()}).");
                reserved = false;
                blockedService.Dispose();

                recoveredService = new GlobalHotkeyService(layalWindow, () => { }, "AltShiftQ");
                Assert.IsTrue(recoveredService.IsRegistered,
                    "The shortcut must become available after its previous owner releases it and Layal retries registration.");
            }
            finally
            {
                recoveredService?.Dispose();
                blockedService?.Dispose();
                if (reserved)
                {
                    var handle = new WindowInteropHelper(reservingWindow).Handle;
                    UnregisterHotKey(handle, ConflictHotkeyId);
                }
                reservingWindow.Close();
                layalWindow.Close();
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        })
        {
            IsBackground = true,
            Name = "Layal global hotkey acceptance"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)),
            "The Windows global hotkey acceptance test timed out.");
        if (failure is not null)
            Assert.Fail("Global hotkey acceptance failed: " + failure);
    }

    private static void WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        if (condition())
            return;

        var deadline = DateTime.UtcNow + timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert.IsTrue(condition(), "The expected global hotkey action did not arrive before the timeout.");
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SendHotkey(string preset)
    {
        var modifiers = preset switch
        {
            "CtrlAltShiftSpace" => new byte[] { 0x11, 0x12, 0x10 },
            "AltShiftQ" or "AltShiftS" => new byte[] { 0x12, 0x10 },
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unsupported test shortcut.")
        };
        var key = preset switch
        {
            "CtrlAltShiftSpace" => (byte)0x20,
            "AltShiftQ" => (byte)0x51,
            "AltShiftS" => (byte)0x53,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unsupported test shortcut.")
        };

        foreach (var modifier in modifiers)
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        for (var index = modifiers.Length - 1; index >= 0; index--)
            keybd_event(modifiers[index], 0, KeyUp, UIntPtr.Zero);
        Thread.Sleep(60);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
