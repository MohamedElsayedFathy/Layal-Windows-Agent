using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

public partial class MainWindow
{
    private void ChatConversationSurface_Loaded(object sender, RoutedEventArgs e) =>
        ApplyChatCompactLayout(MainTabs.ActualHeight);

    private void ChatConversationSurface_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyChatCompactLayout(MainTabs.ActualHeight);

    private void MainTabs_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyChatCompactLayout(e.NewSize.Height);

    private void ApplyChatCompactLayout(double availableHeight)
    {
        if (availableHeight <= 0 || ChatWelcomePanel is null || ClockWeatherCard is null)
            return;

        // Measure the bounded tab viewport, not the chat surface's desired height.
        // The chat content can measure taller than the visible window and be clipped by
        // the page shell; using that unbounded height leaves the welcome card in full mode.
        // MainTabs.SizeChanged keeps this responsive even when the chat child does not resize.
        // Use a dense welcome card before hiding the secondary weather summary.
        // The main window enforces a 600-DIP minimum height; after its caption,
        // margins and navigation shell, the remaining tab viewport is roughly 530 DIPs.
        // Use a viewport-relative breakpoint rather than the old child-height threshold.
        var compact = availableHeight < 560;
        var dense = availableHeight < 900;
        var layoutMode = compact ? 2 : dense ? 1 : 0;
        ChatEmptyStateText.Text = dense
            ? (IsArabic ? "اكتب سؤالك أو اختر اقتراحًا." : "Choose a prompt or type your question.")
            : (IsArabic
                ? "اكتب سؤالك مباشرة، أو اختر نقطة بداية وسأساعدك في تطويرها."
                : "Type a question or choose a prompt starter to fill the message field. Messages appear here after sending.");
        if (_chatWelcomeLayoutModeApplied == layoutMode)
            return;

        _chatWelcomeLayoutModeApplied = layoutMode;
        ChatWelcomeEyebrow.Visibility = dense ? Visibility.Collapsed : Visibility.Visible;
        ChatEmptyStateText.Visibility = dense ? Visibility.Collapsed : Visibility.Visible;
        ChatWelcomeTitle.FontSize = dense ? 21 : 23;
        ChatWelcomeTitle.Margin = dense
            ? new Thickness(0, 0, 0, 3)
            : new Thickness(0, 0, 0, 7);
        ClockWeatherCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        ChatHeaderMascot.Width = compact ? 46 : 60;
        ChatHeaderMascot.Height = compact ? 46 : 60;

        ChatWelcomeAvatarShell.Width = dense ? 54 : 88;
        ChatWelcomeAvatarShell.Height = dense ? 54 : 88;
        ChatWelcomeAvatarShell.Margin = dense
            ? new Thickness(0, 0, 10, 0)
            : new Thickness(0, 0, 18, 0);
        ChatWelcomeAvatarImage.Width = dense ? 50 : 82;
        ChatWelcomeAvatarImage.Height = dense ? 50 : 82;

        ChatWelcomePanel.Margin = dense
            ? new Thickness(4, 1, 4, 1)
            : new Thickness(12, 6, 12, 6);
        ChatWelcomePanel.Padding = dense
            ? new Thickness(8)
            : new Thickness(18);
        ChatEmptyStateText.FontSize = dense ? 11.5 : 13.5;
        ChatEmptyStateText.Margin = dense
            ? new Thickness(0, 0, 0, 2)
            : new Thickness(0, 0, 0, 11);
        ChatEmptyStateText.MaxWidth = 560;
        ChatSuggestionPlanButton.Padding = dense
            ? new Thickness(8, 5, 8, 5)
            : new Thickness(16, 9, 16, 9);
        ChatSuggestionSummaryButton.Padding = dense
            ? new Thickness(8, 5, 8, 5)
            : new Thickness(16, 9, 16, 9);
        ChatSuggestionIdeasButton.Padding = dense
            ? new Thickness(8, 5, 8, 5)
            : new Thickness(16, 9, 16, 9);
    }

    private void HomeMetricsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.UniformGrid metricsGrid)
            return;

        // Four cards read comfortably on wide workspaces; two columns avoid compressed cards on compact windows.
        var columns = e.NewSize.Width <= 880 ? 2 : 4;
        if (metricsGrid.Columns != columns)
            metricsGrid.Columns = columns;
    }

    private void BottomDockShell_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateMainTabsNavigationMargin();

    private void UpdateMainTabsNavigationMargin()
    {
        if (BottomDockShell.ActualWidth <= 0)
            return;

        const double contentGap = 12;
        var left = BottomDockHost.Margin.Left + BottomDockShell.ActualWidth + contentGap;
        MainTabs.Margin = new Thickness(Math.Ceiling(left), 10, 14, 12);
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyCompactWindowChrome(e.NewSize.Width);

    private void ApplyCompactWindowChrome(double availableWidth)
    {
        if (availableWidth <= 0)
            return;

        var compact = availableWidth < 960;
        if (_compactWindowChromeApplied == compact)
            return;

        _compactWindowChromeApplied = compact;
        SubtitleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TitleText.MaxWidth = compact ? 170 : double.PositiveInfinity;
        TitleText.FontSize = compact ? 14 : 15;
        ConnectionStatusShell.MaxWidth = compact ? 150 : double.PositiveInfinity;
        ConnectionStatusText.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _clockTimer.Start();
        UpdateClockDisplay();
        _weatherRefreshTimer.Start();
        UpdateWeatherDisplay();
        _ = RefreshLocalAiStatusAsync();
        if (AppRuntime.Settings.UseAutomaticLocation &&
            AppRuntime.Settings.AutomaticLocationConsentConfirmed)
            _ = RefreshLocationWeatherAsync();
        else if (!AppRuntime.Settings.UseAutomaticLocation &&
                 !string.IsNullOrWhiteSpace(AppRuntime.Settings.WeatherLocation))
            _ = RefreshWeatherAsync();

        // Keep the initial window inside a usable work area on small/high-DPI displays.
        // WorkArea and WPF Width/Height are measured in device-independent units.
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
            return;

        var availableWidth = Math.Max(1d, workArea.Width - 32d);
        var availableHeight = Math.Max(1d, workArea.Height - 32d);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = Math.Clamp(Left, workArea.Left, workArea.Right - Width);
        Top = Math.Clamp(Top, workArea.Top, workArea.Bottom - Height);
        ApplyCompactWindowChrome(ActualWidth > 0 ? ActualWidth : Width);
        BottomDockShell.UpdateLayout();
        UpdateMainTabsNavigationMargin();
        UpdateExpanderHeaderAccessibility();
    }

    private void UpdateExpanderHeaderAccessibility()
    {
        var ar = IsArabic;

        var chatName = ar ? "أدوات المحادثة والمرفقات" : "Conversation tools and attachments";
        var chatHelp = ar
            ? "افتح هذه المنطقة للوصول إلى التقاط الشاشة والملفات والكاميرا والخصوصية والبريد."
            : "Expand this section to access screen capture, files, camera, privacy, and email tools.";
        ChatToolsHeaderText.Text = chatName;
        ChatToolsHeaderHintText.Text = ar ? "  •  أدوات اختيارية" : "  •  Optional tools";
        SetExpanderHeaderAccessibility(ChatToolsExpander, chatName, chatHelp);

        var taskName = ar ? "أدوات وإعدادات المهمة" : "Task tools and settings";
        var taskHelp = ar
            ? "أدوات Windows والتذكيرات والروتينات وإعدادات المتابعة الاستباقية."
            : "Windows tools, reminders, routines, and proactive check-in settings.";
        TaskToolsHeaderText.Text = taskName;
        TaskToolsHeaderHintText.Text = ar ? "  •  أدوات اختيارية" : "  •  Optional tools";
        SetExpanderHeaderAccessibility(TaskToolsExpander, taskName, taskHelp);
    }

    private static void SetExpanderHeaderAccessibility(Expander expander, string name, string helpText)
    {
        // WPF's default Expander template exposes its clickable HeaderSite as a separate Button
        // automation element. Naming the Expander alone does not populate that child's Name.
        AutomationProperties.SetName(expander, name);
        AutomationProperties.SetHelpText(expander, helpText);

        expander.ApplyTemplate();
        if (expander.Template?.FindName("HeaderSite", expander) is DependencyObject headerSite)
        {
            AutomationProperties.SetName(headerSite, name);
            AutomationProperties.SetHelpText(headerSite, helpText);
        }
    }

    private void MainWindow_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Windows raises this notification outside the WPF dispatcher. Reapply after system
        // accessibility/color changes so High Contrast works without restarting Layal.
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsLoaded && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                ApplyTheme();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _clipboardReadCancellation?.Cancel();
        _clockTimer.Stop();
        _weatherRefreshTimer.Stop();
        _reminderTimer.Stop();
        _proactiveCheckInTimer.Stop();
        _gameModeDetectionTimer.Stop();
        _clockTimer.Tick -= ClockTimer_Tick;
        _weatherRefreshTimer.Tick -= WeatherRefreshTimer_Tick;
        _reminderTimer.Tick -= ReminderTimer_Tick;
        _proactiveCheckInTimer.Tick -= ProactiveCheckInTimer_Tick;
        _gameModeDetectionTimer.Tick -= GameModeDetectionTimer_Tick;
        _automaticGameModeSessionActive = false;
        _lastAllowedForegroundProcessName = null;
        _gameModeAutoDismissalPolicy.Reset();
        AppRuntime.IsGameModeActive = false;
        _weatherCancellation?.Cancel();
        _hoveredElementInspectionCancellation?.Cancel();
        _documentExtractionCancellation?.Cancel();
        _speechRecordingTimer.Stop();
        _speechRecordingTimer.Tick -= SpeechRecordingTimer_Tick;
        _speechCancellation?.Cancel();
        _voiceTestCancellation?.Cancel();
        _chatCancellation?.Cancel();
        if (_cameraVisionWindow is not null)
        {
            _cameraVisionWindow.Close();
            _cameraVisionWindow = null;
        }
        _microphoneCapture.Dispose();
        _speechCancellation?.Dispose();
        _voiceTestCancellation?.Dispose();
        SystemEvents.UserPreferenceChanged -= MainWindow_UserPreferenceChanged;
        _globalHotkeyService?.Dispose();
        _quickAskWindow?.Close();
        _quickAskWindow = null;
        if (_gameModeHudWindow is not null)
        {
            var hud = _gameModeHudWindow;
            _gameModeHudWindow = null;
            _gameModeHudAutoLaunched = false;
            _gameModeAutoLaunchedProcessName = null;
            _automaticGameModeSessionActive = false;
            _lastAllowedForegroundProcessName = null;
            _gameModeAutoDismissalPolicy.Reset();
            AppRuntime.IsGameModeActive = false;
            hud.CloseFromDetection();
        }
        AppRuntime.OpenTaskNotification = null;
        Closed -= MainWindow_Closed;
    }

}
