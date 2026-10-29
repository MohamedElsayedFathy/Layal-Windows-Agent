using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

/// <summary>
/// Produces real WPF render snapshots for each main tab/theme/language on the Windows CI runner.
/// These are render smoke checks, not a substitute for testing physical monitor DPI and input.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MainWindowVisualSmokeTests
{
    [TestMethod]
    public void AllMainSectionsRenderInBothThemesAndLanguages()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RenderAllStates(); }
            catch (Exception exception) { failure = exception; }
            finally { App.SuppressStartupForTests = false; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(3)))
            Assert.Fail("WPF visual smoke thread did not finish within three minutes.");
        if (failure is not null)
            Assert.Fail("WPF visual smoke failed: " + failure);
    }

    private static void AssertEscapeCancelsActiveChatAndAgentOperations(MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var chatField = typeof(MainWindow).GetField("_chatCancellation", flags)
            ?? throw new InvalidOperationException("Chat cancellation field was not found.");
        var taskField = typeof(MainWindow).GetField("_agentTaskCancellation", flags)
            ?? throw new InvalidOperationException("Agent task cancellation field was not found.");
        var keyHandler = typeof(MainWindow).GetMethod("MainWindow_PreviewKeyDown", flags)
            ?? throw new InvalidOperationException("Main window keyboard handler was not found.");

        if (chatField.GetValue(window) is not null || taskField.GetValue(window) is not null)
            throw new InvalidOperationException("The Escape regression test requires an idle chat and task runner.");

        using var chatCancellation = new CancellationTokenSource();
        using var taskCancellation = new CancellationTokenSource();
        try
        {
            chatField.SetValue(window, chatCancellation);
            var chatEscape = CreateEscapeKeyEvent(window);
            keyHandler.Invoke(window, new object[] { window, chatEscape });
            Assert.IsTrue(chatCancellation.IsCancellationRequested, "Escape should cancel an active chat request.");
            Assert.IsTrue(chatEscape.Handled, "Escape should be handled when it cancels an operation.");

            chatField.SetValue(window, null);
            taskField.SetValue(window, taskCancellation);
            var taskEscape = CreateEscapeKeyEvent(window);
            keyHandler.Invoke(window, new object[] { window, taskEscape });
            Assert.IsTrue(taskCancellation.IsCancellationRequested, "Escape should cancel an active agent task.");
            Assert.IsTrue(taskEscape.Handled, "Escape should be handled when it cancels an agent task.");
        }
        finally
        {
            chatField.SetValue(window, null);
            taskField.SetValue(window, null);
        }
    }

    private static KeyEventArgs CreateEscapeKeyEvent(MainWindow window)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("The main window has no presentation source.");
        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
    }

    private static void AssertPrivateChatDoesNotPersistMessagesAndRestoresSavedConversation(MainWindow window)
    {
        AppRuntime.ChatSessionService.SaveCurrentMessages(new[]
        {
            new ChatMessage { Role = "user", Content = "Saved conversation sentinel" },
            new ChatMessage { Role = "assistant", Content = "Saved reply sentinel" }
        });

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var enterPrivate = typeof(MainWindow).GetMethod("EnterPrivateChatMode", flags)
            ?? throw new InvalidOperationException("Private-chat entry method was not found.");
        var addMessage = typeof(MainWindow).GetMethod("AddMessage", flags)
            ?? throw new InvalidOperationException("Chat message method was not found.");
        var exitPrivate = typeof(MainWindow).GetMethod("ExitPrivateChatMode", flags)
            ?? throw new InvalidOperationException("Private-chat exit method was not found.");

        enterPrivate.Invoke(window, null);
        Assert.AreEqual(0, window.ChatMessages.Count, "Private chat must start without the previous transcript.");
        Assert.AreEqual(0, window.ChatConversations.Count, "Saved conversations must be hidden in private chat.");
        var historySelector = (ComboBox?)window.FindName("ChatHistorySelector")
            ?? throw new InvalidOperationException("Chat history selector was not found.");
        Assert.IsFalse(historySelector.IsEnabled, "Saved history must not be selectable in private chat.");

        addMessage.Invoke(window, new object[] { "user", "Private-only sentinel" });
        Assert.AreEqual(1, window.ChatMessages.Count);
        var storedWhilePrivate = AppRuntime.ChatSessionService.GetCurrent();
        Assert.AreEqual(2, storedWhilePrivate.Messages.Count, "Private messages must not change persisted history.");
        Assert.AreEqual("Saved conversation sentinel", storedWhilePrivate.Messages[0].Content);
        Assert.AreEqual("Saved reply sentinel", storedWhilePrivate.Messages[1].Content);

        exitPrivate.Invoke(window, null);
        Assert.IsTrue(historySelector.IsEnabled, "Saved history selector should return after private chat.");
        Assert.AreEqual(2, window.ChatMessages.Count, "Exiting private chat should restore the saved transcript.");
        Assert.AreEqual("Saved conversation sentinel", window.ChatMessages[0].Content);
        Assert.AreEqual("Saved reply sentinel", window.ChatMessages[1].Content);
        Assert.AreEqual(1, window.ChatConversations.Count);
        Assert.AreEqual(2, AppRuntime.ChatSessionService.GetCurrent().Messages.Count);
    }

    private static void RenderAllStates()
    {
        using var temp = new TestTempDirectory();
        App.SuppressStartupForTests = true;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        InitializeRuntime(temp.Path);
        // Rendering tests must not request real device-location permission or network weather.
        AppRuntime.Settings.UseAutomaticLocation = false;

        var outputRoot = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (string.IsNullOrWhiteSpace(outputRoot))
            outputRoot = Directory.GetCurrentDirectory();
        var outputDirectory = Path.Combine(outputRoot, "artifacts", "visual-smoke");
        Directory.CreateDirectory(outputDirectory);

        var window = new MainWindow
        {
            Width = 1320,
            Height = 860,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false
        };
        AppRuntime.MainWindow = window;
        window.Show();
        window.UpdateLayout();
        AssertInitialWindowFitsWorkArea(window);
        AssertClockWeatherDashboard(window);
        var windowContent = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("MainWindow content root was not found.");

        var tabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        Assert.AreSame(window.FindName("ChatTab"), tabs.SelectedItem,
            "The primary window must open directly into the glass chat view, not the legacy home dashboard.");
        foreach (var visualSurface in new[] { "AmbientGlassLayer", "ChatMessagesShell", "ChatStatusShell", "ChatComposerShell", "SidebarOverlay", "SidebarBrandRow", "SidebarFooterPanel" })
            Assert.IsNotNull(window.FindName(visualSurface),
                $"The updated layered-glass chat surface '{visualSurface}' must be present in the shipped UI.");
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyTheme was not found.");
        var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyLanguage was not found.");

        var tabNames = new[] { "home", "chat", "tasks", "skills", "cameras", "memory", "api", "settings" };
        Assert.AreEqual(tabNames.Length, tabs.Items.Count,
            "The visual audit must render every expected main section; the test must not silently skip a missing tab.");

        var expectedTabs = new[]
        {
            window.FindName("HomeTab") as TabItem,
            window.FindName("ChatTab") as TabItem,
            window.FindName("TasksTab") as TabItem,
            window.FindName("SkillsTab") as TabItem,
            window.FindName("CamerasTab") as TabItem,
            window.FindName("MemoryTab") as TabItem,
            window.FindName("ApiIntegrationsTab") as TabItem,
            window.FindName("SettingsTab") as TabItem
        };
        Assert.AreEqual(tabNames.Length, expectedTabs.Length);
        for (var i = 0; i < expectedTabs.Length; i++)
        {
            Assert.IsNotNull(expectedTabs[i], $"Expected named tab '{tabNames[i]}' was not found.");
            Assert.AreSame(expectedTabs[i], tabs.Items[i], $"Tab order mismatch at '{tabNames[i]}'.");
        }
        AssertAccessibleStatusRegions(window);

        var historySelector = window.FindName("ChatHistorySelector") as ComboBox
            ?? throw new InvalidOperationException("ChatHistorySelector was not found.");
        AssertThemeTogglePersistsLivePalette(window, historySelector);
        var recentTasksGroup = window.FindName("RecentTasksGroup") as GroupBox
            ?? throw new InvalidOperationException("RecentTasksGroup was not found.");
        var languageButton = window.FindName("LanguageButton") as Button
            ?? throw new InvalidOperationException("LanguageButton was not found.");
        var titleText = window.FindName("TitleText") as TextBlock
            ?? throw new InvalidOperationException("TitleText was not found.");
        var copyChatButton = window.FindName("CopyChatButton") as Button
            ?? throw new InvalidOperationException("CopyChatButton was not found.");
        var newConversationButton = window.FindName("NewConversationButton") as Button
            ?? throw new InvalidOperationException("NewConversationButton was not found.");
        var clearHistoryButton = window.FindName("ClearSavedChatHistoryButton") as Button
            ?? throw new InvalidOperationException("ClearSavedChatHistoryButton was not found.");
        var sendButton = window.FindName("SendButton") as Button
            ?? throw new InvalidOperationException("SendButton was not found.");
        var runTaskButton = window.FindName("RunAgentTaskButton") as Button
            ?? throw new InvalidOperationException("RunAgentTaskButton was not found.");
        var baseUrlInput = window.FindName("BaseUrlInput") as TextBox
            ?? throw new InvalidOperationException("BaseUrlInput was not found.");
        var modelInput = window.FindName("ModelInput") as TextBox
            ?? throw new InvalidOperationException("ModelInput was not found.");
        var apiKeyInput = window.FindName("ApiKeyInput") as PasswordBox
            ?? throw new InvalidOperationException("ApiKeyInput was not found.");
        var githubSkillUrlInput = window.FindName("GitHubSkillUrlInput") as TextBox
            ?? throw new InvalidOperationException("GitHubSkillUrlInput was not found.");

        AssertKeyboardAccessibility(window, sendButton, window.FindName("ChatInput") as TextBox
            ?? throw new InvalidOperationException("ChatInput was not found."));
        AssertChatMessageDirectionContracts(window, applyLanguage);

        var lines = new List<string>
        {
            "Layal Windows WPF visual smoke snapshots",
            $"Window Width/Height DIPs after show: {window.Width:0}x{window.Height:0}",
            $"Actual client content DIPs: {windowContent.ActualWidth:0}x{windowContent.ActualHeight:0}",
            "Captures render the full WPF Window (including its own theme background) at 96 DPI multiplied by renderScale; this does not emulate physical monitor DPI or multi-monitor movement.",
            "Coverage: 112 full-window images (7 sections x 2 themes x 2 languages x 4 render scales), plus 28 compact-window images (7 sections x 2 themes x 2 languages at 900x560 DIPs).",
            "Compact captures test window resizing only; they do not certify OS display scaling at 100%, 125%, 150%, or 175%."
        };

        foreach (var theme in new[] { "light", "dark" })
        foreach (var language in new[] { "ar", "en" })
        {
            AppRuntime.Settings.ThemeMode = theme;
            AppRuntime.Settings.Language = language;
            applyTheme.Invoke(window, null);
            applyLanguage.Invoke(window, null);
            window.UpdateLayout();
            AssertLanguageLayoutContracts(window, language);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            AssertEmptyStateLocalizationAndVisibility(window, language);
            Assert.AreEqual(FlowDirection.LeftToRight, window.FlowDirection,
                "The root window must not horizontally mirror icons or glyphs in RTL mode.");
            AssertThemeContrastAndControlSurfaces(theme, language, historySelector);
            if (language == "ar")
            {
                Assert.AreEqual("نسخ الشات", BidiTextNormalizer.StripDirectionalControls(copyChatButton.Content as string));
                Assert.AreEqual("محادثة جديدة", BidiTextNormalizer.StripDirectionalControls(newConversationButton.Content as string));
                Assert.AreEqual("مسح السجل المحفوظ", BidiTextNormalizer.StripDirectionalControls(clearHistoryButton.Content as string));
                Assert.AreEqual("إرسال", BidiTextNormalizer.StripDirectionalControls(sendButton.Content as string));
                Assert.AreEqual("تنفيذ المهمة", BidiTextNormalizer.StripDirectionalControls(runTaskButton.Content as string));

                Assert.IsTrue((languageButton.Content as string)?.Contains("\u2066English\u2069", StringComparison.Ordinal) == true,
                    "English language-toggle text must retain left-to-right order on the Arabic screen.");
                Assert.IsTrue(titleText.Text.Contains("\u2066Layal\u2069", StringComparison.Ordinal),
                    "Latin text in the Arabic title must retain left-to-right order.");
                Assert.AreEqual(FlowDirection.LeftToRight, titleText.FlowDirection,
                    "Arabic text should be right-aligned locally rather than mirroring its parent visual.");
                Assert.AreEqual(TextAlignment.Right, titleText.TextAlignment,
                    "Arabic title text should align to the right without flipping icons or glyph shapes.");
                Assert.AreEqual(FlowDirection.LeftToRight, baseUrlInput.FlowDirection,
                    "API base URLs must stay left-to-right on the Arabic screen.");
                Assert.AreEqual(FlowDirection.LeftToRight, modelInput.FlowDirection,
                    "Model IDs must stay left-to-right on the Arabic screen.");
                Assert.AreEqual(FlowDirection.LeftToRight, apiKeyInput.FlowDirection,
                    "API keys must stay left-to-right on the Arabic screen.");
                Assert.AreEqual(FlowDirection.LeftToRight, githubSkillUrlInput.FlowDirection,
                    "GitHub URLs must stay left-to-right on the Arabic screen.");
            }
            else
            {
                Assert.AreEqual("Copy chat", BidiTextNormalizer.StripDirectionalControls(copyChatButton.Content as string));
                Assert.AreEqual("New conversation", BidiTextNormalizer.StripDirectionalControls(newConversationButton.Content as string));
                Assert.AreEqual("Clear saved history", BidiTextNormalizer.StripDirectionalControls(clearHistoryButton.Content as string));
                Assert.AreEqual("Send", BidiTextNormalizer.StripDirectionalControls(sendButton.Content as string));
                Assert.AreEqual("Run task", BidiTextNormalizer.StripDirectionalControls(runTaskButton.Content as string));
            }

            for (var index = 0; index < tabNames.Length; index++)
            {
                tabs.SelectedIndex = index;
                tabs.UpdateLayout();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Keyboard.ClearFocus();

                if (tabNames[index] == "chat")
                {
                    var expectedCopy = language == "ar" ? "نسخ الشات" : "Copy chat";
                    var expectedNew = language == "ar" ? "محادثة جديدة" : "New conversation";
                    var expectedClear = language == "ar" ? "مسح السجل المحفوظ" : "Clear saved history";
                    Assert.AreEqual(expectedCopy,
                        BidiTextNormalizer.StripDirectionalControls(FindVisualText(copyChatButton)),
                        $"Rendered copy button text is incorrect for {theme}/{language}.");
                    Assert.AreEqual(expectedNew,
                        BidiTextNormalizer.StripDirectionalControls(FindVisualText(newConversationButton)),
                        $"Rendered new-conversation button text is incorrect for {theme}/{language}.");
                    Assert.AreEqual(expectedClear,
                        BidiTextNormalizer.StripDirectionalControls(FindVisualText(clearHistoryButton)),
                        $"Rendered clear-history button text is incorrect for {theme}/{language}.");
                }

                var selectedTab = tabs.SelectedItem as TabItem
                    ?? throw new InvalidOperationException($"Selected tab '{tabNames[index]}' is not a TabItem.");
                var tabContent = selectedTab.Content as FrameworkElement
                    ?? throw new InvalidOperationException($"Content for tab '{tabNames[index]}' was not found.");
                Assert.IsTrue(tabContent.ActualWidth > 0 && tabContent.ActualHeight > 0,
                    $"Tab '{tabNames[index]}' has an empty viewport for theme={theme}, language={language}.");

                if (tabNames[index] == "tasks")
                {
                    var expectedBorder = Application.Current.TryFindResource("Layal.Border") as SolidColorBrush
                        ?? throw new InvalidOperationException("Layal.Border was not resolved.");
                    var actualBorder = recentTasksGroup.BorderBrush as SolidColorBrush
                        ?? throw new InvalidOperationException("Task GroupBox border is not a theme brush.");
                    Assert.AreEqual(expectedBorder.Color, actualBorder.Color,
                        $"Task GroupBox border does not follow the active theme ({theme}/{language}).");
                    Assert.IsNull(recentTasksGroup.FocusVisualStyle,
                        "Task GroupBox must not add a system focus outline over the themed border.");
                }

                foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
                {
                    var suffix = (int)Math.Round(scale * 100);
                    var path = Path.Combine(outputDirectory, $"app-{theme}-{language}-{tabNames[index]}-scale{suffix}.png");
                    SaveSnapshot(window, path, scale, Application.Current.TryFindResource("Layal.Background") as Brush);
                    lines.Add($"{Path.GetFileName(path)} | tab={tabNames[index]} | theme={theme} | language={language} | renderScale={scale:0.00} | windowDips={window.Width:0}x{window.Height:0} | contentDips={windowContent.ActualWidth:0}x{windowContent.ActualHeight:0}");
                }
            }

            // Add a smaller window pass to expose clipped controls and fixed-layout assumptions.
            var fullContentWidth = windowContent.ActualWidth;
            var fullContentHeight = windowContent.ActualHeight;
            window.Width = 900;
            window.Height = 560;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            windowContent.UpdateLayout();

            Assert.IsTrue(windowContent.ActualWidth < fullContentWidth,
                $"Window width did not shrink for compact capture ({theme}/{language}).");
            Assert.IsTrue(windowContent.ActualHeight < fullContentHeight,
                $"Window height did not shrink for compact capture ({theme}/{language}).");

            for (var index = 0; index < tabNames.Length; index++)
            {
                tabs.SelectedIndex = index;
                tabs.UpdateLayout();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Keyboard.ClearFocus();

                var selectedTab = tabs.SelectedItem as TabItem
                    ?? throw new InvalidOperationException($"Selected tab '{tabNames[index]}' is not a TabItem at compact size.");
                var tabContent = selectedTab.Content as FrameworkElement
                    ?? throw new InvalidOperationException($"Compact content for tab '{tabNames[index]}' was not found.");
                Assert.IsTrue(tabContent.ActualWidth > 0 && tabContent.ActualHeight > 0,
                    $"Compact tab '{tabNames[index]}' has an empty viewport for theme={theme}, language={language}.");

                var compactPath = Path.Combine(outputDirectory, $"app-compact-{theme}-{language}-{tabNames[index]}-scale100.png");
                SaveSnapshot(window, compactPath, 1.0, Application.Current.TryFindResource("Layal.Background") as Brush);
                lines.Add($"{Path.GetFileName(compactPath)} | tab={tabNames[index]} | theme={theme} | language={language} | windowDips={window.Width:0}x{window.Height:0} | contentDips={windowContent.ActualWidth:0}x{windowContent.ActualHeight:0} | compactResize=true");
            }

            window.Width = 1320;
            window.Height = 860;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        // Render the actual mascot Visual3D tree under representative task and social states.
        var mascot = new FloatingMascotWindow { ShowInTaskbar = false };
        AppRuntime.MascotWindow = mascot;
        mascot.Show();
        mascot.UpdateLayout();
        AssertMascotHighContrastAppearance(mascot);
        var animateTick = typeof(FloatingMascotWindow).GetMethod("AnimateMoodTick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AnimateMoodTick was not found.");
        var mascotMoods = new[]
        {
            "idle", "curious", "thinking", "planning", "researching", "reading",
            "writing", "coding", "using_tool", "validating", "waiting",
            "happy", "love", "embarrassed", "frustrated", "celebrating", "error"
        };

        foreach (var mood in mascotMoods)
        {
            mascot.SetMood(mood, showReaction: false);
            for (var tick = 0; tick < 3; tick++) animateTick.Invoke(mascot, null);
            mascot.UpdateLayout();
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
            {
                var suffix = (int)Math.Round(scale * 100);
                var path = Path.Combine(outputDirectory, $"mascot-{mood}-scale{suffix}.png");
                SaveSnapshot(mascot, path, scale);
                lines.Add($"{Path.GetFileName(path)} | mood={mood} | renderScale={scale:0.00} | dips={mascot.ActualWidth:0}x{mascot.ActualHeight:0}");
            }
        }

        AssertInteractionAndSettingsPersistence(window, mascot, temp.Path);
        lines.Add("Interactive settings regression: language/theme/navigation, mascot toggles, size/opacity, opt-in window inspection, provider validation/persistence, and show/hide passed.");

        AssertEscapeCancelsActiveChatAndAgentOperations(window);
        AssertPrivateChatDoesNotPersistMessagesAndRestoresSavedConversation(window);
        lines.Add("AYO safety regression: Escape cancellation and non-persistent private-chat history passed.");

        File.WriteAllLines(Path.Combine(outputDirectory, "manifest.txt"), lines);
        AppRuntime.IsExiting = true;
        mascot.Close();
        window.Close();
        app.Shutdown();
        App.SuppressStartupForTests = false;
    }

    private static void AssertKeyboardAccessibility(MainWindow window, Button primaryButton, TextBox chatInput)
    {
        var focusStyle = Application.Current.TryFindResource("Layal.KeyboardFocusVisual") as Style;
        Assert.IsNotNull(focusStyle, "The shared keyboard-focus visual resource must be defined.");
        AssertStyleUsesKeyboardFocusRing(primaryButton.Style, focusStyle!, "Button");

        var checkbox = window.FindName("AnimationsCheckbox") as CheckBox
            ?? throw new InvalidOperationException("AnimationsCheckbox was not found.");
        AssertStyleUsesKeyboardFocusRing(checkbox.Style, focusStyle!, "CheckBox");
        var slider = window.FindName("MascotSizeSlider") as Slider
            ?? throw new InvalidOperationException("MascotSizeSlider was not found.");
        AssertStyleUsesKeyboardFocusRing(slider.Style, focusStyle!, "Slider");
        var homeTab = window.FindName("HomeTab") as TabItem
            ?? throw new InvalidOperationException("HomeTab was not found.");
        var navigationStyle = Application.Current.TryFindResource("Layal.NavTabItemStyle") as Style;
        Assert.IsNotNull(navigationStyle, "The navigation tab style must be available.");
        AssertStyleUsesKeyboardFocusRing(navigationStyle!, focusStyle!, "TabItem");

        foreach (var controlType in new[] { typeof(ComboBoxItem), typeof(ListBoxItem) })
        {
            var itemStyle = Application.Current.TryFindResource(controlType) as Style;
            Assert.IsNotNull(itemStyle, $"Implicit {controlType.Name} style must be available.");
            AssertStyleUsesKeyboardFocusRing(itemStyle!, focusStyle!, controlType.Name);
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(chatInput)),
            "Chat input must expose an accessible name.");
        var clipboardButton = window.FindName("ReadClipboardButton") as Button
            ?? throw new InvalidOperationException("ReadClipboardButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(clipboardButton)),
            "Clipboard text review must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(clipboardButton)),
            "Clipboard text review must disclose its privacy behaviour.");
        var remindersButton = window.FindName("ManageRemindersButton") as Button
            ?? throw new InvalidOperationException("ManageRemindersButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(remindersButton)),
            "The reminder manager must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(remindersButton)),
            "The reminder manager must disclose where reminders are stored and how notifications work.");
        var routinesButton = window.FindName("ManageRoutinesButton") as Button
            ?? throw new InvalidOperationException("ManageRoutinesButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(routinesButton)),
            "The routine manager must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(routinesButton)),
            "The routine manager must disclose that choosing a routine does not run it.");
        var themePresetSelector = window.FindName("ThemePresetSelector") as ComboBox
            ?? throw new InvalidOperationException("ThemePresetSelector was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(themePresetSelector)),
            "Theme preset selector must expose an accessible name.");
        Assert.AreEqual(4, themePresetSelector.Items.Count,
            "The app should offer four named presets (Classic, Ocean, Forest, Sunset).");
        var workspaceHubButton = window.FindName("WorkspaceHubButton") as Button
            ?? throw new InvalidOperationException("WorkspaceHubButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(workspaceHubButton)),
            "Workspace hub must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(workspaceHubButton)),
            "Workspace hub must disclose local indexing and export privacy.");
        var gmailButton = window.FindName("GmailInboxButton") as Button
            ?? throw new InvalidOperationException("GmailInboxButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(gmailButton)),
            "The Gmail inbox button must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(gmailButton)),
            "The Gmail inbox button must disclose read-only access and manual sharing.");
        var personaSelector = window.FindName("AssistantPersonaSelector") as ComboBox
            ?? throw new InvalidOperationException("AssistantPersonaSelector was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(personaSelector)),
            "The assistant persona selector must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(personaSelector)),
            "The assistant persona selector must disclose that style does not change permissions.");
        Assert.AreEqual(3, personaSelector.Items.Count,
            "The app should offer three named response styles.");

        var privacyShieldButton = window.FindName("PrivacyShieldButton") as Button
            ?? throw new InvalidOperationException("PrivacyShieldButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(privacyShieldButton)),
            "The master privacy shield must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(privacyShieldButton)),
            "The master privacy shield must disclose the capture operations it blocks.");
        var checkInToggle = window.FindName("ProactiveCheckInEnabledCheckBox") as CheckBox
            ?? throw new InvalidOperationException("ProactiveCheckInEnabledCheckBox was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(checkInToggle)),
            "Scheduled check-ins must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(checkInToggle)),
            "Scheduled check-ins must disclose that they do not inspect the screen or call AI.");
        var inspectElementButton = window.FindName("InspectHoveredElementButton") as Button
            ?? throw new InvalidOperationException("InspectHoveredElementButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(inspectElementButton)),
            "UI element inspection must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(inspectElementButton)),
            "UI element inspection must disclose that no field values or screenshots are captured.");
        var loreWebButton = window.FindName("LoreWebButton") as Button
            ?? throw new InvalidOperationException("LoreWebButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(loreWebButton)),
            "The local 3D memory web must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(loreWebButton)),
            "The local 3D memory web must disclose that links are approximate and private.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(homeTab)),
            "Navigation tabs must expose language-appropriate accessible names.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(window.FindName("BaseUrlInput") as DependencyObject)),
            "Provider fields must expose accessible names.");
    }

    private static void AssertAccessibleStatusRegions(MainWindow window)
    {
        var chatActivity = window.FindName("ChatActivityStatus") as TextBlock
            ?? throw new InvalidOperationException("ChatActivityStatus was not found.");
        var providerStatus = window.FindName("ChatProviderStatus") as TextBlock
            ?? throw new InvalidOperationException("ChatProviderStatus was not found.");
        var connectionStatus = window.FindName("ConnectionStatusText") as TextBlock
            ?? throw new InvalidOperationException("ConnectionStatusText was not found.");
        var taskResult = window.FindName("TaskResult") as TextBox
            ?? throw new InvalidOperationException("TaskResult was not found.");

        var liveStatusNames = new[]
        {
            "ConnectionStatusText", "HomeResult", "ChatActivityStatus", "ChatCopyStatus",
            "TaskResult", "TaskCopyStatus", "TaskEventsList", "SkillsResult",
            "MemoryResult", "ProviderActionStatus"
        };
        foreach (var name in liveStatusNames)
        {
            var status = window.FindName(name) as FrameworkElement
                ?? throw new InvalidOperationException($"Live status element '{name}' was not found.");
            Assert.AreEqual(System.Windows.Automation.AutomationLiveSetting.Polite,
                System.Windows.Automation.AutomationProperties.GetLiveSetting(status),
                $"Live status '{name}' must use non-interruptive announcements.");
        }
        Assert.AreEqual(System.Windows.Automation.AutomationLiveSetting.Off,
            System.Windows.Automation.AutomationProperties.GetLiveSetting(providerStatus),
            "Provider setup summary must not repeat every transient chat activity announcement.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(chatActivity)),
            "Chat activity must have a localized accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(providerStatus)),
            "Provider status must have a separate accessible name.");

        var setActivityStatus = typeof(MainWindow).GetMethod("SetChatActivityStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetChatActivityStatus was not found.");
        var refreshProviderStatus = typeof(MainWindow).GetMethod("RefreshProviderStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RefreshProviderStatus was not found.");
        const string marker = "activity status remains separate";
        setActivityStatus.Invoke(window, new object[] { marker });
        refreshProviderStatus.Invoke(window, null);
        Assert.AreEqual(marker, BidiTextNormalizer.StripDirectionalControls(chatActivity.Text),
            "Refreshing provider connectivity must not overwrite the chat's current activity state.");
        Assert.AreNotEqual(marker, BidiTextNormalizer.StripDirectionalControls(providerStatus.Text),
            "Provider connectivity must remain a separate status from chat activity.");
        setActivityStatus.Invoke(window, new object[] { string.Empty });
    }

    private static void AssertMascotHighContrastAppearance(FloatingMascotWindow mascot)
    {
        var applyAppearance = typeof(FloatingMascotWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplySystemAccessibilityAppearance" && method.GetParameters().Length == 1);
        var shell = mascot.FindName("Shell") as Border
            ?? throw new InvalidOperationException("Mascot shell border was not found.");
        var halo = mascot.FindName("GlowHalo") as FrameworkElement
            ?? throw new InvalidOperationException("Mascot glow halo was not found.");
        var effects = mascot.FindName("CinematicEffectsLayer") as FrameworkElement
            ?? throw new InvalidOperationException("Mascot cinematic effects layer was not found.");

        var originalBrush = shell.BorderBrush;
        var originalThickness = shell.BorderThickness;
        var originalEffect = shell.Effect;
        var originalHaloVisibility = halo.Visibility;
        var originalEffectsVisibility = effects.Visibility;
        var systemHighContrast = SystemParameters.HighContrast;

        applyAppearance.Invoke(mascot, new object[] { true });

        Assert.AreEqual(new Thickness(3), shell.BorderThickness,
            "High Contrast must give the floating mascot a visible solid outline.");
        Assert.AreEqual(SystemColors.WindowTextColor, ((SolidColorBrush)shell.BorderBrush).Color,
            "The mascot outline must use the Windows system text color.");
        Assert.IsNull(shell.Effect, "High Contrast must remove the mascot's decorative shell shadow.");
        Assert.AreEqual(Visibility.Collapsed, halo.Visibility,
            "High Contrast must suppress the low-contrast decorative glow.");
        Assert.AreEqual(Visibility.Collapsed, effects.Visibility,
            "High Contrast must suppress decorative particles.");
        
        applyAppearance.Invoke(mascot, new object[] { systemHighContrast });
        Assert.AreEqual(originalBrush, shell.BorderBrush, "The mascot border must restore to the system-selected appearance.");
        Assert.AreEqual(originalThickness, shell.BorderThickness, "The mascot border thickness must restore after the forced test state.");
        Assert.AreEqual(originalEffect, shell.Effect, "The mascot shell effect must restore after the forced test state.");
        Assert.AreEqual(originalHaloVisibility, halo.Visibility, "The mascot glow visibility must restore after the forced test state.");
        Assert.AreEqual(originalEffectsVisibility, effects.Visibility, "The cinematic layer visibility must restore after the forced test state.");
    }


    private static void AssertInitialWindowFitsWorkArea(MainWindow window)
    {
        var workArea = SystemParameters.WorkArea;
        Assert.IsTrue(workArea.Width > 0 && workArea.Height > 0,
            "Windows must report a usable work area for responsive sizing.");
        Assert.IsTrue(window.Width <= workArea.Width,
            $"Initial window width {window.Width:0.##} DIPs exceeds work area width {workArea.Width:0.##} DIPs.");
        Assert.IsTrue(window.Height <= workArea.Height,
            $"Initial window height {window.Height:0.##} DIPs exceeds work area height {workArea.Height:0.##} DIPs.");
        Assert.IsTrue(window.Left >= workArea.Left && window.Top >= workArea.Top &&
                      window.Left + window.Width <= workArea.Right &&
                      window.Top + window.Height <= workArea.Bottom,
            "Initial main window must be positioned fully inside the Windows work area.");
        Assert.IsTrue(window.MinWidth <= window.Width && window.MinHeight <= window.Height,
            "Responsive initial sizing must keep minimum dimensions at or below the applied window dimensions.");
    }


    private static void AssertLanguageLayoutContracts(MainWindow window, string language)
    {
        var ar = language == "ar";
        var expectedWritingDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var proseFields = new[]
        {
            "ChatInput", "TaskInput", "MemoryTitleInput", "MemoryContentInput", "MemorySearchInput"
        };

        foreach (var fieldName in proseFields)
        {
            var field = window.FindName(fieldName) as Control
                ?? throw new InvalidOperationException($"Expected prose input '{fieldName}' was not found.");
            Assert.AreEqual(expectedWritingDirection, field.FlowDirection,
                $"Prose field '{fieldName}' has the wrong flow direction for {language}.");
        }

        foreach (var fieldName in new[] { "BaseUrlInput", "ModelInput", "ApiKeyInput", "GitHubSkillUrlInput" })
        {
            var field = window.FindName(fieldName) as Control
                ?? throw new InvalidOperationException($"Expected technical input '{fieldName}' was not found.");
            Assert.AreEqual(FlowDirection.LeftToRight, field.FlowDirection,
                $"Technical field '{fieldName}' must remain LTR for {language}.");
        }

        Assert.AreEqual(FlowDirection.LeftToRight, window.FlowDirection,
            "The whole window must not be mirrored to achieve Arabic RTL text.");

        var mainTabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        Assert.AreEqual(expectedWritingDirection, mainTabs.FlowDirection,
            $"The navigation shell must reflect the selected writing direction for {language}.");
        var sidebar = window.FindName("SidebarOverlay") as Grid
            ?? throw new InvalidOperationException("SidebarOverlay was not found.");
        Assert.AreEqual(ar ? HorizontalAlignment.Right : HorizontalAlignment.Left, sidebar.HorizontalAlignment,
            $"The brand/settings overlay must stay aligned with the navigation rail for {language}.");
        foreach (var panelName in new[] { "SidebarBrandRow", "SidebarFooterPanel" })
        {
            var panel = window.FindName(panelName) as FrameworkElement
                ?? throw new InvalidOperationException($"Expected RTL-aware sidebar panel '{panelName}' was not found.");
            Assert.AreEqual(expectedWritingDirection, panel.FlowDirection,
                $"Sidebar panel '{panelName}' has the wrong flow direction for {language}.");
        }

        var taskResult = window.FindName("TaskResult") as TextBox
            ?? throw new InvalidOperationException("TaskResult was not found.");
        Assert.AreEqual(ar ? "نتيجة المهمة" : "Task result",
            System.Windows.Automation.AutomationProperties.GetName(taskResult),
            "Task result must have a localized accessible name.");
        Assert.AreEqual(ar
                ? "يعرض النتيجة النهائية وحالة المهمة، بما في ذلك التحذيرات أو الإخفاق."
                : "Shows the final task result and its status, including warnings or failures.",
            System.Windows.Automation.AutomationProperties.GetHelpText(taskResult),
            "Task result must expose localized context to assistive technology.");
        Assert.AreEqual(System.Windows.Automation.AutomationLiveSetting.Polite,
            System.Windows.Automation.AutomationProperties.GetLiveSetting(taskResult),
            "Task result must announce polite live updates to assistive technology.");
    }


    private static void AssertChatMessageDirectionContracts(MainWindow window, MethodInfo applyLanguage)
    {
        var arabicMixed = new ChatMessage
        {
            Role = "user",
            Content = "راجع الرابط https://example.com/v1/models/gpt-4o مع المعرّف model-2026"
        };
        var englishOnly = new ChatMessage
        {
            Role = "assistant",
            Content = "Check https://example.com/v1/models/gpt-4o and model-2026"
        };

        window.ChatMessages.Add(arabicMixed);
        window.ChatMessages.Add(englishOnly);
        applyLanguage.Invoke(window, null);

        Assert.AreEqual(FlowDirection.RightToLeft, arabicMixed.Direction,
            "A message containing Arabic with Latin technical tokens must use RTL paragraph flow.");
        Assert.AreEqual(FlowDirection.LeftToRight, englishOnly.Direction,
            "An English-only message must retain LTR paragraph flow.");

        window.ChatMessages.Remove(arabicMixed);
        window.ChatMessages.Remove(englishOnly);
        applyLanguage.Invoke(window, null);
        AssertEmptyStateLocalizationAndVisibility(window, AppRuntime.Settings.Language);
    }


    private static void AssertClockWeatherDashboard(MainWindow window)
    {
        var clock = window.FindName("ClockTimeText") as TextBlock
            ?? throw new InvalidOperationException("ClockTimeText was not found.");
        var date = window.FindName("CurrentDateText") as TextBlock
            ?? throw new InvalidOperationException("CurrentDateText was not found.");
        var weather = window.FindName("WeatherSummaryText") as TextBlock
            ?? throw new InvalidOperationException("WeatherSummaryText was not found.");
        var weatherLocation = window.FindName("WeatherLocationInput") as TextBox
            ?? throw new InvalidOperationException("WeatherLocationInput was not found.");
        var refreshButton = window.FindName("RefreshWeatherButton") as Button
            ?? throw new InvalidOperationException("RefreshWeatherButton was not found.");
        var useMyLocationButton = window.FindName("UseMyLocationButton") as Button
            ?? throw new InvalidOperationException("UseMyLocationButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(clock.Text), "The dashboard clock must display the system-local time.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(date.Text), "The dashboard must display the system-local date.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(weather.Text), "The dashboard must explain the weather state, even before a city is configured.");
        Assert.IsTrue(refreshButton.IsEnabled, "Weather refresh must be available after the initial display.");
        Assert.IsTrue(useMyLocationButton.IsEnabled, "Automatic device-location detection must be available.");
        Assert.IsNotNull(weatherLocation, "The city setting must be available without requiring precise device location.");
    }

    private static void AssertEmptyStateLocalizationAndVisibility(MainWindow window, string language)
    {
        var isArabic = language == "ar";
        AssertEmptyState(window, "ChatEmptyStateText", window.ChatMessages.Count,
            isArabic ? "لا توجد رسائل هنا بعد. اكتب رسالة بالأسفل لبدء المحادثة." : "No messages yet. Type a message below to start the conversation.",
            isArabic ? "حالة المحادثة الفارغة" : "Chat empty state");
        AssertEmptyState(window, "TaskEventsEmptyStateText", window.TaskEvents.Count,
            isArabic ? "لا يوجد سجل نشاط بعد. ابدأ مهمة لعرض أحداث تنفيذها هنا." : "No task activity yet. Start a task to see its events here.",
            isArabic ? "حالة سجل المهام الفارغ" : "Task activity empty state");
        AssertEmptyState(window, "RecentTasksEmptyStateText", window.RecentTasks.Count,
            isArabic ? "لا توجد مهام سابقة بعد." : "No previous tasks yet.",
            isArabic ? "حالة المهام السابقة الفارغة" : "Recent tasks empty state");
        AssertEmptyState(window, "SkillsEmptyStateText", window.InstalledSkills.Count,
            isArabic ? "لا توجد مهارات مثبتة. استورد ملف ZIP أو مجلدًا من GitHub للبدء." : "No skills installed. Import a ZIP or GitHub folder to get started.",
            isArabic ? "حالة قائمة المهارات الفارغة" : "Skills empty state");
        AssertEmptyState(window, "MemoryEmptyStateText", window.MemoryNotes.Count,
            isArabic ? "لا توجد ملاحظات محفوظة. أنشئ ملاحظة جديدة للبدء." : "No saved notes. Create a note to get started.",
            isArabic ? "حالة الذاكرة الفارغة" : "Memory empty state");
    }

    private static void AssertEmptyState(
        MainWindow window,
        string elementName,
        int itemCount,
        string expectedText,
        string expectedAutomationName)
    {
        var emptyState = window.FindName(elementName) as TextBlock
            ?? throw new InvalidOperationException($"Empty-state element '{elementName}' was not found.");
        Assert.AreEqual(expectedText, BidiTextNormalizer.StripDirectionalControls(emptyState.Text),
            $"Empty state '{elementName}' must be localized in the selected language.");
        Assert.AreEqual(expectedAutomationName,
            System.Windows.Automation.AutomationProperties.GetName(emptyState),
            $"Empty state '{elementName}' must expose a localized UI Automation name.");
        Assert.AreEqual(itemCount == 0 ? Visibility.Visible : Visibility.Collapsed, emptyState.Visibility,
            $"Empty state '{elementName}' must only be visible when its source collection is empty.");
        Assert.AreEqual(TextWrapping.Wrap, emptyState.TextWrapping,
            $"Empty state '{elementName}' must support multiline copy at high scaling.");
    }

    private static void AssertStyleUsesKeyboardFocusRing(Style style, Style focusStyle, string controlName)
    {
        var focusSetter = style.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.FocusVisualStyleProperty);
        Assert.IsNotNull(focusSetter, $"{controlName} style must declare its keyboard focus visual.");
        Assert.AreSame(focusStyle, focusSetter!.Value,
            $"{controlName} style must use the shared keyboard-focus ring.");
    }

    private static string? FindVisualText(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
                return textBlock.Text;

            var nested = FindVisualText(child);
            if (!string.IsNullOrEmpty(nested))
                return nested;
        }

        return null;
    }

    private static void AssertThemeTogglePersistsLivePalette(MainWindow window, ComboBox historySelector)
    {
        var themeButton = window.FindName("ThemeButton") as Button
            ?? throw new InvalidOperationException("ThemeButton was not found.");
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyTheme was not found.");
        var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyLanguage was not found.");
        var toggleTheme = typeof(MainWindow).GetMethod("ThemeButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ThemeButton_Click was not found.");

        // Exercise the actual UI event handler, not just ApplyTheme(), so a broken click
        // binding or a missing settings save cannot slip past visual snapshot generation.
        AppRuntime.Settings.ThemeMode = "light";
        AppRuntime.Settings.Language = "ar";
        applyTheme.Invoke(window, null);
        applyLanguage.Invoke(window, null);

        Assert.AreEqual(Color.FromRgb(244, 247, 252), (Color)Application.Current.Resources["Layal.Color.Background"],
            "The light background palette must be active before the toggle.");
        Assert.AreEqual("☾ المظهر الداكن", BidiTextNormalizer.StripDirectionalControls(themeButton.Content as string),
            "In light mode, the button must offer switching to dark mode.");
        AssertThemeContrastAndControlSurfaces("light", "ar", historySelector);

        toggleTheme.Invoke(window, new object?[] { themeButton, new RoutedEventArgs(Button.ClickEvent, themeButton) });

        Assert.AreEqual("dark", AppRuntime.Settings.ThemeMode,
            "Clicking the theme button must switch the selected theme to dark.");
        Assert.AreEqual(Color.FromRgb(17, 21, 33), (Color)Application.Current.Resources["Layal.Color.Background"],
            "Clicking the theme button must apply the dark resource palette.");
        Assert.AreEqual("dark", AppRuntime.SettingsStore.Load().ThemeMode,
            "Clicking the theme button must persist dark mode in settings.json.");
        Assert.AreEqual("☀ المظهر الفاتح", BidiTextNormalizer.StripDirectionalControls(themeButton.Content as string),
            "In dark mode, the button must offer switching to light mode.");
        AssertThemeContrastAndControlSurfaces("dark", "ar", historySelector);

        toggleTheme.Invoke(window, new object?[] { themeButton, new RoutedEventArgs(Button.ClickEvent, themeButton) });

        Assert.AreEqual("light", AppRuntime.Settings.ThemeMode,
            "Clicking the theme button again must restore the light theme.");
        Assert.AreEqual(Color.FromRgb(244, 247, 252), (Color)Application.Current.Resources["Layal.Color.Background"],
            "Switching back must restore the light resource palette.");
        Assert.AreEqual("light", AppRuntime.SettingsStore.Load().ThemeMode,
            "Switching back must persist light mode in settings.json.");
        AssertThemeContrastAndControlSurfaces("light", "ar", historySelector);
    }

    private static void AssertInteractionAndSettingsPersistence(MainWindow window, FloatingMascotWindow mascot, string dataRoot)
    {
        var store = new SettingsStore(dataRoot);
        var languageButton = window.FindName("LanguageButton") as Button
            ?? throw new InvalidOperationException("LanguageButton was not found.");
        var tabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        var homeTab = window.FindName("HomeTab") as TabItem
            ?? throw new InvalidOperationException("HomeTab was not found.");
        var chatTab = window.FindName("ChatTab") as TabItem
            ?? throw new InvalidOperationException("ChatTab was not found.");
        var settingsTab = window.FindName("SettingsTab") as TabItem
            ?? throw new InvalidOperationException("SettingsTab was not found.");
        var goChat = window.FindName("GoChatButton") as Button
            ?? throw new InvalidOperationException("GoChatButton was not found.");
        var goSettings = window.FindName("GoSettingsButton") as Button
            ?? throw new InvalidOperationException("GoSettingsButton was not found.");
        var showMascot = window.FindName("ShowMascotButton") as Button
            ?? throw new InvalidOperationException("ShowMascotButton was not found.");
        var alwaysOnTop = window.FindName("AlwaysOnTopCheckbox") as CheckBox
            ?? throw new InvalidOperationException("AlwaysOnTopCheckbox was not found.");
        var animations = window.FindName("AnimationsCheckbox") as CheckBox
            ?? throw new InvalidOperationException("AnimationsCheckbox was not found.");
        var windowInspection = window.FindName("WindowInspectionCheckbox") as CheckBox
            ?? throw new InvalidOperationException("WindowInspectionCheckbox was not found.");
        var size = window.FindName("MascotSizeSlider") as Slider
            ?? throw new InvalidOperationException("MascotSizeSlider was not found.");
        var opacity = window.FindName("MascotOpacitySlider") as Slider
            ?? throw new InvalidOperationException("MascotOpacitySlider was not found.");
        var applyMascotSettings = window.FindName("ApplyMascotSettingsButton") as Button
            ?? throw new InvalidOperationException("ApplyMascotSettingsButton was not found.");
        var baseUrl = window.FindName("BaseUrlInput") as TextBox
            ?? throw new InvalidOperationException("BaseUrlInput was not found.");
        var model = window.FindName("ModelInput") as TextBox
            ?? throw new InvalidOperationException("ModelInput was not found.");
        var saveProvider = window.FindName("SaveProviderButton") as Button
            ?? throw new InvalidOperationException("SaveProviderButton was not found.");
        var providerStatus = window.FindName("ProviderActionStatus") as TextBlock
            ?? throw new InvalidOperationException("ProviderActionStatus was not found.");

        // Exercise the visible language toggle in both directions and verify a fresh settings read.
        AppRuntime.Settings.Language = "ar";
        languageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, languageButton));
        Assert.AreEqual("en", AppRuntime.Settings.Language, "The header language button should switch Arabic to English.");
        Assert.AreEqual("en", store.Load().Language, "The language change must survive reloading settings.json.");
        StringAssert.Contains(settingsTab.Header?.ToString() ?? string.Empty, "Settings",
            "Switching to English must update the settings tab label.");

        languageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, languageButton));
        Assert.AreEqual("ar", AppRuntime.Settings.Language, "The header language button should switch English back to Arabic.");
        Assert.AreEqual("ar", store.Load().Language, "Switching the language back must also persist.");
        StringAssert.Contains(settingsTab.Header?.ToString() ?? string.Empty, "الإعدادات",
            "Switching back to Arabic must update the settings tab label.");

        // Navigation entry points must select the real corresponding tabs.
        goSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, goSettings));
        Assert.AreSame(settingsTab, tabs.SelectedItem, "The home settings shortcut must navigate to Settings.");
        goChat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, goChat));
        Assert.AreSame(chatTab, tabs.SelectedItem, "The home chat shortcut must navigate to Chat.");
        tabs.SelectedItem = homeTab;

        // Checkbox changes persist immediately and are reflected by the live mascot.
        alwaysOnTop.IsChecked = false;
        Assert.IsFalse(store.Load().MascotAlwaysOnTop, "Turning off always-on-top must persist immediately.");
        Assert.IsFalse(mascot.Topmost, "The live mascot must stop staying above other windows.");
        alwaysOnTop.IsChecked = true;
        Assert.IsTrue(store.Load().MascotAlwaysOnTop, "Turning always-on-top back on must persist.");
        Assert.IsTrue(mascot.Topmost, "The live mascot must return to topmost mode.");

        animations.IsChecked = false;
        Assert.IsFalse(store.Load().MascotAnimationsEnabled, "Disabling mascot animation must persist immediately.");
        animations.IsChecked = true;
        Assert.IsTrue(store.Load().MascotAnimationsEnabled, "Re-enabling mascot animation must persist.");

        // The window-title permission is opt-in and must round-trip in both directions.
        windowInspection.IsChecked = true;
        Assert.IsTrue(store.Load().AllowWindowTitleInspection, "Enabling optional window-title inspection must persist.");
        windowInspection.IsChecked = false;
        Assert.IsFalse(store.Load().AllowWindowTitleInspection, "Disabling optional window-title inspection must persist.");

        // Apply the size/opacity controls through the actual XAML Click event, then reload settings.
        size.Value = 1.25;
        opacity.Value = 0.8;
        applyMascotSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, applyMascotSettings));
        var applied = store.Load();
        Assert.AreEqual(1.25, applied.MascotScale, 0.0001, "Mascot scale must persist after Apply.");
        Assert.AreEqual(0.8, applied.MascotOpacity, 0.0001, "Mascot opacity must persist after Apply.");
        Assert.IsTrue(applied.MascotAlwaysOnTop, "Always-on-top state must persist alongside slider settings.");
        Assert.IsTrue(mascot.Topmost, "Apply must keep the live window's topmost state in sync.");
        Assert.AreEqual(0.8, mascot.Opacity, 0.001, "Apply must update live window opacity.");
        Assert.AreEqual(248 * 1.25, mascot.Width, 0.5, "Apply must resize the live mascot width within WPF device-pixel rounding tolerance.");
        Assert.AreEqual(175 * 1.25, mascot.Height, 0.5, "Apply must resize the live mascot height within WPF layout rounding tolerance.");

        // The provider form must reject an unsupported scheme without partially saving it.
        var oldBaseUrl = applied.ProviderBaseUrl;
        var oldModelId = applied.ModelId;
        baseUrl.Text = "file:///not-a-provider";
        model.Text = "should-not-be-saved";
        saveProvider.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, saveProvider));
        Assert.AreEqual(oldBaseUrl, store.Load().ProviderBaseUrl, "An invalid provider URL must not overwrite a previously saved URL.");
        Assert.AreEqual(oldModelId, store.Load().ModelId, "A failed provider save must not partially write the model ID.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(providerStatus.Text), "Invalid provider settings must show feedback.");

        // A valid URL/model pair must be normalized and persisted without requiring network access.
        baseUrl.Text = "http://127.0.0.1:31415/v1/";
        model.Text = "interaction-regression-test";
        saveProvider.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, saveProvider));
        Assert.AreEqual("http://127.0.0.1:31415/v1", store.Load().ProviderBaseUrl);
        Assert.AreEqual("interaction-regression-test", store.Load().ModelId);

        // Restore the exact original provider state. The form intentionally rejects an empty
        // URL/model pair, so returning a fresh install to its unconfigured state must bypass UI validation.
        baseUrl.Text = oldBaseUrl;
        model.Text = oldModelId;
        var restoredSettings = store.Load();
        restoredSettings.ProviderBaseUrl = oldBaseUrl;
        restoredSettings.ModelId = oldModelId;
        store.Save(restoredSettings);
        AppRuntime.Settings = restoredSettings;
        alwaysOnTop.IsChecked = true;
        animations.IsChecked = true;
        windowInspection.IsChecked = false;
        size.Value = 1.0;
        opacity.Value = 1.0;
        applyMascotSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, applyMascotSettings));
        Assert.IsTrue(store.Load().MascotAlwaysOnTop);
        Assert.IsTrue(store.Load().MascotAnimationsEnabled);
        Assert.AreEqual(1.0, store.Load().MascotScale, 0.0001);
        Assert.AreEqual(1.0, store.Load().MascotOpacity, 0.0001);
        Assert.IsFalse(store.Load().AllowWindowTitleInspection);
        Assert.AreEqual(oldBaseUrl, store.Load().ProviderBaseUrl);
        Assert.AreEqual(oldModelId, store.Load().ModelId);

        // Test the show control against the real floating window, then leave the harness in a safe state.
        mascot.Hide();
        showMascot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, showMascot));
        Assert.IsTrue(mascot.IsVisible, "The show-mascot button must restore a hidden mascot window.");
        Assert.IsTrue(mascot.Topmost, "ShowAgain must respect the persisted always-on-top preference.");

        AppRuntime.Settings = store.Load();
        AppRuntime.Settings.ThemeMode = "light";
        AppRuntime.Settings.Language = "ar";
        store.Save(AppRuntime.Settings);
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyTheme was not found.");
        var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyLanguage was not found.");
        applyTheme.Invoke(window, null);
        applyLanguage.Invoke(window, null);
        mascot.UpdateLanguage();
        mascot.Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
        mascot.Opacity = AppRuntime.Settings.MascotOpacity;
        mascot.ApplyScale(AppRuntime.Settings.MascotScale);
    }

    private static void AssertThemeContrastAndControlSurfaces(
        string theme,
        string language,
        ComboBox historySelector)
    {
        var resources = Application.Current.Resources;
        var background = (Color)resources["Layal.Color.Background"];
        var text = (Color)resources["Layal.Color.Text"];
        var muted = (Color)resources["Layal.Color.Muted"];
        var scrollThumb = (Color)resources["Layal.Color.ScrollThumb"];
        var panelBrush = Application.Current.TryFindResource("Layal.Panel") as SolidColorBrush;
        var comboBackground = historySelector.Background as SolidColorBrush;
        var textBrush = Application.Current.TryFindResource("Layal.Text") as SolidColorBrush;
        var comboForeground = historySelector.Foreground as SolidColorBrush;

        Assert.IsNotNull(panelBrush, $"Layal.Panel must resolve for {theme}/{language}.");
        Assert.IsNotNull(comboBackground, $"ComboBox background must use a solid theme surface for {theme}/{language}.");
        Assert.IsNotNull(textBrush, $"Layal.Text must resolve for {theme}/{language}.");
        Assert.IsNotNull(comboForeground, $"ComboBox text must use a theme-aware foreground for {theme}/{language}.");
        Assert.AreEqual(panelBrush.Color, comboBackground.Color,
            $"Chat history ComboBox leaked a native/default surface in {theme}/{language}.");
        Assert.AreEqual(textBrush.Color, comboForeground.Color,
            $"Chat history ComboBox text does not follow {theme}/{language} text resources.");

        Assert.IsTrue(ContrastRatio(text, background) >= 7.0,
            $"Primary text contrast must be at least 7:1 in {theme}/{language}.");
        Assert.IsTrue(ContrastRatio(muted, background) >= 4.5,
            $"Muted text contrast must be at least 4.5:1 in {theme}/{language}.");
        Assert.IsTrue(ContrastRatio(scrollThumb, background) >= 2.0,
            $"Scrollbar thumb contrast must be at least 2:1 in {theme}/{language}.");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) +
               0.7152 * Linear(color.G) +
               0.0722 * Linear(color.B);
    }

    private static void InitializeRuntime(string root)
    {
        AppRuntime.SettingsStore = new SettingsStore(root);
        AppRuntime.Settings = new UserSettings();
        AppRuntime.SecretStore = new DpapiSecretStore(Path.Combine(root, "secrets", "visual-smoke.dpapi"));
        AppRuntime.ApiConnectionService = new ApiConnectionService(Path.Combine(root, "api-connections.dpapi"));
        AppRuntime.SkillPackageService = new SkillPackageService(Path.Combine(root, "skills"));
        AppRuntime.MemoryService = new MemoryService(Path.Combine(root, "memory.dpapi"));
        AppRuntime.TaskHistoryService = new TaskHistoryService(Path.Combine(root, "tasks.dpapi"));
        AppRuntime.ReminderService = new ReminderService(Path.Combine(root, "reminders.dpapi"));
        AppRuntime.SavedRoutineService = new SavedRoutineService(Path.Combine(root, "saved-routines.dpapi"));
        AppRuntime.ChatHistoryStore = new ProtectedJsonStore<List<ChatMessage>>(Path.Combine(root, "chat.dpapi"));
        AppRuntime.ChatSessionService = new ChatSessionService(
            Path.Combine(root, "sessions.dpapi"), AppRuntime.ChatHistoryStore, "Visual smoke");
        AppRuntime.WorkspaceRoot = Path.Combine(root, "workspace");
        AppRuntime.AgentToolExecutor = new AgentToolExecutor(AppRuntime.WorkspaceRoot, AppRuntime.MemoryService);
        AppRuntime.AgentOrchestrator = new AgentOrchestrator(
            new OpenAiCompatibleClient(), AppRuntime.AgentToolExecutor,
            AppRuntime.SkillPackageService, AppRuntime.TaskHistoryService);
        AppRuntime.IsExiting = false;
    }

    private static void SaveSnapshot(
        FrameworkElement visual,
        string path,
        double renderScale = 1.0,
        Brush? backdrop = null)
    {
        visual.UpdateLayout();
        renderScale = Math.Clamp(renderScale, 1.0, 2.0);
        var dpi = 96.0 * renderScale;
        var widthDips = Math.Max(1, visual.ActualWidth);
        var heightDips = Math.Max(1, visual.ActualHeight);
        var width = Math.Max(1, (int)Math.Ceiling(widthDips * renderScale));
        var height = Math.Max(1, (int)Math.Ceiling(heightDips * renderScale));

        Visual renderSource = visual;
        if (backdrop is not null)
        {
            // WPF's Window/root content can include transparent margins and shadow pixels.
            // Composite the actual theme background behind the visual to make the PNG match
            // how those same pixels appear inside the live window instead of viewer black/blue.
            var composite = new DrawingVisual();
            using (var drawing = composite.RenderOpen())
            {
                var bounds = new Rect(0, 0, widthDips, heightDips);
                drawing.DrawRectangle(backdrop, null, bounds);
                drawing.DrawRectangle(
                    new VisualBrush(visual) { Stretch = Stretch.Fill },
                    null,
                    bounds);
            }

            renderSource = composite;
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(renderSource);
        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1)
            throw new InvalidOperationException($"Empty snapshot generated for {visual.GetType().Name}.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
