using System;
using Layal;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CompleteSetupWindowAccessibilityTests
{
    [TestMethod]
    public void WizardExposesAccessibleNamesAndCanSwitchHighContrastWhileOpen()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunWizardAccessibilitySmoke();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // The test never constructs an Application or runs app startup.
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(1)))
            Assert.Fail("Complete Setup WPF accessibility smoke did not finish within one minute.");
        if (failure is not null)
            Assert.Fail("Complete Setup WPF accessibility smoke failed: " + failure);
    }

    private static void RunWizardAccessibilitySmoke()
    {
        using var temp = new TestTempDirectory();
        // Do not instantiate a second WPF Application in this test AppDomain.
        // The existing WPF visual smoke tests own the single Application singleton.
        var previousSettings = AppRuntime.Settings;
        var previousSettingsStore = AppRuntime.SettingsStore;
        var previousDataRoot = AppRuntime.DataRoot;
        CompleteSetupWindow? window = null;
        LocalVoiceService? voice = null;
        try
        {
            var profileRoot = Path.Combine(temp.Path, "profile");
            var resourceRoot = Path.Combine(temp.Path, "resources");
            Directory.CreateDirectory(profileRoot);
            AppRuntime.DataRoot = profileRoot;
            AppRuntime.SettingsStore = new SettingsStore(profileRoot);
            AppRuntime.Settings = new UserSettings
            {
                Language = "ar",
                LocalAiDataDirectory = resourceRoot,
                OllamaModelsDirectory = Path.Combine(resourceRoot, "ollama-models")
            };

            voice = new LocalVoiceService();
            window = new CompleteSetupWindow(profileRoot, voice)
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 20,
                Top = 20
            };
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.AreEqual(FlowDirection.RightToLeft, window.FlowDirection);
            var consent = GetField<CheckBox>(window, "_consent");
            var start = GetField<Button>(window, "_startButton");
            var limited = GetField<Button>(window, "_limitedModeButton");
            var close = GetField<Button>(window, "_cancelButton");
            var choose = GetField<Button>(window, "_chooseResourceDirectoryButton");
            var resourcePath = GetField<TextBlock>(window, "_resourceDirectoryText");
            var status = GetField<TextBlock>(window, "_statusText");
            var progress = GetField<ProgressBar>(window, "_progressBar");
            var diagnostics = GetField<TextBox>(window, "_diagnostics");

            Assert.IsFalse(consent.IsChecked == true,
                "The wizard must not treat consent as granted on first display.");
            Assert.IsFalse(start.IsEnabled,
                "Full setup must remain disabled until the user explicitly approves and connectivity preflight finishes.");
            AssertAccessibleName(consent, "consent");
            AssertAccessibleName(start, "start");
            AssertAccessibleName(limited, "limited mode");
            AssertAccessibleName(close, "close");
            AssertAccessibleName(choose, "resource folder selection");
            AssertAccessibleName(resourcePath, "resource folder path");
            AssertAccessibleName(status, "live setup status");
            AssertAccessibleName(progress, "setup progress");
            AssertAccessibleName(diagnostics, "diagnostics");

            var applyTheme = typeof(CompleteSetupWindow).GetMethod(
                "ApplySystemTheme",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(bool) },
                modifiers: null)
                ?? throw new InvalidOperationException("The live High Contrast theme application method was not found.");
            var rootBorder = GetField<Border>(window, "_rootBorder");

            applyTheme.Invoke(window, new object[] { true });
            Assert.AreSame(SystemColors.WindowBrush, window.Background);
            Assert.AreSame(SystemColors.WindowBrush, rootBorder.Background);
            Assert.AreSame(SystemColors.WindowTextBrush, rootBorder.BorderBrush);
            Assert.AreSame(SystemColors.WindowTextBrush, status.Foreground);
            Assert.AreSame(SystemColors.WindowBrush, diagnostics.Background);
            Assert.AreSame(SystemColors.WindowTextBrush, diagnostics.Foreground);

            applyTheme.Invoke(window, new object[] { false });
            Assert.AreNotSame(SystemColors.WindowBrush, window.Background);
            Assert.AreNotSame(SystemColors.WindowBrush, rootBorder.Background);

            window.Close();
            window = null;

            AppRuntime.Settings.Language = "en";
            window = new CompleteSetupWindow(profileRoot, voice)
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 20,
                Top = 20
            };
            window.Show();
            window.UpdateLayout();
            Assert.AreEqual(FlowDirection.LeftToRight, window.FlowDirection);
            AssertAccessibleName(GetField<Button>(window, "_chooseResourceDirectoryButton"), "English resource folder selection");
            AssertAccessibleName(GetField<CheckBox>(window, "_consent"), "English consent");
        }
        finally
        {
            try { window?.Close(); } catch { }
            try { voice?.Dispose(); } catch { }
            AppRuntime.Settings = previousSettings;
            AppRuntime.SettingsStore = previousSettingsStore;
            AppRuntime.DataRoot = previousDataRoot;
        }
    }

    private static T GetField<T>(object target, string name) where T : class =>
        typeof(CompleteSetupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) as T
        ?? throw new InvalidOperationException("The Complete Setup field was not found: " + name);

    private static void AssertAccessibleName(DependencyObject element, string description)
    {
        var name = AutomationProperties.GetName(element);
        Assert.IsFalse(string.IsNullOrWhiteSpace(name),
            "The Complete Setup " + description + " must expose an explicit accessible name.");
    }
}
