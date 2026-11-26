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
    private void SetLanguage(string language)
    {
        if (!_incognitoMode)
            AppRuntime.ChatSessionService.SetDefaultTitle(language == "ar" ? "محادثة جديدة" : "New conversation");
        AppRuntime.Settings.Language = language;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        ChatHistorySelector.Items.Refresh();
        ApplyLanguage();
        AppRuntime.Tray?.SetLanguage(language);
    }

    private void QuickAskHotkeySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || QuickAskHotkeySelector.SelectedItem is not ComboBoxItem item)
            return;

        var preset = GlobalHotkeyService.NormalizePreset(item.Tag?.ToString());
        var previous = AppRuntime.Settings.QuickAskHotkeyPreset;
        if (string.Equals(previous, preset, StringComparison.Ordinal))
            return;

        AppRuntime.Settings.QuickAskHotkeyPreset = preset;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.QuickAskHotkeyPreset = previous;
            QuickAskHotkeySelector.SelectedItem = QuickAskHotkeySelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), previous, StringComparison.Ordinal));
            SetAccessibleStatusText(ProviderActionStatus, UiLocalization.FormatTechnicalError(
                "تعذر حفظ الاختصار", "Could not save the shortcut", ex, IsArabic));
            return;
        }

        if (!Layal.App.SuppressStartupForTests)
        {
            _globalHotkeyService?.Dispose();
            _globalHotkeyService = new GlobalHotkeyService(this, OpenQuickAskWindow, preset);
        }
        _gameModeHudWindow?.UpdateShortcutDisplay(GlobalHotkeyService.GetDisplayName(preset));
        UpdateQuickAskHotkeyStatus();
    }

    private void UpdateQuickAskHotkeyStatus()
    {
        if (QuickAskHotkeyStatus is null)
            return;
        var shortcut = GlobalHotkeyService.GetDisplayName(AppRuntime.Settings.QuickAskHotkeyPreset);
        QuickAskHotkeyStatus.Text = _globalHotkeyService?.IsRegistered == true
            ? (IsArabic ? "الاختصار مسجل: " : "Shortcut registered: ") + shortcut
            : (IsArabic
                ? "تعذر تسجيل الاختصار عالميًا؛ جرّب اختيار اختصار آخر أو استخدم زر السؤال السريع داخل ليال. الاختصار: "
                : "The global shortcut could not be registered. Choose another or use the in-app Quick Ask button. Shortcut: ") + shortcut;
        AutomationProperties.SetHelpText(QuickAskHotkeyStatus, QuickAskHotkeyStatus.Text);
    }

    private void ThemePresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ThemePresetSelector.SelectedItem is not ComboBoxItem item)
            return;

        var preset = item.Tag?.ToString() ?? "classic";
        if (preset is not ("classic" or "ocean" or "forest" or "sunset"))
            preset = "classic";
        if (string.Equals(AppRuntime.Settings.ThemePreset, preset, StringComparison.Ordinal))
            return;

        var previous = AppRuntime.Settings.ThemePreset;
        AppRuntime.Settings.ThemePreset = preset;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            ApplyTheme();
            SetChatActivityStatus(IsArabic
                ? "تم تطبيق مجموعة الألوان وحفظها محليًا."
                : "Theme preset applied and saved locally.");
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.ThemePreset = previous;
            ThemePresetSelector.SelectedItem = ThemePresetSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), previous, StringComparison.Ordinal));
            ApplyTheme();
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر حفظ مجموعة الألوان", "Could not save the theme preset", ex, IsArabic));
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        AppRuntime.Settings.ThemeMode = IsDarkTheme ? "light" : "dark";
        ApplyTheme();
        ApplyLanguage();
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        }
        catch
        {
            SetAccessibleStatusText(HomeResult, IsArabic
                ? "تم تغيير المظهر، لكن تعذر حفظ التفضيل."
                : "Theme changed, but the preference could not be saved.");
            HomeResult.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
    }

    /// <summary>
    /// Gives Escape one predictable cancellation path for the foreground AI operation.
    /// Cancellation is cooperative: the status text reports that cancellation was requested,
    /// while each operation's existing finally block owns cleanup and the final result.
    /// </summary>
    private bool TryCancelActiveOperationFromEscape()
    {
        if (_localAiSetupCancellation is not null)
        {
            _localAiSetupCancellation.Cancel();
            SetChatActivityStatus(IsArabic
                ? "تم طلب إلغاء إعداد الذكاء الاصطناعي المحلي."
                : "Local AI setup cancellation requested.");
            return true;
        }

        if (_voiceConversationCancellation is not null)
        {
            if (AppRuntime.Settings.UseLocalAi && _localVoiceRecordingActive)
                CancelLocalVoiceRecording();
            else
                _voiceConversationCancellation.Cancel();

            SetChatActivityStatus(IsArabic
                ? "تم طلب إيقاف المحادثة الصوتية."
                : "Voice conversation stop requested.");
            return true;
        }

        // Prefer the operation in the selected surface, then fall back to any remaining task.
        if (ReferenceEquals(MainTabs.SelectedItem, TasksTab) && _agentTaskCancellation is not null)
        {
            _agentTaskCancellation.Cancel();
            SetAccessibleStatusText(TaskResult, IsArabic
                ? "تم طلب إلغاء المهمة. جارٍ إيقاف الأداة الحالية بأمان..."
                : "Task cancellation requested. Safely stopping the current tool...");
            return true;
        }

        if (_chatCancellation is not null)
        {
            _chatCancellation.Cancel();
            SetChatActivityStatus(IsArabic
                ? "تم طلب إيقاف الرد أو التحليل الحالي."
                : "Current reply or analysis cancellation requested.");
            return true;
        }

        if (_agentTaskCancellation is not null)
        {
            _agentTaskCancellation.Cancel();
            SetAccessibleStatusText(TaskResult, IsArabic
                ? "تم طلب إلغاء المهمة. جارٍ إيقاف الأداة الحالية بأمان..."
                : "Task cancellation requested. Safely stopping the current tool...");
            return true;
        }

        if (_speechCancellation is not null)
        {
            _speechCancellation.Cancel();
            SetChatActivityStatus(IsArabic
                ? "تم طلب إلغاء معالجة التسجيل الصوتي."
                : "Speech-processing cancellation requested.");
            return true;
        }

        if (_speechIsStopping)
        {
            // StopRecordingAsync is finalizing its buffer and cannot be cancelled directly.
            // Do not allow that buffer to proceed into transcription or upload after Esc.
            _discardSpeechAudioOnStop = true;
            SetChatActivityStatus(IsArabic
                ? "تم طلب التخلص من التسجيل الجاري إيقافه؛ لن يُحوّل أو يُرسل."
                : "Discard requested for the recording being finalized; it will not be transcribed or uploaded.");
            return true;
        }

        if (_voiceTestCancellation is not null)
        {
            _voiceTestCancellation.Cancel();
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "تم طلب إيقاف اختبار الصوت."
                : "Voice preview stop requested.");
            return true;
        }

        if (_localVoiceRecordingActive)
        {
            CancelLocalVoiceRecording();
            return true;
        }

        if (_microphoneCapture.IsRecording && !_speechIsStopping)
        {
            try
            {
                _microphoneCapture.CancelRecording();
                SetChatActivityStatus(IsArabic
                    ? "تم إلغاء التسجيل الصوتي المحلي؛ لم يتم إرسال الصوت."
                    : "Local recording cancelled; audio was not sent.");
            }
            catch (InvalidOperationException)
            {
                // Recording may have completed between the state check and cancellation.
            }
            return true;
        }

        return false;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape &&
            TryCancelActiveOperationFromEscape())
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F1)
        {
            ShowKeyboardHelp();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        TabItem? requestedTab = e.Key switch
        {
            Key.D1 or Key.NumPad1 => ChatTab,
            Key.D2 or Key.NumPad2 => HomeTab,
            Key.D3 or Key.NumPad3 => TasksTab,
            Key.D4 or Key.NumPad4 => SkillsTab,
            Key.D5 or Key.NumPad5 => CamerasTab,
            Key.D6 or Key.NumPad6 => MemoryTab,
            Key.D7 or Key.NumPad7 => ApiIntegrationsTab,
            Key.D8 or Key.NumPad8 => SettingsTab,
            _ => null
        };

        if (requestedTab is null)
            return;

        MainTabs.SelectedItem = requestedTab;
        MainTabs.UpdateLayout();

        if (requestedTab == ChatTab)
        {
            FocusChatComposerAfterNavigation();
        }
        else if (MainTabs.SelectedContent is DependencyObject content &&
                 FindFirstFocusableControl(content) is { } firstControl &&
                 firstControl.Focus())
        {
            // Focus the first usable control in the newly selected tab.
        }
        else
        {
            requestedTab.Focus();
        }

        e.Handled = true;
    }

    private static Control? FindFirstFocusableControl(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Control control &&
                control is not ScrollViewer &&
                control.IsEnabled &&
                control.IsVisible &&
                control.Focusable &&
                control.IsTabStop)
            {
                return control;
            }

            var nested = FindFirstFocusableControl(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private void KeyboardHelpButton_Click(object sender, RoutedEventArgs e) => ShowKeyboardHelp();

    private void ShowKeyboardHelp()
    {
        var message = IsArabic
            ? "اختصارات لوحة المفاتيح\n\n" +
              "Ctrl+1 — المحادثة / الرئيسية\n" +
              "Ctrl+2 — نظرة عامة\n" +
              "Ctrl+3 — المهام\n" +
              "Ctrl+4 — المهارات\n" +
              "Ctrl+5 — الكاميرات\n" +
              "Ctrl+6 — الذاكرة\n" +
              "Ctrl+7 — تكاملات API\n" +
              "Ctrl+8 — الإعدادات\n" +
              "Alt+Shift+S — سؤال سريع من نافذة صغيرة\n" +
              "F1 — عرض هذه المساعدة\n\n" +
              "استخدم Tab للتنقل للأمام وShift+Tab للرجوع. استخدم الأسهم داخل قوائم التبويبات والقوائم، وSpace لتبديل مربعات الاختيار، والأسهم لتعديل أشرطة التمرير. في حقل المحادثة، Enter يرسل الرسالة."
            : "Keyboard shortcuts\n\n" +
              "Ctrl+1 — Home (Chat)\n" +
              "Ctrl+2 — Overview\n" +
              "Ctrl+3 — Tasks\n" +
              "Ctrl+4 — Skills\n" +
              "Ctrl+5 — Cameras\n" +
              "Ctrl+6 — Memory\n" +
              "Ctrl+7 — API integrations\n" +
              "Ctrl+8 — Settings\n" +
              "Alt+Shift+S — Open Quick Ask\n" +
              "F1 — Show this help\n\n" +
              "Use Tab to move forward and Shift+Tab to move backward. Use the arrow keys inside tab and list controls, Space to toggle checkboxes, and the arrow keys to adjust sliders. In the chat field, Enter sends the message.";

        LayalDialogService.Show(
            this,
            message,
            IsArabic ? "اختصارات لوحة المفاتيح" : "Keyboard shortcuts",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e) => SetLanguage(IsArabic ? "en" : "ar");
    private void ArabicLanguageButton_Click(object sender, RoutedEventArgs e) => SetLanguage("ar");
    private void EnglishLanguageButton_Click(object sender, RoutedEventArgs e) => SetLanguage("en");
    private void ShowMascotButton_Click(object sender, RoutedEventArgs e) => AppRuntime.MascotWindow?.ShowAgain();
    private void GoChatButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(ChatTab);
    private void GoSettingsButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(SettingsTab);

    private void NavigateToTab(TabItem tab)
    {
        MainTabs.SelectedItem = tab;
        UpdateDockSelection();
        if (ReferenceEquals(tab, ChatTab))
            FocusChatComposerAfterNavigation();
    }

    private void FocusChatComposerAfterNavigation()
    {
        void FocusComposer()
        {
            if (!ChatInput.IsEnabled || !ChatInput.IsVisible)
                return;

            if (!IsActive)
                Activate();

            MainTabs.UpdateLayout();
            ChatInput.UpdateLayout();
            ChatInput.BringIntoView();
            FocusManager.SetFocusedElement(this, ChatInput);
            Keyboard.Focus(ChatInput);
            ChatInput.Focus();
            ChatInput.CaretIndex = ChatInput.Text.Length;
        }

        // Selecting a TabItem replaces the content presenter asynchronously. Focus once now
        // and once after the current keyboard input has completed so WPF cannot restore focus
        // to the original Window source at the end of PreviewKeyDown processing.
        FocusComposer();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(FocusComposer));
    }

    private void DockChatButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(ChatTab);
    private void DockOverviewButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(HomeTab);
    private void DockTasksButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(TasksTab);
    private void DockSkillsButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(SkillsTab);
    private void DockCamerasButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(CamerasTab);
    private void DockMemoryButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(MemoryTab);
    private void DockApiButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(ApiIntegrationsTab);
    private void DockSettingsButton_Click(object sender, RoutedEventArgs e) => NavigateToTab(SettingsTab);

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // WPF can raise this event while InitializeComponent is still adding the first TabItem.
        // The dock is declared after MainTabs, so its named controls do not exist yet at that point.
        if (_isUiInitialized)
        {
            // The selected page changes inside a content-only template. Invalidate the peer
            // cache so screen readers and UI Automation clients see the newly selected page.
            UIElementAutomationPeer.FromElement(MainTabs)?.ResetChildrenCache();
            UpdateDockSelection();
            // SelectionChanged also bubbles up from lists and combo boxes inside the pages; only a real page change fades.
            if (ReferenceEquals(e.OriginalSource, MainTabs) && e.RemovedItems.Count > 0 && e.AddedItems.Count > 0)
                PlayPageTransition();
        }
    }

    // Short fade and 8 DIP rise for the new page. Skipped by CanAnimateUi (Windows animations off, high contrast,
    // performance mode). The animations are removed when they finish, so nothing keeps running.
    private void PlayPageTransition()
    {
        if (!CanAnimateUi)
        {
            ResetPageTransition();
            return;
        }

        if (MainTabs.RenderTransform is not TranslateTransform rise || rise.IsFrozen)
        {
            rise = new TranslateTransform();
            MainTabs.RenderTransform = rise;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        fade.Completed += (_, _) => ResetPageTransition();
        MainTabs.BeginAnimation(UIElement.OpacityProperty, fade);
        rise.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void ResetPageTransition()
    {
        MainTabs.BeginAnimation(UIElement.OpacityProperty, null);
        MainTabs.Opacity = 1;
        if (MainTabs.RenderTransform is TranslateTransform { IsFrozen: false } rise)
        {
            rise.BeginAnimation(TranslateTransform.YProperty, null);
            rise.Y = 0;
        }
    }

    private void UpdateDockSelection()
    {
        var selected = MainTabs.SelectedItem;
        var currentSection = ReferenceEquals(selected, ChatTab)
            ? (IsArabic ? "المحادثة / الرئيسية" : "Home (Chat)")
            : ReferenceEquals(selected, HomeTab)
                ? (IsArabic ? "نظرة عامة" : "Overview")
                : ReferenceEquals(selected, TasksTab)
                    ? (IsArabic ? "المهام" : "Tasks")
                    : ReferenceEquals(selected, SkillsTab)
                        ? (IsArabic ? "المهارات" : "Skills")
                        : ReferenceEquals(selected, CamerasTab)
                            ? (IsArabic ? "الكاميرات" : "Cameras")
                            : ReferenceEquals(selected, MemoryTab)
                                ? (IsArabic ? "الذاكرة" : "Memory")
                                : ReferenceEquals(selected, ApiIntegrationsTab)
                                    ? (IsArabic ? "تكاملات API" : "API integrations")
                                    : ReferenceEquals(selected, SettingsTab)
                                        ? (IsArabic ? "الإعدادات" : "Settings")
                                        : string.Empty;
        AutomationProperties.SetHelpText(MainTabs, IsArabic
            ? $"القسم الحالي: {currentSection}. استخدم شريط التنقل العائم أو Ctrl+1 إلى Ctrl+8 لاختيار الصفحة."
            : $"Current section: {currentSection}. Use the floating navigation bar or Ctrl+1 through Ctrl+8 to choose a page.");
        var activeForeground = ThemeBrush("Layal.OnAccent", Brushes.White);
        var inactiveForeground = ThemeBrush("Layal.Muted", Brushes.Gray);
        Button? activeButton = null;
        void Set(Button button, bool active)
        {
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Foreground = active ? activeForeground : inactiveForeground;
            button.Tag = active ? "active" : null;
            LiftDockButton(button, active);
            if (active)
                activeButton = button;
        }

        Set(DockChatButton, ReferenceEquals(selected, ChatTab));
        Set(DockOverviewButton, ReferenceEquals(selected, HomeTab));
        Set(DockTasksButton, ReferenceEquals(selected, TasksTab));
        Set(DockSkillsButton, ReferenceEquals(selected, SkillsTab));
        Set(DockCamerasButton, ReferenceEquals(selected, CamerasTab));
        Set(DockMemoryButton, ReferenceEquals(selected, MemoryTab));
        Set(DockApiButton, ReferenceEquals(selected, ApiIntegrationsTab));
        Set(DockSettingsButton, ReferenceEquals(selected, SettingsTab));
        MoveDockActiveBubble(activeButton, animate: true);
    }

    private void ApplyNavigationRailPresentation()
    {
        var ar = IsArabic;

        var destinations = new[]
        {
            (Button: DockChatButton, Name: "Chat", Ar: "الرئيسية", En: "Home", AccessibleAr: "الرئيسية - المحادثة", AccessibleEn: "Home - Chat"),
            (Button: DockOverviewButton, Name: "Overview", Ar: "نظرة عامة", En: "Overview", AccessibleAr: "نظرة عامة", AccessibleEn: "Overview"),
            (Button: DockTasksButton, Name: "Tasks", Ar: "المهام", En: "Tasks", AccessibleAr: "المهام", AccessibleEn: "Tasks"),
            (Button: DockSkillsButton, Name: "Skills", Ar: "المهارات", En: "Skills", AccessibleAr: "المهارات", AccessibleEn: "Skills"),
            (Button: DockCamerasButton, Name: "Cameras", Ar: "الكاميرات", En: "Cameras", AccessibleAr: "الكاميرات", AccessibleEn: "Cameras"),
            (Button: DockMemoryButton, Name: "Memory", Ar: "الذاكرة", En: "Memory", AccessibleAr: "الذاكرة", AccessibleEn: "Memory"),
            (Button: DockApiButton, Name: "Api", Ar: "تكاملات API", En: "API", AccessibleAr: "تكاملات API", AccessibleEn: "API integrations"),
            (Button: DockSettingsButton, Name: "Settings", Ar: "الإعدادات", En: "Settings", AccessibleAr: "الإعدادات", AccessibleEn: "Settings")
        };

        foreach (var destination in destinations)
        {
            var label = ar ? destination.Ar : destination.En;
            var iconPath = CreateDockIconPath(destination.Button,
                (Geometry)FindResource($"Layal.NavIcon.{destination.Name}"));
            var iconCanvas = new Canvas
            {
                Width = 24,
                Height = 24,
                IsHitTestVisible = false
            };
            iconCanvas.Children.Add(iconPath);
            destination.Button.Content = iconCanvas;
            destination.Button.ToolTip = label;
            AutomationProperties.SetName(destination.Button,
                ar ? destination.AccessibleAr : destination.AccessibleEn);
        }
    }

    private static System.Windows.Shapes.Path CreateDockIconPath(Button owner, Geometry geometry)
    {
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = geometry,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        iconPath.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new Binding(nameof(Button.Foreground)) { Source = owner });
        return iconPath;
    }


    private readonly Dictionary<Button, double> _dockLiftTargets = new();
    private double? _dockBubbleSlideTargetX;

    private void LiftDockButton(Button button, bool lifted)
    {
        if (button.RenderTransform is not TranslateTransform transform || transform.IsFrozen)
        {
            transform = new TranslateTransform();
            button.RenderTransform = transform;
        }

        var target = lifted ? _dockActiveLift : 0d;
        // Same target as the last request: do not restart a running animation (language/theme/size refreshes call this too).
        if (_dockLiftTargets.TryGetValue(button, out var lastTarget) && Math.Abs(lastTarget - target) < 0.01)
            return;
        _dockLiftTargets[button] = target;
        if (!CanAnimateUi || DockActiveBubble.Visibility != Visibility.Visible)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = target;
            return;
        }

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // One shared bubble slides under the selected destination. Animation is skipped when Windows
    // animations are off, in high contrast, or in performance mode (see CanAnimateUi).
    private void MoveDockActiveBubble(Button? target, bool animate)
    {
        _activeDockButton = target;
        if (target is null)
        {
            foreach (var button in DockNavigationItems.Children.OfType<Button>())
            {
                if (button.Content is UIElement iconCanvas)
                    iconCanvas.Opacity = 1;
            }
            DockActiveBubble.Visibility = Visibility.Hidden;
            return;
        }

        if (DockNavigationLayer.ActualWidth <= 0 || !DockNavigationLayer.IsAncestorOf(target))
            return;

        if (target.Content is Canvas targetIconCanvas &&
            targetIconCanvas.Children.OfType<System.Windows.Shapes.Path>().FirstOrDefault() is { } sourceIcon)
        {
            DockActiveIconCanvas.Children.Clear();
            DockActiveIconCanvas.Children.Add(CreateDockIconPath(target, sourceIcon.Data));
            targetIconCanvas.Opacity = 0;
        }

        // Use the element's center, not its local origin: in RTL flow WPF mirrors the button's
        // local coordinate system, so (0, 0) can map to the opposite edge of the visual slot.
        var targetCenterX = target.TransformToAncestor(DockNavigationLayer)
            .Transform(new Point(target.ActualWidth / 2d, target.ActualHeight / 2d)).X;
        var x = targetCenterX - DockActiveBubble.Width / 2d;
        if (DockActiveBubble.RenderTransform is not TranslateTransform transform || transform.IsFrozen)
        {
            transform = new TranslateTransform(0, _dockActiveLift);
            DockActiveBubble.RenderTransform = transform;
        }

        var firstPlacement = DockActiveBubble.Visibility != Visibility.Visible;
        if (!animate || firstPlacement || !CanAnimateUi)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = x;
            _dockBubbleSlideTargetX = x;
            ShowInactiveDockIcons(target);
        }
        else if (_dockBubbleSlideTargetX is { } runningX && Math.Abs(runningX - x) < 0.5)
        {
            // Already sliding to (or resting at) this slot: leave the running animation alone.
        }
        else
        {
            _dockBubbleSlideTargetX = x;
            var slide = new DoubleAnimation(x, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            slide.Completed += (_, _) =>
            {
                if (ReferenceEquals(_activeDockButton, target))
                    ShowInactiveDockIcons(target);
            };
            transform.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        transform.Y = _dockActiveLift;
        DockActiveBubble.Visibility = Visibility.Visible;
    }

    private void ShowInactiveDockIcons(Button activeButton)
    {
        foreach (var button in DockNavigationItems.Children.OfType<Button>())
        {
            if (!ReferenceEquals(button, activeButton) && button.Content is UIElement iconCanvas)
                iconCanvas.Opacity = 1;
        }
    }

    private bool CanAnimateUi => !SystemParameters.HighContrast &&
                                 SystemParameters.ClientAreaAnimation &&
                                 !AppRuntime.Settings.PerformanceModeEnabled;

    private void ToggleChatHistoryButton_Click(object sender, RoutedEventArgs e) => SetChatHistoryPanelOpen(!_isChatHistoryPanelOpen);
    private void CloseChatHistoryButton_Click(object sender, RoutedEventArgs e) => SetChatHistoryPanelOpen(false);

    private void SetChatHistoryPanelOpen(bool open)
    {
        if (open == _isChatHistoryPanelOpen &&
            (open ? ChatHistoryPanel.Visibility == Visibility.Visible : ChatHistoryPanel.Visibility == Visibility.Collapsed))
            return;

        _isChatHistoryPanelOpen = open;
        AutomationProperties.SetName(ToggleChatHistoryButton, open
            ? (IsArabic ? "إخفاء سجل المحادثات" : "Hide chat history")
            : (IsArabic ? "إظهار سجل المحادثات" : "Show chat history"));
        ChatHistoryPanel.BeginAnimation(UIElement.OpacityProperty, null);
        ChatHistoryPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
        if (open)
        {
            ChatHistoryPanel.Visibility = Visibility.Visible;
            ChatHistoryPanelTransform.X = IsArabic ? 10 : -10;
            if (!CanAnimateUi)
            {
                ChatHistoryPanel.Opacity = 1;
                ChatHistoryPanelTransform.X = 0;
                return;
            }

            ChatHistoryPanel.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170));
            fade.Completed += (_, _) =>
            {
                ChatHistoryPanel.BeginAnimation(UIElement.OpacityProperty, null);
                ChatHistoryPanel.Opacity = 1;
            };
            var slide = new DoubleAnimation(ChatHistoryPanelTransform.X, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            slide.Completed += (_, _) =>
            {
                ChatHistoryPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
                ChatHistoryPanelTransform.X = 0;
            };
            ChatHistoryPanel.BeginAnimation(UIElement.OpacityProperty, fade);
            ChatHistoryPanelTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        }
        else
        {
            if (ChatHistoryPanel.Visibility == Visibility.Collapsed)
                return;
            if (!CanAnimateUi)
            {
                ChatHistoryPanel.Opacity = 0;
                ChatHistoryPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var fade = new DoubleAnimation(ChatHistoryPanel.Opacity, 0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) =>
            {
                ChatHistoryPanel.BeginAnimation(UIElement.OpacityProperty, null);
                ChatHistoryPanel.Opacity = 0;
                ChatHistoryPanel.Visibility = Visibility.Collapsed;
            };
            var slide = new DoubleAnimation(ChatHistoryPanelTransform.X, IsArabic ? 10 : -10, TimeSpan.FromMilliseconds(150));
            ChatHistoryPanel.BeginAnimation(UIElement.OpacityProperty, fade);
            ChatHistoryPanelTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        }
    }

}
