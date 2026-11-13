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
using Microsoft.Win32;

namespace Layal.Views;

public partial class MainWindow
{
    private void ImportSkillButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = IsArabic ? "اختيار حزمة مهارة ZIP" : "Select skill ZIP package",
            Filter = IsArabic ? "حزم المهارات (*.zip)|*.zip|كل الملفات (*.*)|*.*" : "Skill packages (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            SkillRecord record;
            try { record = AppRuntime.SkillPackageService.ImportZip(dialog.FileName, overwrite: false); }
            catch (SkillAlreadyExistsException duplicate)
            {
                var answer = LayalDialogService.Show(
                    IsArabic ? $"توجد مهارة باسم {duplicate.SkillName}. هل تريد استبدال الحزمة الحالية؟" : $"A skill named {duplicate.SkillName} exists. Replace its package?",
                    IsArabic ? "مهارة موجودة" : "Skill already exists", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                record = AppRuntime.SkillPackageService.ImportZip(dialog.FileName, overwrite: true);
            }
            RefreshSkills();
            SetAccessibleStatusText(SkillsResult, (IsArabic ? "تم استيراد بيانات المهارة: " : "Skill metadata imported: ") + record.Name);
            SkillsResult.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(SkillsResult, UiLocalization.FormatTechnicalError("تعذر استيراد المهارة", "Skill import failed", ex, IsArabic));
            SkillsResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private void RefreshSkillButton_Click(object sender, RoutedEventArgs e) => RefreshSkills();
    private void RefreshSkillsButton_Click(object sender, RoutedEventArgs e) => RefreshSkills();

    private void RefreshSkills()
    {
        if (SkillsList is null) return;
        InstalledSkills.Clear();
        foreach (var record in AppRuntime.SkillPackageService.GetInstalled().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            InstalledSkills.Add(record);
    }

    private async void ImportGitHubSkillButton_Click(object sender, RoutedEventArgs e)
    {
        var url = GitHubSkillUrlInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            SetAccessibleStatusText(SkillsResult, IsArabic ? "أدخل رابط GitHub أولًا." : "Enter a GitHub URL first.");
            SkillsResult.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            return;
        }
        ImportGitHubSkillButton.IsEnabled = false;
        SetAccessibleStatusText(SkillsResult, IsArabic ? "جارٍ تنزيل المستودع وفحص ملفات المهارة..." : "Downloading repository and validating skill files...");
        SkillsResult.Foreground = ThemeBrush("Layal.Cyan", System.Windows.Media.Brushes.SteelBlue);
        try
        {
            SkillRecord record;
            try { record = await AppRuntime.SkillPackageService.ImportFromGitHubAsync(url, overwrite: false); }
            catch (SkillAlreadyExistsException duplicate)
            {
                var answer = LayalDialogService.Show(this,
                    IsArabic ? $"توجد مهارة باسم {duplicate.SkillName}. هل تريد استبدال الحزمة الحالية؟" : $"A skill named {duplicate.SkillName} exists. Replace the existing package?",
                    IsArabic ? "مهارة موجودة" : "Skill already exists", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                record = await AppRuntime.SkillPackageService.ImportFromGitHubAsync(url, overwrite: true);
            }
            RefreshSkills();
            SetAccessibleStatusText(SkillsResult, (IsArabic ? "تم استيراد المهارة: " : "Imported skill: ") + record.Name + Environment.NewLine + record.Description);
            SkillsResult.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
            GitHubSkillUrlInput.Clear();
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(SkillsResult, UiLocalization.FormatTechnicalError("تعذر استيراد المهارة من GitHub", "GitHub skill import failed", ex, IsArabic));
            SkillsResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
        finally { ImportGitHubSkillButton.IsEnabled = true; }
    }

    private void ManageRemindersButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateTaskWorkspaceAccessBlocked()) return;
        var window = new ReminderManagerWindow(AppRuntime.ReminderService, IsArabic, AppRuntime.ReminderTaskSchedulerService, AppRuntime.TaskHistoryService)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void GameModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameModeHudWindow is { IsVisible: true })
        {
            _gameModeHudWindow.CloseByUser();
            return;
        }

        OpenGameModeHud(automaticallyLaunched: false);
    }

    private void ConfigureGameModeDetectionButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = AppRuntime.Settings.Clone();
        var window = new GameModeSettingsWindow(draft, updated =>
        {
            var previous = AppRuntime.Settings.Clone();
            AppRuntime.Settings.CopyFrom(updated);
            try
            {
                AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            }
            catch
            {
                AppRuntime.Settings.CopyFrom(previous);
                throw;
            }
        }, IsArabic)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
            return;

        var enabled = AppRuntime.Settings.GameModeAutoDetectionEnabled &&
            AppRuntime.Settings.GameModeAllowedProcessNames.Count > 0;
        _gameModeAutoDismissalPolicy.Reset();
        _lastAllowedForegroundProcessName = null;
        _automaticGameModeSessionActive = false;
        if (enabled)
        {
            _gameModeDetectionTimer.Start();
            GameModeDetectionTimer_Tick(this, EventArgs.Empty);
            SetChatActivityStatus(IsArabic
                ? "تم تفعيل اكتشاف الألعاب بقائمة السماح. يفحص ليال اسم عملية النافذة الأمامية فقط كل 3 ثوانٍ."
                : "Allowlisted game detection is enabled. Layal checks only the foreground process name every 3 seconds.");
        }
        else
        {
            _gameModeDetectionTimer.Stop();
            if (_gameModeHudAutoLaunched)
                _gameModeHudWindow?.CloseFromDetection();
            UpdateGameModeActivityState();
            SetChatActivityStatus(IsArabic
                ? "تم إيقاف اكتشاف الألعاب التلقائي؛ يظل وضع الألعاب اليدوي متاحًا."
                : "Automatic game detection is disabled; manual game mode remains available.");
        }
    }

    private void GameModeDetectionTimer_Tick(object? sender, EventArgs e)
    {
        var allowlist = AppRuntime.Settings.GameModeAllowedProcessNames;
        if (!AppRuntime.Settings.GameModeAutoDetectionEnabled ||
            allowlist.Count == 0 ||
            AppRuntime.Settings.PrivacyShieldEnabled ||
            AppRuntime.IsExiting)
        {
            _gameModeAutoDismissalPolicy.Reset();
            _lastAllowedForegroundProcessName = null;
            _automaticGameModeSessionActive = false;
            if (_gameModeHudAutoLaunched)
                _gameModeHudWindow?.CloseFromDetection();
            UpdateGameModeActivityState();
            return;
        }

        // Read one foreground process name only. No process list, title, arguments, memory,
        // input, hooks, or game telemetry are queried by this timer.
        var foregroundProcessName = _foregroundGameDetectionService.GetForegroundProcessName();
        var isAllowedForeground = _gameModeAutoDismissalPolicy.IsAllowedForeground(
            foregroundProcessName,
            allowlist);
        _automaticGameModeSessionActive = isAllowedForeground;
        _lastAllowedForegroundProcessName = isAllowedForeground ? foregroundProcessName : null;
        if (_gameModeHudAutoLaunched && isAllowedForeground)
            _gameModeAutoLaunchedProcessName = foregroundProcessName;

        // Manual HUD ownership wins over automatic display, but an allowed foreground game
        // still suppresses proactive check-ins until focus leaves that game.
        if (_gameModeHudWindow is { IsVisible: true } && !_gameModeHudAutoLaunched)
        {
            UpdateGameModeActivityState();
            return;
        }

        var shouldShowAutomaticHud = isAllowedForeground &&
            !_gameModeAutoDismissalPolicy.IsDismissedFor(foregroundProcessName);
        if (shouldShowAutomaticHud)
        {
            if (_gameModeHudWindow is not { IsVisible: true })
                OpenGameModeHud(automaticallyLaunched: true, foregroundProcessName);
        }
        else if (_gameModeHudAutoLaunched)
        {
            _gameModeHudWindow?.CloseFromDetection();
        }

        UpdateGameModeActivityState();
    }

    private void UpdateGameModeActivityState()
    {
        // A dismissed automatic HUD does not end the detected game session. This keeps
        // proactive check-ins paused until the foreground process leaves the allowlist.
        var active = _automaticGameModeSessionActive || _gameModeHudWindow is { IsVisible: true };
        if (AppRuntime.IsGameModeActive != active)
        {
            AppRuntime.IsGameModeActive = active;
            TrySynchronizeLocalNotificationSchedule();
        }
        UpdateGameModeButtonUi();
    }

    private void OpenGameModeHud(bool automaticallyLaunched, string? foregroundProcessName = null)
    {
        if (_gameModeHudWindow is { IsVisible: true })
            return;

        _gameModeHudAutoLaunched = automaticallyLaunched;
        _gameModeAutoLaunchedProcessName = automaticallyLaunched ? foregroundProcessName : null;
        var hud = new GameModeHudWindow(IsArabic, () =>
        {
            if (_agentTaskCancellation is not null)
                return IsArabic ? "جارٍ تنفيذ مهمة." : "A task is running.";
            if (_chatCancellation is not null)
                return IsArabic ? "جارٍ تجهيز رد." : "A chat response is active.";
            return IsArabic ? "ليال جاهزة عند الطلب." : "Layal is available on demand.";
        },
        shortcutDisplay: GlobalHotkeyService.GetDisplayName(AppRuntime.Settings.QuickAskHotkeyPreset),
        automaticallyLaunched: automaticallyLaunched);
        // Keep the HUD independent from the main window so minimizing Layal does not
        // automatically minimize the overlay. It is explicitly closed during shutdown.
        _gameModeHudWindow = hud;
        hud.ExitRequested += (_, _) =>
        {
            if (!ReferenceEquals(_gameModeHudWindow, hud))
                return;

            var processToDismiss = _gameModeHudAutoLaunched
                ? _gameModeAutoLaunchedProcessName
                : _lastAllowedForegroundProcessName;
            if (hud.WasDismissedByUser && processToDismiss is not null)
                _gameModeAutoDismissalPolicy.DismissCurrentForeground(processToDismiss);

            _gameModeHudWindow = null;
            _gameModeHudAutoLaunched = false;
            _gameModeAutoLaunchedProcessName = null;
            UpdateGameModeActivityState();
            SetChatActivityStatus(_automaticGameModeSessionActive
                ? (IsArabic
                    ? "تم إخفاء HUD. ما زالت اللعبة المسموح بها في المقدمة؛ تظل المتابعة الاستباقية متوقفة حتى مغادرة اللعبة."
                    : "HUD hidden. The allowlisted game remains in front; proactive check-ins stay paused until focus leaves it.")
                : (IsArabic
                    ? "انتهى وضع الألعاب؛ استؤنفت المتابعة الاستباقية وفق إعداداتها."
                    : "Game mode ended; proactive check-ins resumed according to settings."));
        };
        hud.Show();
        UpdateGameModeActivityState();
        SetChatActivityStatus(automaticallyLaunched
            ? (IsArabic
                ? "تم فتح HUD تلقائيًا لتطبيق في قائمة السماح. لا تُقرأ عناوين النوافذ أو محتويات اللعبة."
                : "The HUD opened for an allowlisted foreground app. Window titles and game contents are not inspected.")
            : (IsArabic
                ? "وضع الألعاب اليدوي نشط. لا تُفحص الألعاب أو العمليات خارج قائمة السماح."
                : "Manual game mode is active. Games and processes outside the allowlist are not inspected."));
    }

    private void UpdateGameModeButtonUi()
    {
        if (GameModeButton is null)
            return;

        var hudVisible = _gameModeHudWindow is { IsVisible: true };
        var autoGameActiveWithoutHud = _automaticGameModeSessionActive && !hudVisible;
        GameModeButton.Content = hudVisible
            ? (IsArabic ? "إنهاء وضع الألعاب" : "Hide game HUD")
            : autoGameActiveWithoutHud
                ? (IsArabic ? "إظهار HUD" : "Show HUD")
                : (IsArabic ? "وضع الألعاب" : "Game mode");
        GameModeButton.ToolTip = hudVisible
            ? (IsArabic
                ? "إخفاء HUD. إذا كانت اللعبة المسموح بها ما زالت في المقدمة، تظل المتابعة الاستباقية متوقفة. التذكيرات التي أنشأتها تستمر كالمعتاد."
                : "Hide the HUD. If the allowlisted game remains in front, proactive check-ins stay paused. Your saved reminders continue normally.")
            : autoGameActiveWithoutHud
                ? (IsArabic
                    ? "تم التعرف على لعبة مسموح بها. اضغط لإظهار HUD؛ تظل المتابعة الاستباقية متوقفة حتى مغادرة اللعبة."
                    : "An allowlisted game is active. Show the HUD; proactive check-ins remain paused until focus leaves the game.")
                : (IsArabic
                    ? "تشغيل HUD يدوي. الكشف التلقائي خيار منفصل ومغلق افتراضيًا."
                    : "Open the HUD manually. Automatic detection is separate and off by default.");
        AutomationProperties.SetName(GameModeButton, hudVisible
            ? (IsArabic ? "إخفاء HUD وإنهاء العرض" : "Hide game HUD")
            : autoGameActiveWithoutHud
                ? (IsArabic ? "إظهار HUD للعبة المكتشفة" : "Show HUD for detected game")
                : (IsArabic ? "تفعيل وضع الألعاب اليدوي" : "Enable manual game mode"));
        AutomationProperties.SetHelpText(GameModeButton, IsArabic
            ? "HUD يعرض الوقت وحالة ليال والاختصار العام للسؤال السريع. لا يقرأ بيانات اللعبة ولا يحقن إدخالًا. الكشف التلقائي اختياري بقائمة سماح. الإخفاء لا يستأنف المتابعة الاستباقية ما دامت اللعبة المسموح بها في المقدمة."
            : "The HUD shows the clock, Layal status, and Quick Ask hotkey. It does not inspect game data or inject input. Automatic detection is optional and allowlist-only. Hiding the HUD does not resume proactive check-ins while the allowed game remains in front.");
    }

    private void WorkspaceHubButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateTaskWorkspaceAccessBlocked() || IsPrivateMemoryAccessBlocked()) return;
        try
        {
            var window = new WorkspaceHubWindow(
                AppRuntime.TaskHistoryService,
                AppRuntime.MemoryService,
                AppRuntime.WorkspaceRoot,
                IsArabic,
                taskId =>
                {
                    MainTabs.SelectedItem = TasksTab;
                    RefreshRecentTasks(taskId);
                    SetAccessibleStatusText(TaskResult, IsArabic
                        ? "تم فتح سجل المهمة المحددة من مساحة العمل."
                        : "Opened the selected task record from the workspace hub.");
                },
                memoryId =>
                {
                    if (!ConfirmDiscardMemoryDraft()) return;
                    MainTabs.SelectedItem = MemoryTab;
                    MemorySearchInput.Clear();
                    MemoryCategoryFilter.SelectedIndex = 0;
                    var note = AppRuntime.MemoryService.GetAll(includeArchived: true).FirstOrDefault(item =>
                        string.Equals(item.Id, memoryId, StringComparison.Ordinal));
                    if (note is not null)
                    {
                        if (note.IsArchived) MemoryShowArchivedCheckBox.IsChecked = true;
                        RefreshMemoryNotes();
                        note = AppRuntime.MemoryService.GetAll(includeArchived: true).FirstOrDefault(item =>
                            string.Equals(item.Id, memoryId, StringComparison.Ordinal)) ?? note;
                        MemoryList.SelectedItem = MemoryNotes.FirstOrDefault(item =>
                            string.Equals(item.Id, note.Id, StringComparison.Ordinal));
                        LoadMemoryEditor(note);
                        SetAccessibleStatusText(MemoryResult, IsArabic
                            ? "تم فتح الملاحظة من مساحة العمل المحلية."
                            : "Opened the note from the local workspace hub.");
                        MemoryTitleInput.Focus();
                    }
                })
            {
                Owner = this
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTechnicalError(
                "تعذر فتح مساحة العمل", "Could not open the workspace hub", ex, IsArabic));
        }
    }

    private void ManageRoutinesButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateTaskWorkspaceAccessBlocked()) return;
        var window = new SavedRoutineManagerWindow(
            AppRuntime.SavedRoutineService,
            IsArabic,
            goal =>
            {
                TaskInput.Text = goal;
                MainTabs.SelectedItem = TasksTab;
                TaskInput.Focus();
                SetAccessibleStatusText(TaskResult, IsArabic
                    ? "تم تحميل قالب الروتين في مسودة المهمة. راجع الهدف ثم اضغط تنفيذ المهمة."
                    : "The saved routine was loaded into the task draft. Review it, then press Run task.");
            })
        {
            Owner = this
        };
        window.ShowDialog();
        if (!string.IsNullOrWhiteSpace(window.RunPrompt))
        {
            if (_agentTaskCancellation is not null)
            {
                SetAccessibleStatusText(TaskResult, IsArabic
                    ? "هناك مهمة قيد التنفيذ بالفعل؛ لم يبدأ هذا الروتين. أوقف المهمة الحالية أو انتظر انتهائها ثم حاول مجددًا."
                    : "A task is already running; this routine was not started. Cancel or finish the current task, then try again.");
                return;
            }

            TaskInput.Text = window.RunPrompt;
            MainTabs.SelectedItem = TasksTab;
            RunAgentTaskButton_Click(RunAgentTaskButton, new RoutedEventArgs(Button.ClickEvent, RunAgentTaskButton));
        }
    }

    private void ProactiveCheckInEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (IsPrivateTaskWorkspaceAccessBlocked())
        {
            ProactiveCheckInEnabledCheckBox.IsChecked = AppRuntime.Settings.ProactiveCheckInEnabled;
            ProactiveCheckInEnabledCheckBox.IsEnabled = false;
            return;
        }

        var previousEnabled = AppRuntime.Settings.ProactiveCheckInEnabled;
        var previousLastNotifiedAt = AppRuntime.Settings.LastProactiveCheckInAt;
        AppRuntime.Settings.ProactiveCheckInEnabled = ProactiveCheckInEnabledCheckBox.IsChecked == true;
        AppRuntime.Settings.LastProactiveCheckInAt = DateTimeOffset.Now;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            if (AppRuntime.Settings.ProactiveCheckInEnabled)
                _proactiveCheckInTimer.Start();
            else
                _proactiveCheckInTimer.Stop();
            TrySynchronizeLocalNotificationSchedule();

            SetChatActivityStatus(AppRuntime.Settings.ProactiveCheckInEnabled
                ? (IsArabic
                    ? "تم تفعيل تذكيرات المتابعة المحلية؛ لا يوجد فحص للشاشة أو استدعاء AI في الخلفية."
                    : "Local check-in reminders are enabled; there is no screen monitoring or background AI call.")
                : (IsArabic ? "تم إيقاف تذكيرات المتابعة." : "Check-in reminders are disabled."));
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.ProactiveCheckInEnabled = previousEnabled;
            AppRuntime.Settings.LastProactiveCheckInAt = previousLastNotifiedAt;
            ProactiveCheckInEnabledCheckBox.IsChecked = previousEnabled;
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر حفظ تذكيرات المتابعة", "Could not save check-in reminder settings", ex, IsArabic));
        }
    }

    private void ProactiveCheckInIntervalSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringProactiveCheckInInterval || !IsLoaded)
            return;

        if (IsPrivateTaskWorkspaceAccessBlocked())
        {
            _restoringProactiveCheckInInterval = true;
            try
            {
                ProactiveCheckInIntervalSelector.SelectedItem =
                    ProactiveCheckInIntervalSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
                        string.Equals(candidate.Tag?.ToString(), AppRuntime.Settings.ProactiveCheckInIntervalMinutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
            finally { _restoringProactiveCheckInInterval = false; }
            return;
        }

        if (ProactiveCheckInIntervalSelector.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return;

        var previousInterval = AppRuntime.Settings.ProactiveCheckInIntervalMinutes;
        var previousLastNotifiedAt = AppRuntime.Settings.LastProactiveCheckInAt;
        AppRuntime.Settings.ProactiveCheckInIntervalMinutes = minutes;
        AppRuntime.Settings.LastProactiveCheckInAt = DateTimeOffset.Now;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            TrySynchronizeLocalNotificationSchedule();
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.ProactiveCheckInIntervalMinutes = previousInterval;
            AppRuntime.Settings.LastProactiveCheckInAt = previousLastNotifiedAt;
            _restoringProactiveCheckInInterval = true;
            try
            {
                ProactiveCheckInIntervalSelector.SelectedItem =
                    ProactiveCheckInIntervalSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
                        string.Equals(candidate.Tag?.ToString(), previousInterval.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
            finally
            {
                _restoringProactiveCheckInInterval = false;
            }
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر حفظ الفاصل الزمني", "Could not save the check-in interval", ex, IsArabic));
        }
    }

    private void TrySynchronizeLocalNotificationSchedule()
    {
        try
        {
            AppRuntime.SynchronizeLocalNotifications();
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر تحديث جدولة التذكيرات في Windows",
                "Windows local notification scheduling could not be updated",
                ex,
                IsArabic));
        }
    }

    private void ProactiveCheckInTimer_Tick(object? sender, EventArgs e)
    {
        if (!PrivateChatModePolicy.CanDeliverLocalNotifications(_incognitoMode))
            return;

        var now = DateTimeOffset.Now;
        var last = AppRuntime.Settings.LastProactiveCheckInAt;
        if (last is null)
        {
            if (AppRuntime.IsExiting || !AppRuntime.Settings.ProactiveCheckInEnabled)
                return;
            AppRuntime.Settings.LastProactiveCheckInAt = now;
            try
            {
                AppRuntime.SettingsStore.Save(AppRuntime.Settings);
                TrySynchronizeLocalNotificationSchedule();
            }
            catch { _proactiveCheckInTimer.Stop(); }
            return;
        }

        if (!ProactiveCheckInPolicy.ShouldNotify(
                enabled: AppRuntime.Settings.ProactiveCheckInEnabled,
                privacyShieldEnabled: AppRuntime.Settings.PrivacyShieldEnabled,
                gameModeActive: AppRuntime.IsGameModeActive,
                applicationExiting: AppRuntime.IsExiting,
                lastNotifiedAt: last,
                now: now,
                intervalMinutes: AppRuntime.Settings.ProactiveCheckInIntervalMinutes))
            return;

        // Persist the next time before notifying. If the machine wakes after several missed
        // intervals, only one notification is issued; overlapping ticks cannot create a storm.
        AppRuntime.Settings.LastProactiveCheckInAt = now;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        }
        catch (Exception ex)
        {
            _proactiveCheckInTimer.Stop();
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر حفظ حالة تذكير المتابعة؛ أُوقف المؤقت", "Could not save check-in state; the timer was stopped", ex, IsArabic));
            return;
        }

        TrySynchronizeLocalNotificationSchedule();

        // The check-in is useful even if task history cannot be read. Keep the notification
        // generic on storage failure rather than losing the due check-in or escaping the timer.
        var message = IsArabic
            ? "موعد مراجعة قصيرة. افتح ليال وحدد ما تريد التركيز عليه الآن."
            : "Time for a quick check-in. Open Layal and choose what to focus on now.";
        string? taskId = null;
        try
        {
            var recentTask = AppRuntime.TaskHistoryService.GetRecent(1).FirstOrDefault();
            if (recentTask is not null)
            {
                message = IsArabic
                    ? "موعد مراجعة المهمة. افتح ليال لمراجعة التقدم أو تحديث الهدف."
                    : "Time to check in on your task. Open Layal to review progress or update the goal.";
                taskId = recentTask.Id;
            }
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر قراءة سجل المهام؛ سيظهر تذكير متابعة عام",
                "Task history could not be read; a generic check-in will be shown",
                ex,
                IsArabic));
        }

        try
        {
            if (AppRuntime.Tray is { } tray)
                tray.ShowReminderNotification(
                    message,
                    IsArabic,
                    taskId is null ? null : () => OpenTaskFromNotification(taskId));
            else
                SetChatActivityStatus(message);
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر عرض إشعار المتابعة",
                "The check-in notification could not be displayed",
                ex,
                IsArabic));
        }
    }

    private void OpenTaskFromNotification(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (AppRuntime.IsExiting)
                return;
            if (!PrivateChatModePolicy.CanAccessTaskWorkspace(_incognitoMode))
            {
                SetChatActivityStatus(IsArabic
                    ? "افتح الوضع الخاص أولًا قبل الرجوع إلى المهمة المرتبطة بالإشعار."
                    : "Exit private chat before opening the task linked to this notification.");
                return;
            }

            ShowFromTray();
            MainTabs.SelectedItem = TasksTab;
            RefreshRecentTasks(taskId);
            if (RecentTasksList.SelectedItem is null)
            {
                TaskEvents.Clear();
                _rawTaskEvents.Clear();
                SetAccessibleStatusText(TaskResult,
                    IsArabic
                        ? "المهمة المرتبطة بالإشعار لم تعد موجودة في السجل؛ لم يتم اختيار مهمة بديلة."
                        : "The task linked to this notification is no longer in history; no replacement task was selected.");
                TaskResult.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
                return;
            }

            RecentTasksList.Focus();
            SetChatActivityStatus(IsArabic
                ? "تم فتح المهمة المرتبطة بإشعار المتابعة؛ لم يتم تشغيل أي إجراء."
                : "Opened the task associated with the check-in; no action was executed.");
        }));
    }

    private void ReminderTimer_Tick(object? sender, EventArgs e)
    {
        if (AppRuntime.IsExiting ||
            !PrivateChatModePolicy.CanDeliverLocalNotifications(_incognitoMode))
            return;

        var now = DateTimeOffset.Now;
        var scheduleChanged = false;
        var deliveredReminders = new List<ReminderRecord>();
        try
        {
            foreach (var due in AppRuntime.ReminderService.GetDue(now))
            {
                // Persist the next schedule (or deactivate a one-shot item) before notifying.
                // A stale/overlapping tick can therefore never deliver the same due instant twice.
                var delivered = AppRuntime.ReminderService.MarkDelivered(due.Id, due.DueAt, now);
                if (delivered is null)
                    continue;
                scheduleChanged = true;
                deliveredReminders.Add(delivered);
            }
        }
        catch (Exception ex)
        {
            // Avoid a notification storm if the protected store becomes unavailable.
            _reminderTimer.Stop();
            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTechnicalError(
                "تعذر فحص التذكيرات؛ تم إيقاف المؤقت لحماية السجل",
                "Could not check reminders; the timer was stopped to protect the saved history",
                ex,
                IsArabic));
            return;
        }

        try
        {
            if (deliveredReminders.Count == 1 && AppRuntime.Tray is { } singleTray)
            {
                var delivered = deliveredReminders[0];
                var linkedTaskId = delivered.RelatedTaskId;
                singleTray.ShowReminderNotification(
                    delivered.Title,
                    IsArabic,
                    string.IsNullOrWhiteSpace(linkedTaskId)
                        ? null
                        : () => OpenTaskFromNotification(linkedTaskId));
            }
            else if (deliveredReminders.Count > 1 && AppRuntime.Tray is { } digestTray)
            {
                var uniqueTaskId = ReminderTaskLinkPolicy.GetUniqueLinkedTaskId(deliveredReminders);
                digestTray.ShowReminderSummary(
                    deliveredReminders.Count,
                    IsArabic,
                    uniqueTaskId is null
                        ? () => ShowFromTray()
                        : () => OpenTaskFromNotification(uniqueTaskId));
            }
            else if (deliveredReminders.Count == 1)
            {
                SetAccessibleStatusText(TaskResult,
                    (IsArabic ? "حان موعد التذكير: " : "Reminder due: ") + deliveredReminders[0].Title);
            }
            else if (deliveredReminders.Count > 1)
            {
                SetAccessibleStatusText(TaskResult,
                    IsArabic
                        ? $"حان موعد {deliveredReminders.Count} تذكيرات محفوظة."
                        : $"{deliveredReminders.Count} saved reminders are due.");
            }
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTechnicalError(
                "تم تسجيل موعد التذكير، لكن تعذر عرض الإشعار",
                "The reminder was recorded as delivered, but its notification could not be displayed",
                ex,
                IsArabic));
        }

        if (scheduleChanged && AppRuntime.ReminderTaskSchedulerService is { } scheduler)
        {
            try { AppRuntime.SynchronizeLocalNotifications(); }
            catch (Exception ex)
            {
                SetAccessibleStatusText(TaskResult, UiLocalization.FormatTechnicalError(
                    "تعذر تحديث جدولة التذكيرات في Windows",
                    "Windows reminder scheduling could not be updated",
                    ex,
                    IsArabic));
            }
        }
    }

}
