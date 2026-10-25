using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;
using Microsoft.Win32;

namespace Layal.Views;

public partial class MainWindow : Window
{
    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();
    public ObservableCollection<ChatConversation> ChatConversations { get; } = new();
    public ObservableCollection<SkillRecord> InstalledSkills { get; } = new();
    public ObservableCollection<ApiConnectionProfile> ApiConnections { get; } = new();
    public ObservableCollection<string> TaskEvents { get; } = new();
    public ObservableCollection<TaskHistoryListItem> RecentTasks { get; } = new();

    private readonly List<string> _rawTaskEvents = new();
    private bool _refreshingRecentTasks;

    public sealed class TaskHistoryListItem
    {
        private readonly bool _isArabic;

        public AgentTaskRecord Record { get; }
        public string Id => Record.Id;
        public string Goal => Record.Goal;
        public string DisplayStatus => UiLocalization.FormatTaskStatus(Record.Status, _isArabic);
        public string DisplayUpdatedAt => UiLocalization.FormatDateTime(Record.UpdatedAt.LocalDateTime, _isArabic);

        public TaskHistoryListItem(AgentTaskRecord record, bool isArabic)
        {
            Record = record;
            _isArabic = isArabic;
        }
    }
    public ObservableCollection<MemoryNote> MemoryNotes { get; } = new();

    private readonly OpenAiCompatibleClient _aiClient = new();
    private readonly SpeechToTextService _speechToText = new();
    private readonly LayalSpeechService _layalSpeech = new();
    private readonly DispatcherTimer _speechRecordingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _speechCancellation;
    private CancellationTokenSource? _voiceTestCancellation;
    private CancellationTokenSource? _voiceConversationCancellation;
    private VoiceConversationStatus _voiceConversationStatus = VoiceConversationStatus.Stopped;
    private DateTimeOffset _speechRecordingStartedAt;
    private bool _speechIsStopping;
    private CancellationTokenSource? _chatCancellation;
    private CancellationTokenSource? _agentTaskCancellation;
    private string? _selectedMemoryId;
    private bool _providerTestPassed;
    private string? _testedBaseUrl;
    private string? _testedModelId;
    private string _currentConversationId = string.Empty;
    private bool _refreshingChatSessions;

    public MainWindow()
    {
        InitializeComponent();
        SystemEvents.UserPreferenceChanged += MainWindow_UserPreferenceChanged;
        Closed += MainWindow_Closed;
        _speechRecordingTimer.Tick += SpeechRecordingTimer_Tick;
        DataContext = this;
        BaseUrlInput.Text = AppRuntime.Settings.ProviderBaseUrl;
        ModelInput.Text = AppRuntime.Settings.ModelId;
        SpeechModelInput.Text = AppRuntime.Settings.SpeechToTextModelId;
        VoiceReplyCheckbox.IsChecked = AppRuntime.Settings.VoiceRepliesEnabled;
        VoiceReplyModelInput.Text = AppRuntime.Settings.VoiceReplyModelId;
        // Shimmer was the previous default. Switch that legacy default to Marin for more natural delivery.
        if (string.IsNullOrWhiteSpace(AppRuntime.Settings.VoiceReplyId) ||
            string.Equals(AppRuntime.Settings.VoiceReplyId, "shimmer", StringComparison.OrdinalIgnoreCase))
        {
            AppRuntime.Settings.VoiceReplyId = LayalSpeechService.DefaultVoiceId;
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        }
        VoiceReplyVoiceSelector.SelectedValue = AppRuntime.Settings.VoiceReplyId;
        AlwaysOnTopCheckbox.IsChecked = AppRuntime.Settings.MascotAlwaysOnTop;
        AnimationsCheckbox.IsChecked = AppRuntime.Settings.MascotAnimationsEnabled;
        MascotSizeSlider.Value = AppRuntime.Settings.MascotScale;
        MascotOpacitySlider.Value = AppRuntime.Settings.MascotOpacity;
        WindowInspectionCheckbox.IsChecked = AppRuntime.Settings.AllowWindowTitleInspection;
        var currentConversation = AppRuntime.ChatSessionService.GetCurrent();
        _currentConversationId = currentConversation.Id;
        foreach (var message in currentConversation.Messages.TakeLast(100)) ChatMessages.Add(message);
        RefreshChatSessions(_currentConversationId);
        var recent = AppRuntime.TaskHistoryService.GetRecent(1).FirstOrDefault();
        if (recent is not null)
        {
            foreach (var line in recent.Events.TakeLast(100))
            {
                _rawTaskEvents.Add(line);
                TaskEvents.Add(UiLocalization.FormatTaskEvent(line, IsArabic));
            }
            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTaskResult(recent, IsArabic));
        }
        RefreshRecentTasks();
        RefreshSkills();
        RefreshApiConnections();
        RefreshMemoryNotes();
        ApplyTheme();
        ApplyLanguage();
        RefreshProviderStatus();
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private bool IsArabic => AppRuntime.Settings.Language == "ar";
    private bool IsDarkTheme => AppRuntime.Settings.ThemeMode == "dark";

    private static System.Windows.Media.Brush ThemeBrush(string key, System.Windows.Media.Brush fallback) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

    private void ApplyTheme()
    {
        // Replace the theme brushes and gradients themselves, not only their source colors.
        // DynamicResource consumers then receive the replacement even if a control was
        // constructed before the user switched themes.
        var highContrast = SystemParameters.HighContrast;
        var palette = UiThemePalette.Create(IsDarkTheme, highContrast);

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var (key, value) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value)!;
            colors[key] = color;
            Application.Current.Resources[key] = color;
        }

        void SetSolidBrush(string brushKey, string colorKey) =>
            Application.Current.Resources[brushKey] = new SolidColorBrush(colors[colorKey]);

        SetSolidBrush("Layal.Background", "Layal.Color.Background");
        SetSolidBrush("Layal.Panel", "Layal.Color.Surface");
        SetSolidBrush("Layal.PanelAlt", "Layal.Color.SurfaceRaised");
        SetSolidBrush("Layal.PanelHover", "Layal.Color.SurfaceHover");
        SetSolidBrush("Layal.Border", "Layal.Color.Border");
        SetSolidBrush("Layal.Cyan", "Layal.Color.Cyan");
        SetSolidBrush("Layal.Purple", "Layal.Color.Purple");
        SetSolidBrush("Layal.Text", "Layal.Color.Text");
        SetSolidBrush("Layal.Muted", "Layal.Color.Muted");
        SetSolidBrush("Layal.ScrollThumb", "Layal.Color.ScrollThumb");
        SetSolidBrush("Layal.Success", "Layal.Color.Success");
        SetSolidBrush("Layal.Warning", "Layal.Color.Warning");
        SetSolidBrush("Layal.Danger", "Layal.Color.Danger");
        SetSolidBrush("Layal.Focus", "Layal.Color.Focus");
        SetSolidBrush("Layal.White", "Layal.Color.Surface");
        SetSolidBrush("Layal.BrandBorder", "Layal.Color.BrandBorder");
        SetSolidBrush("Layal.DecorativeAmber", "Layal.Color.BrandDot");
        SetSolidBrush("Layal.DangerSurface", "Layal.Color.DangerSurface");
        SetSolidBrush("Layal.DangerBorder", "Layal.Color.DangerBorder");
        SetSolidBrush("Layal.HoverSurface", "Layal.Color.HoverSurface");
        SetSolidBrush("Layal.InputFocus", "Layal.Color.InputFocus");
        SetSolidBrush("Layal.ListHover", "Layal.Color.ListHover");
        SetSolidBrush("Layal.ListSelected", "Layal.Color.ListSelected");
        SetSolidBrush("Layal.NavSelected", "Layal.Color.NavSelected");
        SetSolidBrush("Layal.NavSelectedBorder", "Layal.Color.NavSelectedBorder");
        SetSolidBrush("Layal.NavHover", "Layal.Color.NavHover");

        LinearGradientBrush MakeGradient(params (string Key, double Offset)[] stops)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            foreach (var stop in stops)
                brush.GradientStops.Add(new GradientStop(colors[stop.Key], stop.Offset));
            return brush;
        }

        Application.Current.Resources["Layal.Background"] = MakeGradient(
            ("Layal.Color.Background", 0),
            ("Layal.Color.BackgroundEnd", 1));
        Application.Current.Resources["Layal.HeaderGradient"] = MakeGradient(
            ("Layal.Color.HeaderStart", 0),
            ("Layal.Color.HeaderMiddle", 0.56),
            ("Layal.Color.HeaderEnd", 1));
        Application.Current.Resources["Layal.CardGradient"] = MakeGradient(
            ("Layal.Color.CardStart", 0),
            ("Layal.Color.CardEnd", 1));
        Application.Current.Resources["Layal.PrimaryButtonGradient"] = MakeGradient(
            ("Layal.Color.ButtonStart", 0),
            ("Layal.Color.ButtonEnd", 1));
        Application.Current.Resources["Layal.CardShadow"] = new DropShadowEffect
        {
            Color = colors["Layal.Color.Shadow"],
            BlurRadius = 24,
            ShadowDepth = 2,
            Opacity = 0.14
        };

        if (highContrast)
        {
            // High Contrast is controlled by Windows and must override app-authored gradients,
            // shadows and accent surfaces rather than merely recoloring their source stops.
            var systemSurface = new SolidColorBrush(SystemColors.WindowColor);
            Application.Current.Resources["Layal.Background"] = systemSurface;
            Application.Current.Resources["Layal.HeaderGradient"] = systemSurface;
            Application.Current.Resources["Layal.CardGradient"] = systemSurface;
            Application.Current.Resources["Layal.PrimaryButtonGradient"] = systemSurface;
            Application.Current.Resources["Layal.CardShadow"] = new DropShadowEffect
            {
                Color = SystemColors.WindowTextColor,
                BlurRadius = 0,
                ShadowDepth = 0,
                Opacity = 0
            };
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
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
        _speechRecordingTimer.Stop();
        _speechRecordingTimer.Tick -= SpeechRecordingTimer_Tick;
        _speechCancellation?.Cancel();
        _voiceTestCancellation?.Cancel();
        _speechToText.Dispose();
        _speechCancellation?.Dispose();
        _voiceTestCancellation?.Dispose();
        SystemEvents.UserPreferenceChanged -= MainWindow_UserPreferenceChanged;
        Closed -= MainWindow_Closed;
    }

    private void ApplyLanguage()
    {
        var ar = IsArabic;
        // Keep the root visual tree LTR: setting RTL on the entire Window mirrors glyphs and icons.
        // Apply RTL to input fields and Arabic text runs locally instead of flipping all visuals.
        FlowDirection = FlowDirection.LeftToRight;
        ChatInput.FlowDirection = TaskInput.FlowDirection = MemoryTitleInput.FlowDirection = MemoryContentInput.FlowDirection = MemorySearchInput.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SpeechModelInput.FlowDirection = VoiceReplyModelInput.FlowDirection = FlowDirection.LeftToRight;
        TaskResult.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        BaseUrlInput.FlowDirection = ModelInput.FlowDirection = ApiKeyInput.FlowDirection = GitHubSkillUrlInput.FlowDirection = FlowDirection.LeftToRight;
        foreach (var message in ChatMessages)
        {
            message.DisplayRole = message.Role switch
            {
                "user" => ar ? "أنت" : "You",
                "assistant" => "Layal | ليال",
                _ => ar ? "حالة النظام" : "System status"
            };
            message.Direction = ContainsArabic(message.Content) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        ChatMessagesList?.Items.Refresh();

        Title = ar ? "ليال | Layal" : "Layal | ليال";
        TitleText.Text = ar ? "ليال | Layal" : "Layal | Bilingual Desktop AI";
        SubtitleText.Text = ar ? "وكيل ذكاء اصطناعي مستقل لسطح المكتب" : "A bilingual Windows AI desktop companion";
        LanguageButton.Content = ar ? "English" : "العربية";
        ThemeButton.Content = IsDarkTheme
            ? (ar ? "☀ المظهر الفاتح" : "☀ Light theme")
            : (ar ? "☾ المظهر الداكن" : "☾ Dark theme");
        ThemeButton.ToolTip = IsDarkTheme
            ? (ar ? "التبديل إلى المظهر الفاتح" : "Switch to light theme")
            : (ar ? "التبديل إلى المظهر الداكن" : "Switch to dark theme");
        ShowMascotButton.Content = ar ? "إظهار الشخصية" : "Show mascot";
        KeyboardHelpButton.Content = ar ? "_اختصارات" : "_Shortcuts";
        KeyboardHelpButton.ToolTip = ar ? "F1 — عرض اختصارات لوحة المفاتيح" : "F1 — Show keyboard shortcuts";
        AutomationProperties.SetName(KeyboardHelpButton, ar ? "اختصارات لوحة المفاتيح" : "Keyboard shortcuts");
        AutomationProperties.SetHelpText(KeyboardHelpButton, ar ? "اضغط F1 لعرض اختصارات التنقل." : "Press F1 to review navigation shortcuts.");
        AutomationProperties.SetName(MainTabs, ar ? "التنقل الرئيسي" : "Main navigation");
        AutomationProperties.SetHelpText(MainTabs, ar ? "استخدم Ctrl مع الأرقام من 1 إلى 7 للتنقل بين التبويبات." : "Use Ctrl+1 through Ctrl+7 to switch tabs.");
        AutomationProperties.SetName(ConnectionStatusText, ar ? "حالة اتصال الذكاء الاصطناعي" : "AI connection status");
        AutomationProperties.SetName(ChatHistorySelector, ar ? "المحادثات المحفوظة" : "Saved conversations");
        AutomationProperties.SetHelpText(ChatHistorySelector, ar ? "اختر محادثة محفوظة لاستكمالها." : "Choose a saved conversation to resume it.");
        AutomationProperties.SetName(ChatMessagesList, ar ? "رسائل المحادثة" : "Chat messages");
        AutomationProperties.SetHelpText(ChatMessagesList, ar ? "استخدم الأسهم لاستعراض الرسائل." : "Use the arrow keys to review messages.");
        AutomationProperties.SetName(ChatInput, ar ? "الرسالة" : "Message");
        AutomationProperties.SetHelpText(ChatInput, ar ? "اكتب رسالة واضغط Enter للإرسال. Ctrl+2 ينقل التركيز إلى هذا الحقل." : "Type a message and press Enter to send. Ctrl+2 focuses this field.");
        AutomationProperties.SetName(TaskInput, ar ? "وصف المهمة" : "Task description");
        AutomationProperties.SetHelpText(TaskInput, ar ? "اكتب المهمة ثم استخدم Tab للوصول إلى زر تنفيذ المهمة." : "Describe the task, then press Tab to reach Run task.");
        AutomationProperties.SetName(TaskEventsList, ar ? "سجل أحداث المهمة" : "Task event log");
        AutomationProperties.SetName(RecentTasksList, ar ? "المهام السابقة" : "Recent tasks");
        AutomationProperties.SetHelpText(RecentTasksList, ar ? "اختر مهمة سابقة لعرض تفاصيلها." : "Select a previous task to view its details.");
        AutomationProperties.SetName(SkillsList, ar ? "المهارات المثبتة" : "Installed skills");
        AutomationProperties.SetHelpText(SkillsList, ar ? "يمكن تحديد عدة مهارات باستخدام Ctrl أو Shift مع الأسهم." : "Select multiple skills with Ctrl or Shift and the arrow keys.");
        AutomationProperties.SetName(GitHubSkillUrlInput, ar ? "رابط مهارة GitHub" : "GitHub skill URL");
        AutomationProperties.SetHelpText(GitHubSkillUrlInput, ar ? "أدخل رابط مستودع أو مجلد يحتوي على SKILL.md." : "Enter a repository or folder URL containing SKILL.md.");
        AutomationProperties.SetHelpText(BaseUrlInput, ar ? "عنوان HTTP أو HTTPS الأساسي لمزود الذكاء الاصطناعي." : "HTTP or HTTPS base URL for the provider.");
        AutomationProperties.SetHelpText(ApiKeyInput, ar ? "يُحفظ المفتاح مشفرًا لمستخدم Windows الحالي." : "The API key is saved encrypted for the current Windows user.");
        AutomationProperties.SetName(MemorySearchInput, ar ? "البحث في الذاكرة" : "Search memory");
        AutomationProperties.SetHelpText(MemorySearchInput, ar ? "أدخل كلمات البحث ثم استخدم زر بحث." : "Enter search terms, then activate Search.");
        AutomationProperties.SetName(MemoryList, ar ? "ملاحظات الذاكرة" : "Memory notes");
        AutomationProperties.SetHelpText(MemoryList, ar ? "اختر ملاحظة لعرضها أو تعديلها." : "Select a note to view or edit it.");

        // Refresh transient feedback in the newly selected language. Unknown model/tool output
        // is intentionally preserved rather than guessed at or translated destructively.
        SetAccessibleStatusText(HomeResult, UiLocalization.TranslateKnownFeedback(HomeResult.Text, ar));
        SetAccessibleStatusText(ProviderActionStatus, UiLocalization.TranslateKnownFeedback(ProviderActionStatus.Text, ar));
        SetAccessibleStatusText(ChatCopyStatus, UiLocalization.TranslateKnownFeedback(ChatCopyStatus.Text, ar));
        SetAccessibleStatusText(TaskCopyStatus, UiLocalization.TranslateKnownFeedback(TaskCopyStatus.Text, ar));
        SetAccessibleStatusText(SkillsResult, UiLocalization.TranslateKnownFeedback(SkillsResult.Text, ar));
        if (MemoryList.SelectedItem is MemoryNote selectedMemory &&
            UiLocalization.IsFormattedDateTime(MemoryResult.Text, !ar))
            SetAccessibleStatusText(MemoryResult, UiLocalization.FormatDateTime(selectedMemory.UpdatedAt.LocalDateTime, ar));
        else
            SetAccessibleStatusText(MemoryResult, UiLocalization.TranslateKnownFeedback(MemoryResult.Text, ar));
        SetAccessibleStatusText(TaskResult, UiLocalization.TranslateTaskResultDisplay(TaskResult.Text, ar));

        TaskEvents.Clear();
        foreach (var line in _rawTaskEvents)
            TaskEvents.Add(UiLocalization.FormatTaskEvent(line, ar));
        RefreshRecentTasks();

        HomeTab.Header = ar ? "⌂  الرئيسية" : "⌂  Home";
        ChatTab.Header = ar ? "✦  المحادثة" : "✦  Chat";
        TasksTab.Header = ar ? "▣  المهام" : "▣  Tasks";
        SkillsTab.Header = ar ? "◇  المهارات" : "◇  Skills";
        CamerasTab.Header = ar ? "◉  الكاميرات" : "◉  Cameras";
        MemoryTab.Header = ar ? "▤  الذاكرة" : "▤  Memory";
        SettingsTab.Header = ar ? "⚙  الإعدادات" : "⚙  Settings";
        System.Windows.Automation.AutomationProperties.SetName(HomeTab, ar ? "الرئيسية" : "Home");
        System.Windows.Automation.AutomationProperties.SetName(ChatTab, ar ? "المحادثة" : "Chat");
        System.Windows.Automation.AutomationProperties.SetName(TasksTab, ar ? "المهام" : "Tasks");
        System.Windows.Automation.AutomationProperties.SetName(SkillsTab, ar ? "المهارات" : "Skills");
        System.Windows.Automation.AutomationProperties.SetName(CamerasTab, ar ? "الكاميرات" : "Cameras");
        System.Windows.Automation.AutomationProperties.SetName(MemoryTab, ar ? "الذاكرة" : "Memory");
        System.Windows.Automation.AutomationProperties.SetName(SettingsTab, ar ? "الإعدادات" : "Settings");
        System.Windows.Automation.AutomationProperties.SetName(LanguageButton, ar ? "تغيير لغة الواجهة" : "Change interface language");
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, ThemeButton.ToolTip as string ?? (ar ? "تغيير المظهر" : "Change theme"));
        System.Windows.Automation.AutomationProperties.SetName(ShowMascotButton, ar ? "إظهار الشخصية" : "Show mascot");

        HomeHeading.Text = ar ? "أهلًا، أنا ليال" : "Hello, I'm Layal";
        HomeDescription.Text = ar
            ? "هذه النسخة هي أساس Windows الأصلي. اربط نموذجًا متوافقًا من الإعدادات لتفعيل المحادثة الحقيقية."
            : "This is the native Windows foundation. Configure a compatible model provider to enable real AI chat.";
        CardAiTitle.Text = ar ? "اتصال الذكاء الاصطناعي" : "AI connection";
        CardAiHint.Text = ar ? "أدخل عنوان API والمفتاح من الإعدادات" : "Configure an API endpoint and key in Settings.";
        CardMascotTitle.Text = ar ? "الشخصية العائمة" : "Floating mascot";
        CardMascotValue.Text = ar ? "نافذة Windows أصلية" : "Native Windows window";
        CardMascotHint.Text = ar ? "اسحب ليال على سطح المكتب، وانقر مرتين لفتح الواجهة." : "Drag Layal on the desktop; double-click to open the main UI.";
        CardToolsTitle.Text = ar ? "أدوات Windows" : "Windows tools";
        CardToolsValue.Text = ar ? "محرك أدوات محدود" : "Bounded tool engine";
        CardToolsHint.Text = ar ? "تشغيل أدوات محددة مع موافقات وتسجيل للنتائج." : "Allow-listed tools with approvals and task history.";
        CardSkillsTitle.Text = ar ? "مدير المهارات" : "Skills manager";
        CardSkillsValue.Text = ar ? "استيراد ZIP وGitHub" : "ZIP and GitHub import";
        CardSkillsHint.Text = ar ? "فحص SKILL.md؛ لا تُشغَّل سكربتات المهارة." : "SKILL.md validation; skill scripts are never executed.";
        OpenNotepadButton.Content = ar ? "فتح المفكرة (اختبار فعلي)" : "Launch Notepad (real test)";
        GoChatButton.Content = ar ? "فتح المحادثة" : "Open chat";
        GoSettingsButton.Content = ar ? "إعداد اتصال AI" : "Configure AI";
        HomeCaveat.Text = ar
            ? "يتوفر الآن تسجيل يدوي لتحويل الكلام إلى نص (عربية وعامية مصرية وإنجليزية) بعد موافقة المستخدم على إرسال التسجيل. توليد الصوت والمحادثة اللحظية وأتمتة المتصفح والبث الحقيقي للكاميرات ما زالت مراحل لاحقة."
            : "Manual speech-to-text is now available for Arabic, Egyptian Arabic colloquial speech, and English; uploading requires explicit confirmation. Speech synthesis, realtime voice, browser automation, and real camera streaming remain future milestones.";

        ChatHeading.Text = ar ? "المحادثة مع ليال" : "Chat with Layal";
        ChatNotice.Text = ar ? "تحتاج إلى عنوان API ومفتاح صحيح من الإعدادات لإرسال رسائل حقيقية." : "Configure a valid API endpoint and key in Settings to send real messages.";
        AutomationProperties.SetName(ConnectionStatusText, ar ? "حالة اتصال المزود" : "Provider connection status");
        AutomationProperties.SetName(ChatProviderStatus, ar ? "حالة إعداد الاتصال" : "Provider configuration status");
        AutomationProperties.SetName(HomeResult, ar ? "نتيجة الإجراء" : "Action result");
        AutomationProperties.SetName(ChatCopyStatus, ar ? "حالة النسخ" : "Copy status");
        AutomationProperties.SetName(TaskCopyStatus, ar ? "حالة نسخ النتيجة" : "Result copy status");
        AutomationProperties.SetName(SkillsResult, ar ? "نتيجة استيراد المهارة" : "Skill import status");
        AutomationProperties.SetName(MemoryResult, ar ? "نتيجة إجراء الذاكرة" : "Memory action status");
        AutomationProperties.SetName(ProviderActionStatus, ar ? "حالة إعداد المزود" : "Provider settings status");
        SetAccessibleStatusText(ChatActivityStatus, TranslateChatActivityStatus(ChatActivityStatus.Text, ar));
        AutomationProperties.SetName(ChatActivityStatus, ar ? "حالة المحادثة" : "Chat activity status");
        AutomationProperties.SetHelpText(ChatActivityStatus, ar
            ? "تُعلن هنا حالة إرسال الرد أو إيقافه أو فشله."
            : "Announces when a response starts, completes, is cancelled, or fails.");
        ChatEmptyStateText.Text = ar
            ? "لا توجد رسائل هنا بعد. اكتب رسالة بالأسفل لبدء المحادثة."
            : "No messages yet. Type a message below to start the conversation.";
        AutomationProperties.SetName(ChatEmptyStateText, ar ? "حالة المحادثة الفارغة" : "Chat empty state");
        AutomationProperties.SetHelpText(ChatEmptyStateText, ar ? "أرسل رسالة أو استقبل ردًا لتظهر الرسائل في هذه القائمة." : "Send or receive a message to populate this list.");
        NewConversationButton.Content = ar ? "محادثة جديدة" : "New conversation";
        ClearSavedChatHistoryButton.Content = ar ? "مسح السجل المحفوظ" : "Clear saved history";
        ChatHistorySelector.ToolTip = ar ? "المحادثات المحفوظة — اختر محادثة للمتابعة" : "Saved conversations — select one to resume";
        System.Windows.Automation.AutomationProperties.SetName(ChatHistorySelector, ar ? "المحادثات المحفوظة" : "Saved conversations");
        System.Windows.Automation.AutomationProperties.SetName(ChatInput, ar ? "رسالة المحادثة" : "Chat message");
        System.Windows.Automation.AutomationProperties.SetHelpText(ChatInput, ar ? "اكتب رسالة ثم اضغط Enter لإرسالها." : "Type a message and press Enter to send.");
        CopyChatButton.Content = ar ? "نسخ الشات" : "Copy chat";
        CopyChatButton.ToolTip = ar ? "نسخ المحادثة كاملة إلى الحافظة" : "Copy the entire conversation to the clipboard";
        SendButton.Content = ar ? "إرسال" : "Send";
        CancelResponseButton.Content = ar ? "إيقاف" : "Stop";
        ChatInput.ToolTip = ar ? "اكتب رسالة ثم اضغط Enter" : "Type a message and press Enter";
        SpeechModelLabel.Text = ar ? "معرّف نموذج تحويل الكلام إلى نص (STT)" : "Speech-to-text model ID (STT)";
        VoiceReplySectionTitle.Text = ar ? "صوت ليال" : "Layal voice";
        VoiceReplyCheckbox.Content = ar ? "ليال تقرأ ردودها بصوت طفولي" : "Have Layal read replies aloud in a childlike voice";
        VoiceReplyVoiceLabel.Text = ar ? "صوت ليال الطفولي" : "Layal childlike voice";
        VoiceReplyModelLabel.Text = ar ? "معرّف نموذج الصوت (Realtime)" : "Voice model ID (Realtime)";
        VoiceReplyDisclosureText.Text = ar
            ? "صوت ليال طفولي وأنثوي وناعم، مولّد بالذكاء الاصطناعي وليس صوت إنسان. يتطلب دعم Realtime API وقد يستهلك تكلفة إضافية."
            : "Layal uses a soft, childlike feminine AI-generated voice, not a human voice. Realtime API support is required and extra usage charges may apply.";
        TestVoiceReplyButton.Content = ar ? "تجربة صوت ليال" : "Test Layal voice";
        TestVoiceReplyButton.ToolTip = ar
            ? "يرسل جملة قصيرة إلى موفر Realtime لتجربة نبرة الصوت."
            : "Sends a short sample to your Realtime provider to preview the selected voice.";
        foreach (ComboBoxItem voiceItem in VoiceReplyVoiceSelector.Items)
        {
            voiceItem.Content = (voiceItem.Tag?.ToString() ?? string.Empty).ToLowerInvariant() switch
            {
                "shimmer" => ar ? "طفولي وناعم (Shimmer)" : "Childlike and soft (Shimmer)",
                "coral" => ar ? "طفولي ودافئ (Coral)" : "Childlike and warm (Coral)",
                "marin" => ar ? "طبيعي وعفوي (Marin)" : "Natural and conversational (Marin)",
                "cedar" => ar ? "هادئة وواضحة (Cedar)" : "Calm and clear (Cedar)",
                _ => voiceItem.Tag?.ToString() ?? string.Empty
            };
        }
        AutomationProperties.SetName(VoiceReplyCheckbox, ar ? "الردود الصوتية لليال" : "Layal voice replies");
        AutomationProperties.SetHelpText(VoiceReplyCheckbox, ar
            ? "توليد صوت ليال الطفولي بعد اكتمال الرد النصي. يتطلب موفر Realtime وقد يستهلك تكلفة إضافية."
            : "Generate Layal’s childlike AI voice after text replies complete. Requires a Realtime provider and may incur additional usage charges.");
        SpeechModelInput.ToolTip = ar
            ? "الافتراضي gpt-transcribe؛ تحقق من دعم موفر API لنقطة /audio/transcriptions والنموذج."
            : "Defaults to gpt-transcribe. Confirm the provider supports /audio/transcriptions and this model.";
        AutomationProperties.SetName(SpeechModelInput, ar ? "معرّف نموذج تحويل الكلام إلى نص" : "Speech-to-text model ID");
        AutomationProperties.SetHelpText(SpeechModelInput, ar
            ? "اسم نموذج الصوت لدى مزودك، وليس بالضرورة معرّف نموذج المحادثة."
            : "The speech model supported by your provider; it may differ from your chat model.");
        AutomationProperties.SetName(SpeechToTextButton, ar ? "تسجيل صوت وتحويله إلى نص" : "Record speech and transcribe it");
        AutomationProperties.SetHelpText(SpeechToTextButton, ar
            ? "بدء التسجيل يدوي. اضغط ثانية للإيقاف. الإرسال إلى موفر API يحتاج تأكيدًا منفصلًا، والنص الناتج لا يُرسل تلقائيًا."
            : "Manual recording only. Press again to stop. Sending audio to the API needs separate confirmation; the transcript is not sent automatically.");
        UpdateSpeechToTextButton();
        UpdateVoiceConversationControls();

        TasksHeading.Text = ar ? "مركز المهام" : "Task center";
        TasksDescription.Text = ar
            ? "اكتب مهمة بالعربية أو الإنجليزية. تستخدم ليال استدعاء الأدوات المحددة، وتطلب موافقتك قبل الإجراءات التي تغيّر الملفات أو تفتح تطبيقات أو تشارك بيانات."
            : "Describe a task in Arabic or English. Layal uses allow-listed tools and requests approval before file changes, application launches, or data disclosure.";
        TaskSafeToolTitle.Text = ar ? "أدوات Windows المحدودة" : "Bounded Windows tools";
        TaskSafeToolDescription.Text = ar ? "الأدوات المتاحة محددة؛ لا يوجد وصول عام غير مقيد إلى سطر الأوامر أو الملفات." : "Available tools are allow-listed; there is no unrestricted shell or filesystem access.";
        TaskOpenNotepadButton.Content = ar ? "فتح المفكرة (اختبار)" : "Launch Notepad (test)";
        RunAgentTaskButton.Content = ar ? "تنفيذ المهمة" : "Run task";
        CancelAgentTaskButton.Content = ar ? "إيقاف المهمة" : "Stop task";
        TaskInput.ToolTip = ar ? "اكتب المهمة بالعربية أو الإنجليزية" : "Describe the task in Arabic or English";
        System.Windows.Automation.AutomationProperties.SetName(TaskInput, ar ? "وصف المهمة" : "Task description");
        System.Windows.Automation.AutomationProperties.SetHelpText(TaskInput, ar ? "صف ما تريد من ليال تنفيذه." : "Describe what you want Layal to do.");
        System.Windows.Automation.AutomationProperties.SetName(TaskResult, ar ? "نتيجة المهمة" : "Task result");
        System.Windows.Automation.AutomationProperties.SetHelpText(TaskResult, ar
            ? "يعرض النتيجة النهائية وحالة المهمة، بما في ذلك التحذيرات أو الإخفاق."
            : "Shows the final task result and its status, including warnings or failures.");
        TaskSkillHint.Text = ar ? "يمكن تحديد حتى 3 مهارات من تبويب المهارات؛ نصوص المهارات تُعامل كمراجع غير موثوقة." : "Select up to 3 skills in Skills; skill text is treated as untrusted reference material.";
        CopyTaskResultButton.Content = ar ? "نسخ النتيجة" : "Copy result";
        CopyTaskLogButton.Content = ar ? "نسخ سجل التنفيذ" : "Copy task log";
        TaskEventsEmptyStateText.Text = ar
            ? "لا يوجد سجل نشاط بعد. ابدأ مهمة لعرض أحداث تنفيذها هنا."
            : "No task activity yet. Start a task to see its events here.";
        AutomationProperties.SetName(TaskEventsEmptyStateText, ar ? "حالة سجل المهام الفارغ" : "Task activity empty state");
        AutomationProperties.SetHelpText(TaskEventsEmptyStateText, ar ? "ستظهر أحداث التنفيذ هنا عند بدء مهمة." : "Task events appear here after a task starts.");
        RecentTasksEmptyStateText.Text = ar ? "لا توجد مهام سابقة بعد." : "No previous tasks yet.";
        AutomationProperties.SetName(RecentTasksEmptyStateText, ar ? "حالة المهام السابقة الفارغة" : "Recent tasks empty state");
        AutomationProperties.SetHelpText(RecentTasksEmptyStateText, ar ? "تظهر المهام السابقة بعد تشغيل المهام وحفظ سجلاتها." : "Previous tasks appear after tasks run and their history is saved.");
        if (string.IsNullOrEmpty(TaskResult.Text)) SetAccessibleStatusText(TaskResult, ar ? "لم تُنفذ أي عملية بعد." : "No operation has been run yet.");

        SkillsHeading.Text = ar ? "مدير المهارات" : "Skills manager";
        SkillsDescription.Text = ar ? "استيراد حزمة ZIP والتحقق من SKILL.md. لا يتم استخراج الحزمة أو تشغيل سكربتاتها." : "Import a ZIP and validate SKILL.md. The package is stored without extraction or script execution.";
        SkillsEmptyStateText.Text = ar
            ? "لا توجد مهارات مثبتة. استورد ملف ZIP أو مجلدًا من GitHub للبدء."
            : "No skills installed. Import a ZIP or GitHub folder to get started.";
        AutomationProperties.SetName(SkillsEmptyStateText, ar ? "حالة قائمة المهارات الفارغة" : "Skills empty state");
        AutomationProperties.SetHelpText(SkillsEmptyStateText, ar ? "استخدم زري استيراد ZIP أو GitHub لإضافة مهارة." : "Use Import ZIP or Import from GitHub to add a skill.");
        ImportSkillButton.Content = ar ? "استيراد ZIP" : "Import ZIP";
        RefreshSkillsButton.Content = ar ? "تحديث القائمة" : "Refresh list";
        RecentTasksGroup.Header = ar ? "المهام السابقة" : "Recent tasks";
        ImportGitHubSkillButton.Content = ar ? "استيراد من GitHub" : "Import from GitHub";
        GitHubSkillUrlInput.ToolTip = ar ? "رابط مستودع أو مجلد يحتوي على SKILL.md" : "Repository or directory URL containing SKILL.md";
        System.Windows.Automation.AutomationProperties.SetName(GitHubSkillUrlInput, ar ? "رابط مستودع المهارة" : "Skill repository URL");
        System.Windows.Automation.AutomationProperties.SetHelpText(GitHubSkillUrlInput, ar ? "الصق رابط GitHub لمجلد يحتوي على SKILL.md." : "Paste a GitHub URL to a directory containing SKILL.md.");

        CamerasHeading.Text = ar ? "الكاميرات والرؤية الحاسوبية" : "Cameras and computer vision";
        CameraStatus.Text = ar ? "غير متصل — التكامل الحقيقي غير منفذ في إصدار الأساس" : "Not connected — real camera integration is not implemented in this foundation release";
        CameraDescription.Text = ar
            ? "سيُضاف اتصال RTSP/ONVIF ومعالجة الفيديو بعد إنشاء خدمة الكاميرات واختبارها على Windows مع جهاز مصرح به. لا يعرض هذا الإصدار بثًا وهميًا."
            : "RTSP/ONVIF and video processing will be added after the camera service is implemented and tested on Windows with an authorized device. This release does not show fake streams.";


        ApiIntegrationsTab.Header = ar ? "تكاملات API" : "API integrations";
        ApiIntegrationsHeading.Text = ar ? "ربط التطبيقات عبر API" : "Connect applications through APIs";
        ApiIntegrationsDescription.Text = ar
            ? "أضف تطبيقات توفر REST/JSON API. بيانات المصادقة تُحفظ مشفرة محليًا. كل طلب تنفذه ليال يحتاج موافقتك، بما فيه طلبات القراءة."
            : "Add applications that expose a REST/JSON API. Credentials are encrypted locally. Every request initiated by Layal requires your approval, including reads.";
        ApiConnectionsListLabel.Text = ar ? "التطبيقات المتصلة" : "Connected applications";
        NewApiConnectionButton.Content = ar ? "إضافة اتصال جديد" : "Add connection";
        ApiConnectionsPrivacyNote.Text = ar
            ? "لا تضع المفاتيح في المحادثة أو ملاحظات التوثيق. أدخلها هنا؛ تُحفظ بتشفير Windows DPAPI لهذا المستخدم."
            : "Do not paste API keys in chat or API notes. Enter them here; they are stored with Windows DPAPI for this Windows user.";
        ApiConnectionNameLabel.Text = ar ? "اسم التطبيق / الاتصال" : "Application / connection name";
        ApiConnectionBaseUrlLabel.Text = ar ? "عنوان API الأساسي (Base URL)" : "API base URL";
        ApiAuthTypeLabel.Text = ar ? "طريقة المصادقة" : "Authentication type";
        ApiAuthHeaderNameLabel.Text = ar ? "اسم ترويسة المصادقة (Custom header)" : "Authentication header name (Custom header)";
        ApiAuthSecretLabel.Text = ar ? "Token / API key / قيمة الترويسة" : "Token / API key / header value";
        ApiAdditionalHeadersLabel.Text = ar ? "ترويسات HTTP إضافية غير سرية (JSON)" : "Additional non-secret HTTP headers (JSON)";
        ApiTestPathLabel.Text = ar ? "مسار اختبار الاتصال (GET)، مثل health أو users/me" : "Connection test path (GET), e.g. health or users/me";
        ApiInstructionsLabel.Text = ar ? "ملاحظات التوثيق ونقاط النهاية (اختياري، بدون أسرار)" : "API docs and endpoint notes (optional; do not include secrets)";
        SaveApiConnectionButton.Content = ar ? "حفظ الاتصال" : "Save connection";
        TestApiConnectionButton.Content = ar ? "اختبار API" : "Test API";
        DeleteApiConnectionButton.Content = ar ? "حذف الاتصال المحدد" : "Delete selected connection";
        ((ComboBoxItem)ApiAuthTypeComboBox.Items[0]).Content = ar ? "بدون مصادقة / None" : "No authentication";
        ((ComboBoxItem)ApiAuthTypeComboBox.Items[1]).Content = "Bearer token";
        ((ComboBoxItem)ApiAuthTypeComboBox.Items[2]).Content = ar ? "ترويسة مخصصة (API key)" : "Custom header (API key)";

        SettingsHeading.Text = ar ? "الإعدادات والاتصال" : "Settings and connections";
        LanguageSettingLabel.Text = ar ? "لغة الواجهة" : "Interface language";
        ArabicLanguageButton.Content = "العربية";
        EnglishLanguageButton.Content = "English";
        ProviderSectionTitle.Text = ar ? "محرك الذكاء الاصطناعي (بروتوكول OpenAI-compatible)" : "AI model endpoint (OpenAI-compatible protocol)";
        EndpointLabel.Text = ar ? "عنوان API الأساسي (Base URL)" : "API base URL";
        ModelLabel.Text = ar ? "معرّف النموذج" : "Model ID";
        ApiKeyLabel.Text = ar ? "مفتاح API / Unified API Key" : "API key / Unified API key";
        System.Windows.Automation.AutomationProperties.SetName(BaseUrlInput, EndpointLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(ModelInput, ModelLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(ApiKeyInput, ApiKeyLabel.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(ApiKeyInput, ar ? "يُخزن المفتاح مشفرًا للمستخدم الحالي." : "The key is encrypted for the current Windows user.");
        KeyStorageNote.Text = ar ? "يُحفظ المفتاح باستخدام Windows DPAPI للمستخدم الحالي، ولا يظهر في ملف الإعدادات النصي." : "The key is encrypted with Windows DPAPI for the current user and is not stored in the plain settings file.";
        SaveProviderButton.Content = ar ? "حفظ الإعدادات" : "Save settings";
        TestProviderButton.Content = ar ? "اختبار الاتصال" : "Test connection";
        DeleteKeyButton.Content = ar ? "حذف المفتاح المحفوظ" : "Delete saved key";
        MascotSettingsTitle.Text = ar ? "إعدادات الشخصية العائمة" : "Floating mascot settings";
        AlwaysOnTopCheckbox.Content = ar ? "إبقاء الشخصية فوق النوافذ الأخرى" : "Keep mascot above other windows";
        AnimationsCheckbox.Content = ar ? "تفعيل حركة الشخصية والرمش" : "Enable mascot movement and blinking";
        MascotSizeLabel.Text = ar ? "حجم الشخصية" : "Mascot size";
        MascotOpacityLabel.Text = ar ? "شفافية الشخصية" : "Mascot opacity";
        System.Windows.Automation.AutomationProperties.SetName(MascotSizeSlider, MascotSizeLabel.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(MascotSizeSlider, ar ? "عدّل القيمة باستخدام الأسهم بعد الوصول إلى شريط التمرير." : "Use the arrow keys to adjust this slider when focused.");
        System.Windows.Automation.AutomationProperties.SetName(MascotOpacitySlider, MascotOpacityLabel.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(MascotOpacitySlider, ar ? "عدّل القيمة باستخدام الأسهم بعد الوصول إلى شريط التمرير." : "Use the arrow keys to adjust this slider when focused.");
        ApplyMascotSettingsButton.Content = ar ? "تطبيق إعدادات الشخصية" : "Apply mascot settings";
        SettingsLimitations.Text = ar
            ? "يتوفر تحويل الكلام إلى نص يدويًا (عربية وعامية مصرية وإنجليزية) بزر الميكروفون في المحادثة. توليد الصوت والمحادثة الصوتية اللحظية، وأتمتة المتصفح، والتحكم العام بالماوس والكيبورد، وبث الكاميرات، وMCP ما زالت غير منفذة."
            : "Manual speech-to-text is available from the microphone button in Chat for Arabic, Egyptian Arabic colloquial speech, and English. Speech synthesis/realtime voice, browser automation, general mouse/keyboard control, camera streaming, and MCP remain unimplemented.";
        WindowInspectionCheckbox.Content = ar ? "السماح بفحص النوافذ وعناصر الواجهة وتنفيذ فعل واحد بموافقة" : "Allow window/UI inspection and one approved control action";
        MemoryHeading.Text = ar ? "الذاكرة الشخصية" : "Personal memory";
        MemoryDescription.Text = ar ? "ملاحظات تحفظها أنت. تُشفّر محليًا بحماية Windows DPAPI؛ مشاركة نتائج البحث مع AI تتطلب موافقة." : "User-managed notes encrypted with Windows DPAPI. Sharing search results with AI requires approval.";
        MemoryEmptyStateText.Text = ar
            ? "لا توجد ملاحظات محفوظة. أنشئ ملاحظة جديدة للبدء."
            : "No saved notes. Create a note to get started.";
        AutomationProperties.SetName(MemoryEmptyStateText, ar ? "حالة الذاكرة الفارغة" : "Memory empty state");
        AutomationProperties.SetHelpText(MemoryEmptyStateText, ar ? "استخدم زر ملاحظة جديدة لإضافة محتوى إلى الذاكرة." : "Use New note to add content to memory.");
        MemorySearchInput.ToolTip = ar ? "بحث في الملاحظات" : "Search memory notes";
        System.Windows.Automation.AutomationProperties.SetName(MemorySearchInput, ar ? "البحث في الذاكرة" : "Search memory notes");
        MemorySearchButton.Content = ar ? "بحث" : "Search";
        NewMemoryButton.Content = ar ? "ملاحظة جديدة" : "New note";
        MemoryTitleLabel.Text = ar ? "العنوان" : "Title";
        MemoryContentLabel.Text = ar ? "المحتوى" : "Content";
        System.Windows.Automation.AutomationProperties.SetName(MemoryTitleInput, MemoryTitleLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(MemoryContentInput, MemoryContentLabel.Text);
        SaveMemoryButton.Content = ar ? "حفظ الملاحظة" : "Save note";
        DeleteMemoryButton.Content = ar ? "حذف الملاحظة" : "Delete note";
        ExportMemoryButton.Content = ar ? "تصدير JSON" : "Export JSON";
        MemoryPrivacyNote.Text = ar ? "لا تحفظ مفاتيح API أو كلمات المرور في الذاكرة. ملف JSON المُصدَّر غير مشفّر." : "Do not store API keys or passwords in memory. Exported JSON is unencrypted.";

        AppRuntime.Tray?.SetLanguage(AppRuntime.Settings.Language);
        AppRuntime.MascotWindow?.UpdateLanguage();
        RefreshProviderStatus();
    }

    private void SetChatActivityStatus(string message) =>
        SetAccessibleStatusText(ChatActivityStatus, message);

    private static string TranslateChatActivityStatus(string message, bool arabic) => message switch
    {
        "Waiting for the first response chunk..." or "بانتظار أول جزء من الرد..." =>
            arabic ? "بانتظار أول جزء من الرد..." : "Waiting for the first response chunk...",
        "Layal is responding..." or "ليال بتكتب..." =>
            arabic ? "ليال بتكتب..." : "Layal is responding...",
        "Response completed" or "اكتمل الرد" =>
            arabic ? "اكتمل الرد" : "Response completed",
        "Cancelled" or "تم الإيقاف" =>
            arabic ? "تم الإيقاف" : "Cancelled",
        "Request failed" or "فشل الطلب" =>
            arabic ? "فشل الطلب" : "Request failed",
        "Cancel the active response before starting a new conversation." or "أوقف الرد الحالي قبل بدء محادثة جديدة." =>
            arabic ? "أوقف الرد الحالي قبل بدء محادثة جديدة." : "Cancel the active response before starting a new conversation.",
        "New conversation started. Previous conversations remain saved in the selector." or "بدأت محادثة جديدة؛ المحادثات السابقة ما زالت محفوظة ويمكن اختيارها من القائمة." =>
            arabic ? "بدأت محادثة جديدة؛ المحادثات السابقة ما زالت محفوظة ويمكن اختيارها من القائمة." : "New conversation started. Previous conversations remain saved in the selector.",
        "Cancel the active response before clearing history." or "أوقف الرد الحالي قبل مسح السجل." =>
            arabic ? "أوقف الرد الحالي قبل مسح السجل." : "Cancel the active response before clearing history.",
        "Saved chat history was cleared. A fresh conversation is ready." or "تم مسح سجل المحادثات المحفوظ وبدء محادثة جديدة." =>
            arabic ? "تم مسح سجل المحادثات المحفوظ وبدء محادثة جديدة." : "Saved chat history was cleared. A fresh conversation is ready.",
        "Cancel the active response before switching conversations." or "أوقف الرد الحالي قبل تبديل المحادثة." =>
            arabic ? "أوقف الرد الحالي قبل تبديل المحادثة." : "Cancel the active response before switching conversations.",
        "Saved conversation loaded." or "تم تحميل المحادثة المحفوظة." =>
            arabic ? "تم تحميل المحادثة المحفوظة." : "Saved conversation loaded.",
        _ => message
    };

    private void SetAccessibleStatusText(FrameworkElement element, string message)
    {
        var previous = element switch
        {
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.Text,
            _ => throw new ArgumentException("The status element must be a TextBlock or TextBox.", nameof(element))
        };
        if (string.Equals(previous, message, StringComparison.Ordinal))
            return;

        switch (element)
        {
            case TextBlock textBlock:
                textBlock.Text = message;
                break;
            case TextBox textBox:
                textBox.Text = message;
                break;
        }

        if (!string.IsNullOrWhiteSpace(message))
            NotifyLiveRegion(element);
    }

    private static void NotifyLiveRegion(FrameworkElement element)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(element)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch
        {
            // A UI Automation client failure must never interrupt the user action itself.
        }
    }

    private void RefreshProviderStatus()
    {
        var previousConnectionStatus = BidiTextNormalizer.StripDirectionalControls(ConnectionStatusText.Text);
        var hasKey = !string.IsNullOrWhiteSpace(AppRuntime.SecretStore?.Load());
        var hasConfig = Uri.TryCreate(AppRuntime.Settings.ProviderBaseUrl, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                        !string.IsNullOrWhiteSpace(AppRuntime.Settings.ModelId);
        var ready = hasKey && hasConfig;
        var tested = ready && _providerTestPassed && string.Equals(_testedBaseUrl, AppRuntime.Settings.ProviderBaseUrl, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(_testedModelId, AppRuntime.Settings.ModelId, StringComparison.Ordinal);
        ConnectionStatusText.Text = tested ? (IsArabic ? "AI: الاتصال مختبَر" : "AI: Connection tested")
            : ready ? (IsArabic ? "AI: الإعداد محفوظ؛ غير مختبَر" : "AI: Configured; not tested")
            : (IsArabic ? "AI: غير متصل" : "AI: Not connected");
        ConnectionStatusText.Foreground = tested ? ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen) : ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        CardAiValue.Text = tested ? (IsArabic ? "تم اختبار الاتصال" : "Connection tested") : ready ? (IsArabic ? "إعداد محفوظ — لم يُختبر" : "Configured — not tested") : (IsArabic ? "غير متصل" : "Not connected");
        CardAiValue.Foreground = tested ? ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen) : ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        ChatProviderStatus.Text = tested ? (IsArabic ? "تم اختبار نقطة الاتصال في هذه الجلسة" : "Endpoint tested in this session")
            : ready ? (IsArabic ? "إعداد محفوظ؛ اختبر الاتصال" : "Configured; test connection")
            : (IsArabic ? "لا يوجد اتصال مختبَر" : "Not connected/tested");

        NormalizeBidiTextInTree();
        var currentConnectionStatus = BidiTextNormalizer.StripDirectionalControls(ConnectionStatusText.Text);
        if (IsLoaded && !string.Equals(previousConnectionStatus, currentConnectionStatus, StringComparison.Ordinal))
            NotifyLiveRegion(ConnectionStatusText);
    }

    private void NormalizeBidiTextInTree()
    {
        // Preserve the RTL layout of Arabic screens while isolating embedded Latin terms
        // such as English labels, API names and file formats from the surrounding direction.
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(this);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;

            if (current is Window currentWindow)
                currentWindow.Title = BidiTextNormalizer.NormalizeMixedText(currentWindow.Title);

            if (current is TextBlock textBlock &&
                !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty))
            {
                var hasArabic = ContainsArabic(textBlock.Text);
                // Localize text direction without flipping the containing visual tree.
                textBlock.FlowDirection = FlowDirection.LeftToRight;
                textBlock.TextAlignment = hasArabic ? TextAlignment.Right : TextAlignment.Left;
                textBlock.Text = BidiTextNormalizer.NormalizeMixedText(textBlock.Text);
            }

            if (current is ContentControl contentControl &&
                contentControl.Content is string content &&
                !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty))
            {
                contentControl.Content = BidiTextNormalizer.NormalizeMixedText(content);
            }

            if (current is HeaderedContentControl headeredContent &&
                headeredContent.Header is string header &&
                !BindingOperations.IsDataBound(headeredContent, HeaderedContentControl.HeaderProperty))
            {
                headeredContent.Header = BidiTextNormalizer.NormalizeMixedText(header);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dependencyChild)
                    pending.Push(dependencyChild);
            }

            // Intentionally traverse the logical tree only. Template-generated visual children
            // (e.g. AccessText inside a Button) must remain bound to their owning Content property.
        }
    }

    private void SetLanguage(string language)
    {
        AppRuntime.ChatSessionService.SetDefaultTitle(language == "ar" ? "محادثة جديدة" : "New conversation");
        AppRuntime.Settings.Language = language;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        ChatHistorySelector.Items.Refresh();
        ApplyLanguage();
        AppRuntime.Tray?.SetLanguage(language);
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

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape && _voiceConversationCancellation is not null)
        {
            _voiceConversationCancellation.Cancel();
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
            Key.D1 or Key.NumPad1 => HomeTab,
            Key.D2 or Key.NumPad2 => ChatTab,
            Key.D3 or Key.NumPad3 => TasksTab,
            Key.D4 or Key.NumPad4 => SkillsTab,
            Key.D5 or Key.NumPad5 => CamerasTab,
            Key.D6 or Key.NumPad6 => MemoryTab,
            Key.D7 or Key.NumPad7 => SettingsTab,
            _ => null
        };

        if (requestedTab is null)
            return;

        MainTabs.SelectedItem = requestedTab;
        MainTabs.UpdateLayout();

        if (requestedTab == ChatTab)
        {
            ChatInput.Focus();
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
              "Ctrl+1 — الرئيسية\n" +
              "Ctrl+2 — المحادثة (ينقل التركيز إلى حقل الرسالة)\n" +
              "Ctrl+3 — المهام\n" +
              "Ctrl+4 — المهارات\n" +
              "Ctrl+5 — الكاميرات\n" +
              "Ctrl+6 — الذاكرة\n" +
              "Ctrl+7 — الإعدادات\n" +
              "F1 — عرض هذه المساعدة\n\n" +
              "استخدم Tab للتنقل للأمام وShift+Tab للرجوع. استخدم الأسهم داخل قوائم التبويبات والقوائم، وSpace لتبديل مربعات الاختيار، والأسهم لتعديل أشرطة التمرير. في حقل المحادثة، Enter يرسل الرسالة."
            : "Keyboard shortcuts\n\n" +
              "Ctrl+1 — Home\n" +
              "Ctrl+2 — Chat (focuses the message field)\n" +
              "Ctrl+3 — Tasks\n" +
              "Ctrl+4 — Skills\n" +
              "Ctrl+5 — Cameras\n" +
              "Ctrl+6 — Memory\n" +
              "Ctrl+7 — Settings\n" +
              "F1 — Show this help\n\n" +
              "Use Tab to move forward and Shift+Tab to move backward. Use the arrow keys inside tab and list controls, Space to toggle checkboxes, and the arrow keys to adjust sliders. In the chat field, Enter sends the message.";

        MessageBox.Show(
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
    private void GoChatButton_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = ChatTab;
    private void GoSettingsButton_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = SettingsTab;
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void OpenNotepadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = SafeWindowsTools.LaunchNotepad();
            var displayResult = UiLocalization.FormatNotepadResult(result, IsArabic);
            SetAccessibleStatusText(HomeResult, displayResult);
            SetAccessibleStatusText(TaskResult, displayResult);
            AppRuntime.MascotWindow?.SetMood("success");
        }
        catch (Exception ex)
        {
            var message = UiLocalization.FormatTechnicalError("تعذر فتح المفكرة", "Could not launch Notepad", ex, IsArabic);
            SetAccessibleStatusText(HomeResult, message);
            SetAccessibleStatusText(TaskResult, message);
            AppRuntime.MascotWindow?.SetMood("error");
        }
    }

    private void RefreshApiConnections(string? selectedName = null)
    {
        if (ApiConnectionList is null || AppRuntime.ApiConnectionService is null) return;
        ApiConnections.Clear();
        foreach (var profile in AppRuntime.ApiConnectionService.GetAll())
            ApiConnections.Add(profile);

        var selected = ApiConnections.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(selectedName) &&
            string.Equals(x.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        selected ??= ApiConnections.FirstOrDefault();
        ApiConnectionList.SelectedItem = selected;
        if (selected is null) ClearApiConnectionForm();
    }

    private void ClearApiConnectionForm()
    {
        ApiConnectionNameInput.Text = string.Empty;
        ApiConnectionBaseUrlInput.Text = string.Empty;
        ApiAuthHeaderNameInput.Text = "X-API-Key";
        ApiAuthSecretInput.Clear();
        ApiAdditionalHeadersInput.Clear();
        ApiTestPathInput.Text = string.Empty;
        ApiInstructionsInput.Clear();
        ApiAuthTypeComboBox.SelectedIndex = 1;
    }

    private string SelectedApiAuthenticationType =>
        (ApiAuthTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Bearer";

    private ApiConnectionProfile BuildApiConnectionProfile()
    {
        var name = ApiConnectionNameInput.Text.Trim();
        var existing = AppRuntime.ApiConnectionService.Find(name);
        var secret = ApiAuthSecretInput.Password;
        if (string.IsNullOrWhiteSpace(secret) && existing is not null)
            secret = existing.AuthSecret;

        return new ApiConnectionProfile
        {
            Name = name,
            BaseUrl = ApiConnectionBaseUrlInput.Text.Trim(),
            AuthenticationType = SelectedApiAuthenticationType,
            AuthHeaderName = ApiAuthHeaderNameInput.Text.Trim(),
            AuthSecret = secret,
            AdditionalHeadersJson = ApiAdditionalHeadersInput.Text.Trim(),
            TestPath = ApiTestPathInput.Text.Trim(),
            Instructions = ApiInstructionsInput.Text.Trim()
        };
    }

    private void ApiConnectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApiConnectionList.SelectedItem is not ApiConnectionProfile profile)
            return;

        ApiConnectionNameInput.Text = profile.Name;
        ApiConnectionBaseUrlInput.Text = profile.BaseUrl;
        ApiAuthHeaderNameInput.Text = profile.AuthHeaderName;
        ApiAuthSecretInput.Password = profile.AuthSecret;
        ApiAdditionalHeadersInput.Text = profile.AdditionalHeadersJson;
        ApiTestPathInput.Text = profile.TestPath;
        ApiInstructionsInput.Text = profile.Instructions;
        ApiAuthTypeComboBox.SelectedItem = ApiAuthTypeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), profile.AuthenticationType, StringComparison.Ordinal));
    }

    private void NewApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        ApiConnectionList.SelectedItem = null;
        ClearApiConnectionForm();
        ApiConnectionsStatus.Text = IsArabic ? "أدخل بيانات الاتصال واحفظها قبل استخدامها في المهام." : "Enter connection details and save before using it in tasks.";
        ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
        ApiConnectionNameInput.Focus();
    }

    private void SaveApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = BuildApiConnectionProfile();
            AppRuntime.ApiConnectionService.Save(profile);
            RefreshApiConnections(profile.Name);
            ApiConnectionsStatus.Text = IsArabic
                ? $"تم حفظ اتصال API «{profile.Name}» في مخزن محمي. اختبره قبل استخدامه."
                : $"API connection '{profile.Name}' was saved in encrypted storage. Test it before using it.";
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = (IsArabic ? "تعذر حفظ اتصال API: " : "Could not save API connection: ") + ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private async void TestApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        ApiConnectionProfile profile;
        try { profile = BuildApiConnectionProfile(); }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        TestApiConnectionButton.IsEnabled = false;
        ApiConnectionsStatus.Text = IsArabic ? "جارٍ إرسال طلب GET لمسار الاختبار..." : "Sending GET request to the configured test path...";
        ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.SteelBlue;
        try
        {
            var result = await AppRuntime.ApiConnectionService.TestAsync(profile, profile.TestPath);
            var bodyPreview = result.Body.Length > 700 ? result.Body[..700] + "…" : result.Body;
            ApiConnectionsStatus.Text = $"HTTP {result.StatusCode} {result.ReasonPhrase}{Environment.NewLine}{result.RequestUrl}{Environment.NewLine}{bodyPreview}";
            ApiConnectionsStatus.Foreground = result.StatusCode is >= 200 and < 300
                ? System.Windows.Media.Brushes.ForestGreen
                : System.Windows.Media.Brushes.DarkGoldenrod;
        }
        catch (Exception ex)
        {
            ApiConnectionsStatus.Text = (IsArabic ? "فشل اختبار API: " : "API test failed: ") + ex.Message;
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
        finally { TestApiConnectionButton.IsEnabled = true; }
    }

    private void DeleteApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ApiConnectionNameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || AppRuntime.ApiConnectionService.Find(name) is null)
        {
            ApiConnectionsStatus.Text = IsArabic ? "حدد اتصالًا محفوظًا لحذفه." : "Select a saved connection to delete.";
            ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            return;
        }

        var answer = MessageBox.Show(this,
            IsArabic ? $"هل تريد حذف اتصال API «{name}» وبيانات المصادقة المحفوظة؟" : $"Delete API connection '{name}' and its stored authentication data?",
            IsArabic ? "تأكيد حذف الاتصال" : "Confirm API connection deletion",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        AppRuntime.ApiConnectionService.Delete(name);
        RefreshApiConnections();
        ApiConnectionsStatus.Text = IsArabic ? "تم حذف الاتصال." : "Connection deleted.";
        ApiConnectionsStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
    }

    private void SaveProviderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = BaseUrlInput.Text.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException(IsArabic ? "اكتب عنوان HTTP/HTTPS صحيحًا دون بيانات دخول داخل الرابط." : "Enter a valid HTTP/HTTPS base URL without embedded credentials.");
            var model = ModelInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException(IsArabic ? "معرّف النموذج مطلوب." : "Model ID is required.");
            var speechModel = SpeechModelInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(speechModel))
                throw new InvalidOperationException(IsArabic ? "معرّف نموذج تحويل الكلام إلى نص مطلوب." : "Speech-to-text model ID is required.");
            var voiceModel = VoiceReplyModelInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(voiceModel))
                throw new InvalidOperationException(IsArabic ? "معرّف نموذج الصوت مطلوب." : "Voice model ID is required.");
            var voiceId = VoiceReplyVoiceSelector.SelectedValue?.ToString() ?? LayalSpeechService.DefaultVoiceId;
            if (!LayalSpeechService.IsSupportedVoice(voiceId))
                throw new InvalidOperationException(IsArabic ? "اختر نبرة صوت مدعومة لليال." : "Choose a supported Layal voice preset.");

            AppRuntime.Settings.ProviderBaseUrl = baseUrl;
            AppRuntime.Settings.ModelId = model;
            AppRuntime.Settings.SpeechToTextModelId = speechModel;
            AppRuntime.Settings.VoiceRepliesEnabled = VoiceReplyCheckbox.IsChecked == true;
            AppRuntime.Settings.VoiceReplyModelId = voiceModel;
            AppRuntime.Settings.VoiceReplyId = voiceId.ToLowerInvariant();
            _providerTestPassed = false;
            if (!string.IsNullOrWhiteSpace(ApiKeyInput.Password))
            {
                AppRuntime.SecretStore.Save(ApiKeyInput.Password);
                ApiKeyInput.Clear();
            }
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم حفظ الإعدادات. لم يتم تأكيد الاتصال حتى الآن؛ استخدم اختبار الاتصال." : "Settings saved. Connectivity is not confirmed yet; run the connection test.");
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
            RefreshProviderStatus();
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(ProviderActionStatus, UiLocalization.FormatTechnicalError("تعذر حفظ الإعدادات", "Could not save settings", ex, IsArabic));
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private void VoiceReplySettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppRuntime.Settings.VoiceRepliesEnabled = VoiceReplyCheckbox.IsChecked == true;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        SetAccessibleStatusText(ProviderActionStatus, IsArabic
            ? (AppRuntime.Settings.VoiceRepliesEnabled ? "تم تفعيل قراءة ليال لردودها صوتيًا." : "تم إيقاف الردود الصوتية لليال.")
            : (AppRuntime.Settings.VoiceRepliesEnabled ? "Layal voice replies are enabled." : "Layal voice replies are disabled."));
    }

    private void VoiceReplyVoiceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var voiceId = VoiceReplyVoiceSelector.SelectedValue?.ToString();
        if (!LayalSpeechService.IsSupportedVoice(voiceId)) return;
        AppRuntime.Settings.VoiceReplyId = voiceId!.ToLowerInvariant();
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
    }

    private async void TestVoiceReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceTestCancellation is not null) return;
        if (_chatCancellation is not null)
        {
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "أوقف الرد الحالي قبل تجربة الصوت."
                : "Stop the active reply before previewing the voice.");
            return;
        }

        var baseUrl = BaseUrlInput.Text.Trim();
        var apiKey = string.IsNullOrWhiteSpace(ApiKeyInput.Password)
            ? (AppRuntime.SecretStore?.Load() ?? string.Empty)
            : ApiKeyInput.Password;
        var model = VoiceReplyModelInput.Text.Trim();
        var voiceId = VoiceReplyVoiceSelector.SelectedValue?.ToString() ?? LayalSpeechService.DefaultVoiceId;
        var cancellation = new CancellationTokenSource();
        _voiceTestCancellation = cancellation;
        TestVoiceReplyButton.IsEnabled = false;
        try
        {
            await _layalSpeech.SpeakAsync(
                baseUrl, apiKey, model, voiceId,
                "أهلًا، أنا ليال. خلّينا نتكلم بهدوء، خطوة بخطوة. I will speak gently and clearly.",
                cancellation.Token);
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "انتهت تجربة صوت ليال."
                : "Layal voice preview completed.");
        }
        catch (OperationCanceledException)
        {
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "تم إيقاف تجربة الصوت."
                : "Voice preview cancelled.");
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(ProviderActionStatus,
                UiLocalization.FormatTechnicalError("تعذر تشغيل صوت ليال", "Could not play Layal voice", ex, IsArabic));
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
        finally
        {
            if (ReferenceEquals(_voiceTestCancellation, cancellation))
                _voiceTestCancellation = null;
            cancellation.Dispose();
            if (IsLoaded) TestVoiceReplyButton.IsEnabled = true;
        }
    }

    private async void TestProviderButton_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlInput.Text.Trim();
        var key = string.IsNullOrWhiteSpace(ApiKeyInput.Password) ? AppRuntime.SecretStore.Load() : ApiKeyInput.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "أدخل مفتاح API ثم اختبر الاتصال. لن يتم حفظه إلا إذا ضغطت حفظ الإعدادات." : "Enter an API key to test. It is saved only when you select Save settings.");
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            return;
        }
        TestProviderButton.IsEnabled = false;
        SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "جارٍ اختبار الاتصال الفعلي..." : "Testing the actual endpoint...");
        ProviderActionStatus.Foreground = ThemeBrush("Layal.Cyan", System.Windows.Media.Brushes.SteelBlue);
        try
        {
            var result = await _aiClient.ProbeAsync(baseUrl, key);
            _providerTestPassed = true;
            _testedBaseUrl = baseUrl.TrimEnd('/');
            _testedModelId = ModelInput.Text.Trim();
            var testSummary = IsArabic ? $"نجح الاختبار: {result}" : $"Test succeeded: {result}";
            SetAccessibleStatusText(ProviderActionStatus, UiLocalization.TranslateKnownFeedback(testSummary, IsArabic));
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            _providerTestPassed = false;
            SetAccessibleStatusText(ProviderActionStatus, UiLocalization.FormatTechnicalError("فشل اختبار الاتصال", "Connection test failed", ex, IsArabic));
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
        finally
        {
            TestProviderButton.IsEnabled = true;
            RefreshProviderStatus();
        }
    }

    private void DeleteKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            IsArabic ? "هل تريد حذف مفتاح AI المحفوظ على حساب Windows الحالي؟" : "Delete the saved AI key for the current Windows user?",
            IsArabic ? "تأكيد حذف المفتاح" : "Confirm key deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        AppRuntime.SecretStore.Delete();
        ApiKeyInput.Clear();
        SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم حذف المفتاح المحفوظ." : "Saved key deleted.");
        ProviderActionStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        RefreshProviderStatus();
    }

    private void MascotSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppRuntime.Settings.MascotAlwaysOnTop = AlwaysOnTopCheckbox.IsChecked == true;
        AppRuntime.Settings.MascotAnimationsEnabled = AnimationsCheckbox.IsChecked == true;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        if (AppRuntime.MascotWindow is not null) AppRuntime.MascotWindow.Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
    }

    private void WindowInspectionSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppRuntime.Settings.AllowWindowTitleInspection = WindowInspectionCheckbox.IsChecked == true;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
    }

    private void ApplyMascotSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AppRuntime.Settings.MascotAlwaysOnTop = AlwaysOnTopCheckbox.IsChecked == true;
        AppRuntime.Settings.MascotAnimationsEnabled = AnimationsCheckbox.IsChecked == true;
        AppRuntime.Settings.MascotScale = Math.Clamp(MascotSizeSlider.Value, 0.65, 1.8);
        AppRuntime.Settings.MascotOpacity = Math.Clamp(MascotOpacitySlider.Value, 0.35, 1.0);
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        if (AppRuntime.MascotWindow is not null)
        {
            AppRuntime.MascotWindow.Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
            AppRuntime.MascotWindow.Opacity = AppRuntime.Settings.MascotOpacity;
            AppRuntime.MascotWindow.ApplyScale(AppRuntime.Settings.MascotScale);
        }
        SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم تطبيق إعدادات الشخصية." : "Mascot settings applied.");
    }

    private async void SpeechToTextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speechCancellation is not null)
        {
            _speechCancellation.Cancel();
            SetChatActivityStatus(IsArabic ? "جارٍ إلغاء تحويل الصوت..." : "Cancelling speech transcription...");
            return;
        }
        if (_speechIsStopping) return;

        if (_speechToText.IsRecording)
        {
            _speechRecordingTimer.Stop();
            await StopSpeechRecordingAndTranscribeAsync();
            return;
        }

        var baseUrl = AppRuntime.Settings.ProviderBaseUrl;
        var apiKey = AppRuntime.SecretStore?.Load() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetChatActivityStatus(IsArabic
                ? "أدخل مفتاح API صالحًا في الإعدادات قبل استخدام تحويل الصوت."
                : "Save a valid API key in Settings before using speech input.");
            MainTabs.SelectedItem = SettingsTab;
            return;
        }
        try { _ = SpeechToTextService.BuildTranscriptionUri(baseUrl); }
        catch (InvalidOperationException ex)
        {
            SetChatActivityStatus(ex.Message);
            MainTabs.SelectedItem = SettingsTab;
            return;
        }

        var microphoneConsent = MessageBox.Show(
            this,
            IsArabic
                ? "سيبدأ التسجيل فقط بعد موافقتك. التسجيل يدوي وبحد أقصى دقيقتين، ويظل الصوت في الذاكرة ولا يُحفظ كملف. بعد الإيقاف ستُسأل مرة أخرى قبل إرسال الصوت إلى موفر API. هل تريد تشغيل الميكروفون الآن؟"
                : "Recording starts only after you agree. It is manual, limited to two minutes, and kept in memory rather than saved as a file. After you stop, you will be asked again before audio is sent to your API provider. Start the microphone now?",
            IsArabic ? "السماح بالتسجيل اليدوي" : "Allow manual microphone recording",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (microphoneConsent != MessageBoxResult.Yes) return;

        try
        {
            _speechToText.StartRecording();
            _speechRecordingStartedAt = DateTimeOffset.UtcNow;
            _speechRecordingTimer.Start();
            UpdateSpeechToTextButton();
            SetChatActivityStatus(IsArabic
                ? "الميكروفون يعمل الآن. اضغط «إيقاف وتحويل» عند الانتهاء؛ لا يتم الإرسال قبل تأكيدك."
                : "Microphone recording is active. Press Stop & transcribe when done; nothing is uploaded until you confirm.");
        }
        catch (Exception ex)
        {
            _speechRecordingTimer.Stop();
            SetChatActivityStatus((IsArabic ? "تعذر بدء التسجيل: " : "Could not start recording: ") + ex.Message);
            UpdateSpeechToTextButton();
        }
    }

    private void SpeechRecordingTimer_Tick(object? sender, EventArgs e)
    {
        if (!_speechToText.IsRecording) return;
        if (DateTimeOffset.UtcNow - _speechRecordingStartedAt < SpeechToTextService.MaximumRecordingDuration)
            return;
        _speechRecordingTimer.Stop();
        _ = StopSpeechRecordingAndTranscribeAsync();
    }

    private async Task StopSpeechRecordingAndTranscribeAsync()
    {
        if (_speechIsStopping || !_speechToText.IsRecording) return;
        _speechIsStopping = true;
        _speechRecordingTimer.Stop();
        UpdateSpeechToTextButton();
        byte[]? audio = null;
        try
        {
            var recording = await _speechToText.StopRecordingAsync();
            audio = recording.WavBytes;
            _speechIsStopping = false;
            UpdateSpeechToTextButton();

            // If the window was hidden while StopRecordingAsync yielded, discard the clip.
            if (!IsVisible || AppRuntime.IsExiting)
            {
                SetChatActivityStatus(IsArabic ? "تم إلغاء التسجيل والتخلص من الصوت المؤقت." : "Recording cancelled; temporary audio discarded.");
                return;
            }

            var endpoint = SpeechToTextService.BuildTranscriptionUri(AppRuntime.Settings.ProviderBaseUrl);
            var host = endpoint.GetLeftPart(UriPartial.Authority);
            var durationNote = recording.MaximumDurationReached
                ? (IsArabic ? "\nوصل التسجيل إلى حد الدقيقتين؛ سيتم إرسال الجزء المسجل فقط." : "\nThe two-minute limit was reached; only the captured segment will be sent.")
                : string.Empty;
            var uploadConsent = MessageBox.Show(
                this,
                IsArabic
                    ? $"سيتم إرسال التسجيل إلى موفر API التالي لتحويله إلى نص:\n{host}{durationNote}\nلن يُحفظ الصوت الخام على القرص. هل تؤكد الإرسال؟"
                    : $"This recording will be uploaded to the following API provider for transcription:\n{host}{durationNote}\nThe raw recording is not saved to disk. Do you approve sending it?",
                IsArabic ? "تأكيد إرسال الصوت" : "Confirm audio upload",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (uploadConsent != MessageBoxResult.Yes)
            {
                SetChatActivityStatus(IsArabic ? "تم إلغاء الإرسال والتخلص من التسجيل المؤقت." : "Upload cancelled; temporary audio discarded.");
                return;
            }

            var key = AppRuntime.SecretStore?.Load() ?? string.Empty;
            var modelId = AppRuntime.Settings.SpeechToTextModelId;
            var tokenSource = new CancellationTokenSource();
            _speechCancellation = tokenSource;
            UpdateSpeechToTextButton();
            SetChatActivityStatus(IsArabic
                ? "جارٍ تحويل الصوت إلى نص. اضغط زر الصوت لإلغاء العملية."
                : "Transcribing audio. Press the voice button to cancel.");
            try
            {
                var transcript = await _speechToText.TranscribeAsync(
                    AppRuntime.Settings.ProviderBaseUrl, key, modelId, audio, tokenSource.Token);
                ChatInput.Text = string.IsNullOrWhiteSpace(ChatInput.Text)
                    ? transcript
                    : ChatInput.Text.TrimEnd() + Environment.NewLine + transcript;
                ChatInput.CaretIndex = ChatInput.Text.Length;
                ChatInput.Focus();
                SetChatActivityStatus(IsArabic
                    ? "تم تحويل الصوت. راجع النص وعدّله ثم اضغط إرسال؛ لم تُرسل الرسالة تلقائيًا."
                    : "Transcription complete. Review or edit the text, then press Send; it was not sent automatically.");
            }
            catch (OperationCanceledException)
            {
                SetChatActivityStatus(IsArabic ? "تم إلغاء تحويل الصوت والتخلص من التسجيل المؤقت." : "Speech transcription cancelled; temporary audio discarded.");
            }
            catch (Exception ex)
            {
                SetChatActivityStatus((IsArabic ? "فشل تحويل الصوت: " : "Speech transcription failed: ") + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_speechCancellation, tokenSource))
                {
                    _speechCancellation = null;
                    tokenSource.Dispose();
                }
                UpdateSpeechToTextButton();
            }
        }
        catch (OperationCanceledException)
        {
            SetChatActivityStatus(IsArabic ? "تم إلغاء التسجيل." : "Recording cancelled.");
        }
        catch (Exception ex)
        {
            SetChatActivityStatus((IsArabic ? "تعذر إيقاف التسجيل بأمان: " : "Could not finish microphone recording: ") + ex.Message);
        }
        finally
        {
            if (audio is not null) Array.Clear(audio, 0, audio.Length);
            _speechIsStopping = false;
            UpdateSpeechToTextButton();
        }
    }

    private void UpdateSpeechToTextButton()
    {
        SpeechToTextButton.Content = _speechToText.IsRecording
            ? (IsArabic ? "إيقاف وتحويل" : "Stop & transcribe")
            : _speechIsStopping
                ? (IsArabic ? "جارٍ إيقاف التسجيل…" : "Stopping recording…")
                : _speechCancellation is not null
                    ? (IsArabic ? "إلغاء التحويل" : "Cancel transcription")
                    : (IsArabic ? "تسجيل صوت" : "Record voice");
        SpeechToTextButton.IsEnabled = !_speechIsStopping;
        SpeechToTextButton.ToolTip = _speechToText.IsRecording
            ? (IsArabic ? "اضغط لإيقاف التسجيل. لن يُرسل الصوت إلا بعد تأكيد منفصل." : "Press to stop recording. Audio is uploaded only after separate confirmation.")
            : _speechCancellation is not null
                ? (IsArabic ? "إلغاء طلب تحويل الصوت الحالي." : "Cancel the current speech transcription request.")
                : (IsArabic ? "تسجيل يدوي فقط؛ لن يُرسل الصوت قبل تأكيدك." : "Manual recording only; audio is not uploaded until you confirm.");
    }

    private async void VoiceConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceConversationCancellation is not null)
        {
            SetChatActivityStatus(IsArabic ? "جارٍ إيقاف المحادثة والميكروفون..." : "Stopping the voice conversation and microphone...");
            VoiceConversationMicStatus.Text = IsArabic ? "جارٍ إيقاف الميكروفون..." : "Stopping microphone...";
            VoiceConversationMicDot.Fill = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            _voiceConversationCancellation.Cancel();
            return;
        }

        if (_chatCancellation is not null || _voiceTestCancellation is not null ||
            _speechToText.IsRecording || _speechCancellation is not null || _speechIsStopping)
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف عملية الصوت أو الرد الحالي قبل بدء المحادثة الصوتية."
                : "Stop the current audio operation or text reply before starting a voice conversation.");
            return;
        }

        var baseUrl = BaseUrlInput.Text.Trim().TrimEnd('/');
        var apiKey = string.IsNullOrWhiteSpace(ApiKeyInput.Password)
            ? (AppRuntime.SecretStore?.Load() ?? string.Empty)
            : ApiKeyInput.Password;
        var modelId = VoiceReplyModelInput.Text.Trim();
        var voiceId = VoiceReplyVoiceSelector.SelectedValue?.ToString() ?? LayalSpeechService.DefaultVoiceId;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetChatActivityStatus(IsArabic
                ? "أدخل مفتاح API صالحًا واحفظه من الإعدادات قبل بدء المحادثة الصوتية."
                : "Enter and save a valid API key in Settings before starting a voice conversation.");
            MainTabs.SelectedItem = SettingsTab;
            return;
        }

        Uri realtimeUri;
        try
        {
            realtimeUri = LayalSpeechService.BuildRealtimeUri(baseUrl, modelId);
            if (!LayalSpeechService.IsSupportedVoice(voiceId))
                throw new InvalidOperationException(IsArabic ? "اختر صوتًا مدعومًا." : "Choose a supported voice.");
        }
        catch (Exception ex)
        {
            SetChatActivityStatus((IsArabic ? "إعداد الصوت غير صالح: " : "Invalid voice configuration: ") + ex.Message);
            MainTabs.SelectedItem = SettingsTab;
            return;
        }

        var approved = MessageBox.Show(
            this,
            IsArabic
                ? $"ستستخدم ليال الميكروفون باستمرار أثناء المحادثة، وسيُبث الصوت مباشرة إلى موفر Realtime التالي:\n{realtimeUri.GetLeftPart(UriPartial.Authority)}\nلن يُحفظ التسجيل الخام على القرص. قد تترتب تكلفة استخدام إضافية. يمكنك إيقاف الميكروفون في أي وقت بزر إيقاف المحادثة أو زر Esc. هل تريد البدء؟"
                : $"Layal will keep the microphone active during the conversation and stream audio directly to this Realtime provider:\n{realtimeUri.GetLeftPart(UriPartial.Authority)}\nRaw recordings are not saved to disk. Additional usage charges may apply. Stop the conversation or press Esc to turn the microphone off at any time. Start now?",
            IsArabic ? "تأكيد تشغيل الميكروفون المباشر" : "Confirm live microphone access",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (approved != MessageBoxResult.Yes) return;

        var cancellation = new CancellationTokenSource();
        var service = new VoiceConversationService();
        _voiceConversationCancellation = cancellation;
        _voiceConversationStatus = VoiceConversationStatus.Connecting;
        service.StatusChanged += VoiceConversationService_StatusChanged;
        service.TranscriptReceived += VoiceConversationService_TranscriptReceived;
        UpdateVoiceConversationControls();
        ChatInput.IsEnabled = false;
        SendButton.IsEnabled = false;
        SpeechToTextButton.IsEnabled = false;
        CancelResponseButton.IsEnabled = false;
        NewConversationButton.IsEnabled = false;
        ClearSavedChatHistoryButton.IsEnabled = false;
        ChatHistorySelector.IsEnabled = false;
        SetChatActivityStatus(IsArabic ? "جارٍ الاتصال وتهيئة الميكروفون..." : "Connecting and preparing the microphone...");

        try
        {
            var characterInstructions = LayalIdentity.ForInterfaceLanguage(AppRuntime.Settings.Language);
            var instructions = characterInstructions + Environment.NewLine + Environment.NewLine +
                "This is a natural, full-duplex spoken conversation with Layal, a fictional young-girl character. " +
                LayalSpeechService.VoiceCharacterDescription + " " +
                "Speak in natural Egyptian Arabic for Egyptian colloquial speech, clear Modern Standard Arabic for formal Arabic, " +
                "and natural conversational English for English. Listen continuously, wait for the user to finish a turn, " +
                "and respond naturally without announcing turn detection. If the user speaks while you are talking, stop your response and listen to them. " +
                "The voice is AI-generated, not a human recording.";

            await service.RunAsync(baseUrl, apiKey, modelId, voiceId, instructions, cancellation.Token);
            SetChatActivityStatus(IsArabic ? "انتهت المحادثة الصوتية." : "Voice conversation ended.");
        }
        catch (OperationCanceledException)
        {
            SetChatActivityStatus(IsArabic ? "تم إيقاف المحادثة الصوتية والميكروفون." : "Voice conversation and microphone stopped.");
        }
        catch (Exception ex)
        {
            var message = UiLocalization.FormatTechnicalError(
                "تعذر بدء/استمرار المحادثة الصوتية",
                "Could not start or continue the voice conversation",
                ex, IsArabic);
            SetChatActivityStatus(message);
            if (!AppRuntime.IsExiting && IsLoaded)
                AddMessage("status", message);
        }
        finally
        {
            service.StatusChanged -= VoiceConversationService_StatusChanged;
            service.TranscriptReceived -= VoiceConversationService_TranscriptReceived;
            if (ReferenceEquals(_voiceConversationCancellation, cancellation))
            {
                _voiceConversationCancellation = null;
                _voiceConversationStatus = VoiceConversationStatus.Stopped;
                cancellation.Dispose();
            }
            service.Dispose();
            UpdateVoiceConversationControls();
            if (IsLoaded)
            {
                ChatInput.IsEnabled = true;
                SendButton.IsEnabled = _chatCancellation is null;
                NewConversationButton.IsEnabled = true;
                ClearSavedChatHistoryButton.IsEnabled = true;
                ChatHistorySelector.IsEnabled = true;
                UpdateSpeechToTextButton();
                RefreshProviderStatus();
            }
        }
    }

    private void VoiceConversationService_StatusChanged(VoiceConversationStatus status, string? detail)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            _voiceConversationStatus = status;
            UpdateVoiceConversationControls();
            if (status != VoiceConversationStatus.Stopped)
                SetChatActivityStatus(VoiceConversationStatusMessage(status));
        }));
    }

    private void VoiceConversationService_TranscriptReceived(string role, string text)
    {
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(text)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded || AppRuntime.IsExiting) return;
            AddMessage(role == "user" ? "user" : "assistant", text.Trim());
            if (role == "assistant")
                AppRuntime.MascotWindow?.SetMood(SelectChatMood(
                    ChatMessages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty, text));
        }));
    }

    private string VoiceConversationStatusMessage(VoiceConversationStatus status) =>
        status switch
        {
            VoiceConversationStatus.Connecting => IsArabic ? "جارٍ الاتصال بموفر Realtime قبل تشغيل الميكروفون..." : "Connecting to Realtime before activating the microphone...",
            VoiceConversationStatus.Listening => IsArabic ? "الميكروفون نشط — تحدث إلى ليال في أي وقت." : "Microphone active — speak to Layal at any time.",
            VoiceConversationStatus.UserSpeaking => IsArabic ? "ليال بتسمعك الآن..." : "Layal is listening to you...",
            VoiceConversationStatus.AssistantSpeaking => IsArabic ? "ليال بترد — تقدر تقاطعها بالكلام." : "Layal is responding — speak to interrupt.",
            _ => IsArabic ? "الميكروفون متوقف." : "Microphone is off."
        };

    private void UpdateVoiceConversationControls()
    {
        if (VoiceConversationButton is null) return;
        var active = _voiceConversationCancellation is not null;
        VoiceConversationButton.Content = active
            ? (IsArabic ? "إيقاف المحادثة الصوتية" : "Stop voice conversation")
            : (IsArabic ? "بدء المحادثة الصوتية" : "Start voice conversation");
        VoiceConversationButton.ToolTip = active
            ? (IsArabic ? "اضغط لإيقاف البث الصوتي وإغلاق الميكروفون فورًا، أو اضغط Esc." : "Stop audio streaming and close the microphone, or press Esc.")
            : (IsArabic ? "تبدأ بث الميكروفون إلى موفر Realtime بعد موافقتك. يمكن إيقافه في أي وقت." : "Streams microphone audio to your Realtime provider after confirmation. You can stop at any time.");
        AutomationProperties.SetName(VoiceConversationButton, active
            ? (IsArabic ? "إيقاف المحادثة الصوتية" : "Stop voice conversation")
            : (IsArabic ? "بدء المحادثة الصوتية" : "Start voice conversation"));
        AutomationProperties.SetHelpText(VoiceConversationButton, IsArabic
            ? "الزر يبدّل بين بدء المحادثة وإيقافها. أثناء الجلسة يظهر مؤشر الميكروفون النشط أعلى النافذة."
            : "Toggles the live voice conversation. A microphone-active indicator remains visible in the window header.");
        var micActive = _voiceConversationStatus is VoiceConversationStatus.Listening or
            VoiceConversationStatus.UserSpeaking or VoiceConversationStatus.AssistantSpeaking;
        VoiceConversationIndicator.Visibility = _voiceConversationStatus == VoiceConversationStatus.Stopped
            ? Visibility.Collapsed
            : Visibility.Visible;
        VoiceConversationMicDot.Fill = ThemeBrush(
            micActive ? "Layal.Danger" : "Layal.Warning",
            micActive ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkGoldenrod);
        VoiceConversationMicStatus.Text = VoiceConversationStatusMessage(_voiceConversationStatus);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async Task SendMessageAsync()
    {
        if (_voiceConversationCancellation is not null)
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف المحادثة الصوتية قبل إرسال رسالة نصية."
                : "Stop the live voice conversation before sending a text message.");
            return;
        }

        var text = ChatInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || _chatCancellation is not null) return;
        ChatInput.Clear();
        AddMessage("user", text);
        var key = AppRuntime.SecretStore.Load();
        if (string.IsNullOrWhiteSpace(key))
        {
            AddMessage("status", IsArabic ? "لم يتم ضبط اتصال حقيقي بعد. افتح الإعدادات وأدخل عنوان API ومفتاحًا صالحًا." : "No real AI connection is configured. Open Settings and provide a valid API URL and key.");
            SetChatActivityStatus(IsArabic
                ? "لا يوجد اتصال حقيقي مضبوط. افتح الإعدادات وأدخل عنوان API ومفتاحًا صالحًا."
                : "No real AI connection is configured. Open Settings and provide a valid API URL and key.");
            MainTabs.SelectedItem = ChatTab;
            return;
        }

        _chatCancellation = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        CancelResponseButton.IsEnabled = true;
        SetChatActivityStatus(IsArabic ? "بانتظار أول جزء من الرد..." : "Waiting for the first response chunk...");
        AppRuntime.MascotWindow?.SetMood("thinking");
        try
        {
            // Keep the most recent chat context bounded; archive history remains saved locally.
            var history = ChatContextBuilder.Build(LayalIdentity.ForInterfaceLanguage(AppRuntime.Settings.Language), ChatMessages);
            ChatMessage? liveAssistantMessage = null;
            var streamedChunks = 0;
            var response = await _aiClient.ChatStreamAsync(
                AppRuntime.Settings.ProviderBaseUrl,
                AppRuntime.Settings.ModelId,
                key,
                history,
                delta =>
                {
                    if (liveAssistantMessage is null)
                    {
                        AppRuntime.MascotWindow?.SetMood("responding");
                        SetChatActivityStatus(IsArabic ? "ليال بتكتب..." : "Layal is responding...");
                        liveAssistantMessage = AddMessage("assistant", delta);
                    }
                    else
                    {
                        liveAssistantMessage.Content += delta;
                        if (liveAssistantMessage.Direction != FlowDirection.RightToLeft && ContainsArabic(delta))
                            liveAssistantMessage.Direction = FlowDirection.RightToLeft;

                        // Avoid re-scanning the entire growing answer and forcing layout on every token.
                        streamedChunks++;
                        if (streamedChunks % 8 == 0)
                            ChatMessagesList.ScrollIntoView(liveAssistantMessage);
                    }
                },
                _chatCancellation.Token);

            if (liveAssistantMessage is null)
                AddMessage("assistant", response);
            else
            {
                if (!string.Equals(liveAssistantMessage.Content, response, StringComparison.Ordinal))
                    liveAssistantMessage.Content = response;
                liveAssistantMessage.Direction = ContainsArabic(liveAssistantMessage.Content)
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                SaveCurrentChatMessages();
            }
            var completionStatus = IsArabic ? "اكتمل الرد" : "Response completed";
            if (AppRuntime.Settings.VoiceRepliesEnabled && !string.IsNullOrWhiteSpace(response))
            {
                SetChatActivityStatus(IsArabic ? "ليال بتتكلم بصوتها..." : "Layal is speaking...");
                try
                {
                    await _layalSpeech.SpeakAsync(
                        AppRuntime.Settings.ProviderBaseUrl,
                        key,
                        AppRuntime.Settings.VoiceReplyModelId,
                        AppRuntime.Settings.VoiceReplyId,
                        response,
                        _chatCancellation.Token);
                    completionStatus = IsArabic ? "اكتمل الرد الصوتي" : "Response completed with voice";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception voiceError)
                {
                    completionStatus = (IsArabic
                        ? "اكتمل الرد النصي، لكن تعذر تشغيل الصوت: "
                        : "Text reply completed, but voice playback failed: ") + voiceError.Message;
                }
            }
            AppRuntime.MascotWindow?.SetMood(SelectChatMood(text, response));
            SetChatActivityStatus(completionStatus);
        }
        catch (OperationCanceledException)
        {
            AddMessage("status", IsArabic ? "تم إيقاف الطلب." : "Request cancelled.");
            AppRuntime.MascotWindow?.SetMood("paused");
            SetChatActivityStatus(IsArabic ? "تم الإيقاف" : "Cancelled");
        }
        catch (Exception ex)
        {
            AddMessage("status", (IsArabic ? "فشل طلب النموذج: " : "Model request failed: ") + ex.Message);
            AppRuntime.MascotWindow?.SetMood("error");
            SetChatActivityStatus(IsArabic ? "فشل الطلب" : "Request failed");
        }
        finally
        {
            SaveCurrentChatMessages();
            _chatCancellation?.Dispose();
            _chatCancellation = null;
            SendButton.IsEnabled = true;
            CancelResponseButton.IsEnabled = false;
            ChatMessagesList.ScrollIntoView(ChatMessages.LastOrDefault());
            RefreshProviderStatus();
        }
    }

    private ChatMessage AddMessage(string role, string content)
    {
        var ar = IsArabic;
        var display = role switch
        {
            "user" => ar ? "أنت" : "You",
            "assistant" => "Layal | ليال",
            _ => ar ? "حالة النظام" : "System status"
        };
        var direction = ContainsArabic(content) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var message = new ChatMessage { Role = role, Content = content, DisplayRole = display, CreatedAt = DateTimeOffset.Now, Direction = direction };
        ChatMessages.Add(message);
        while (ChatMessages.Count > 100) ChatMessages.RemoveAt(0);
        SaveCurrentChatMessages();
        ChatMessagesList.ScrollIntoView(ChatMessages.LastOrDefault());
        return message;
    }

    private void SaveCurrentChatMessages()
    {
        try
        {
            AppRuntime.ChatSessionService.SaveCurrentMessages(ChatMessages.ToList());
            RefreshChatSessions(_currentConversationId);
        }
        catch { /* A history save failure must not block the current conversation. */ }
    }

    private static string SelectChatMood(string userText, string response)
    {
        var input = userText.ToLowerInvariant();
        var answer = response.ToLowerInvariant();

        // Let users preview expressions explicitly as well as trigger them contextually.
        if (input.Contains("غمزة", StringComparison.Ordinal) || input.Contains("غمزي", StringComparison.Ordinal) ||
            input.Contains("wink", StringComparison.Ordinal))
            return "wink";
        if (input.Contains("وش مفاجأة", StringComparison.Ordinal) || input.Contains("اتخضي", StringComparison.Ordinal) ||
            input.Contains("اتفاجئي", StringComparison.Ordinal) || input.Contains("surprised face", StringComparison.Ordinal))
            return "surprised";
        if (input.Contains("وش حيران", StringComparison.Ordinal) || input.Contains("تعبير حيرة", StringComparison.Ordinal) ||
            input.Contains("confused face", StringComparison.Ordinal))
            return "confused";
        if (input.Contains("وش حزين", StringComparison.Ordinal) || input.Contains("وش زعلان", StringComparison.Ordinal) ||
            input.Contains("sad face", StringComparison.Ordinal))
            return "sad";
        if (input.Contains("وش غاضب", StringComparison.Ordinal) || input.Contains("وش عصبي", StringComparison.Ordinal) ||
            input.Contains("angry face", StringComparison.Ordinal))
            return "annoyed";
        if (input.Contains("وش زهقان", StringComparison.Ordinal) || input.Contains("تعبير ملل", StringComparison.Ordinal) ||
            input.Contains("bored face", StringComparison.Ordinal))
            return "bored";
        if (input.Contains("اتكسفي", StringComparison.Ordinal) || input.Contains("خليكي مكسوفة", StringComparison.Ordinal) ||
            input.Contains("shy face", StringComparison.Ordinal))
            return "shy";
        if (input.Contains("تعبيرات الوجه", StringComparison.Ordinal) || input.Contains("تعبيراتك", StringComparison.Ordinal) ||
            input.Contains("رياكشنات وشك", StringComparison.Ordinal) || input.Contains("رياكشناتك", StringComparison.Ordinal) ||
            input.Contains("show your expressions", StringComparison.Ordinal))
            return "playful";

        if (input.Contains("مكسوف", StringComparison.Ordinal) || input.Contains("محرج", StringComparison.Ordinal) ||
            input.Contains("embarrassed", StringComparison.Ordinal))
            return "embarrassed";

        if (input.Contains("ارتحت", StringComparison.Ordinal) || input.Contains("اطمنت", StringComparison.Ordinal) ||
            input.Contains("that's a relief", StringComparison.Ordinal) || input.Contains("relieved", StringComparison.Ordinal))
            return "relieved";

        if (input.Contains("أنا آسف", StringComparison.Ordinal) || input.Contains("انا اسف", StringComparison.Ordinal) ||
            input.Contains("حقك علي", StringComparison.Ordinal) || input.Contains("sorry", StringComparison.Ordinal) ||
            input.Contains("my apologies", StringComparison.Ordinal))
            return "apologetic";

        if (input.Contains("محبط", StringComparison.Ordinal) || input.Contains("زهقت من", StringComparison.Ordinal) ||
            input.Contains("frustrated", StringComparison.Ordinal))
            return "frustrated";

        if (input.Contains("خلينا نبدأ", StringComparison.Ordinal) || input.Contains("نقدر نعملها", StringComparison.Ordinal) ||
            input.Contains("let's do this", StringComparison.Ordinal) || input.Contains("determined", StringComparison.Ordinal))
            return "determined";

        if (input.Contains("واثق", StringComparison.Ordinal) || input.Contains("confident", StringComparison.Ordinal) ||
            input.Contains("i've got this", StringComparison.Ordinal))
            return "confident";

        if (input.Contains("مبروك", StringComparison.Ordinal) || input.Contains("نجحنا", StringComparison.Ordinal) ||
            input.Contains("we did it", StringComparison.Ordinal) || input.Contains("congratulations", StringComparison.Ordinal))
            return "celebrating";

        if (input.Contains("أنا زعلان", StringComparison.Ordinal) || input.Contains("انا زعلان", StringComparison.Ordinal) ||
            input.Contains("أنا حزينة", StringComparison.Ordinal) || input.Contains("انا حزين", StringComparison.Ordinal) ||
            input.Contains("i feel sad", StringComparison.Ordinal) || input.Contains("i'm sad", StringComparison.Ordinal))
            return "worried";

        if (input.Contains("هههه", StringComparison.Ordinal) || input.Contains("😂", StringComparison.Ordinal) ||
            input.Contains("joke", StringComparison.Ordinal) || input.Contains("نكتة", StringComparison.Ordinal) ||
            input.Contains("ضحكني", StringComparison.Ordinal) || answer.Contains("😂", StringComparison.Ordinal) ||
            answer.Contains("🤣", StringComparison.Ordinal))
            return "laughing";

        if (input.Contains("شكرا", StringComparison.Ordinal) || input.Contains("شكرًا", StringComparison.Ordinal) ||
            input.Contains("متشكر", StringComparison.Ordinal) || input.Contains("تسلم", StringComparison.Ordinal) ||
            input.Contains("thank you", StringComparison.Ordinal) || input.Contains("thanks", StringComparison.Ordinal))
            return "grateful";

        if (input.Contains("السلام عليكم", StringComparison.Ordinal) || input.Contains("صباح الخير", StringComparison.Ordinal) ||
            input.Contains("مساء الخير", StringComparison.Ordinal) || input.Contains("اهلا", StringComparison.Ordinal) ||
            input.Contains("أهلا", StringComparison.Ordinal) || input.Contains("hello", StringComparison.Ordinal) ||
            input.Equals("hi", StringComparison.Ordinal) || input.StartsWith("hi ", StringComparison.Ordinal))
            return "greeting";

        if (input.Contains("مش فاهم", StringComparison.Ordinal) || input.Contains("مش شغال", StringComparison.Ordinal) ||
            input.Contains("غلط", StringComparison.Ordinal) || input.Contains("مشكلة", StringComparison.Ordinal) ||
            input.Contains("help me", StringComparison.Ordinal) || input.Contains("محتاج مساعدة", StringComparison.Ordinal))
            return "worried";

        if (input.Contains("ممتاز", StringComparison.Ordinal) || input.Contains("رائع", StringComparison.Ordinal) ||
            input.Contains("awesome", StringComparison.Ordinal) || input.Contains("great job", StringComparison.Ordinal))
            return "proud";

        if (input.Contains("بحب", StringComparison.Ordinal) || input.Contains("أحبك", StringComparison.Ordinal) ||
            input.Contains("love you", StringComparison.Ordinal))
            return "shy";

        if (input.Contains("؟", StringComparison.Ordinal) || input.Contains("?", StringComparison.Ordinal))
            return "curious";

        if (input.Contains("!", StringComparison.Ordinal) || input.Contains("！", StringComparison.Ordinal) ||
            input.Contains("مبروك", StringComparison.Ordinal) || input.Contains("احتفل", StringComparison.Ordinal))
            return "excited";

        return "happy";
    }

    private void CopyChatButton_Click(object sender, RoutedEventArgs e)
    {
        var transcript = ChatTranscriptFormatter.Format(ChatMessages);
        CopyChatTextToClipboard(transcript, IsArabic ? "المحادثة" : "chat");
    }

    private void ChatMessageContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Items.Count > 0 && menu.Items[0] is MenuItem item)
            item.Header = IsArabic ? "نسخ الرسالة" : "Copy message";
    }

    private void ChatMessageContextMenu_Opening(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Items.OfType<MenuItem>().FirstOrDefault() is { } copyItem)
            copyItem.Header = IsArabic ? "نسخ الرسالة" : "Copy message";
    }

    private void CopyChatMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem &&
            menuItem.Parent is ContextMenu contextMenu &&
            contextMenu.PlacementTarget is FrameworkElement target &&
            target.DataContext is ChatMessage message)
        {
            CopyChatTextToClipboard(message.Content, IsArabic ? "الرسالة" : "message");
            return;
        }

        SetAccessibleStatusText(ChatCopyStatus, IsArabic ? "تعذر تحديد الرسالة لنسخها." : "Could not identify the message to copy.");
        ChatCopyStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
    }

    private void CopyChatTextToClipboard(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetAccessibleStatusText(ChatCopyStatus, IsArabic ? "لا يوجد نص لنسخه." : "There is no text to copy.");
            ChatCopyStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            return;
        }

        try
        {
            Clipboard.SetText(BidiTextNormalizer.StripDirectionalControls(text));
            SetAccessibleStatusText(ChatCopyStatus, IsArabic ? $"تم نسخ {label} إلى الحافظة." : $"{label} copied to the clipboard.");
            ChatCopyStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(ChatCopyStatus, (IsArabic ? "تعذر النسخ: " : "Copy failed: ") + ex.Message);
            ChatCopyStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private static bool ContainsArabic(string value) => value.Any(c => c is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F');

    private void RefreshChatSessions(string? selectedId = null)
    {
        _refreshingChatSessions = true;
        try
        {
            ChatConversations.Clear();
            foreach (var conversation in AppRuntime.ChatSessionService.GetAll())
                ChatConversations.Add(conversation);
            var id = string.IsNullOrWhiteSpace(selectedId) ? _currentConversationId : selectedId;
            ChatHistorySelector.SelectedItem = ChatConversations.FirstOrDefault(c => c.Id == id)
                ?? ChatConversations.FirstOrDefault();
        }
        finally { _refreshingChatSessions = false; }
    }

    private void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null)
        {
            SetChatActivityStatus(IsArabic ? "أوقف الرد الحالي قبل بدء محادثة جديدة." : "Cancel the active response before starting a new conversation.");
            return;
        }
        var conversation = AppRuntime.ChatSessionService.CreateNewConversation(IsArabic ? "محادثة جديدة" : "New conversation");
        _currentConversationId = conversation.Id;
        ChatMessages.Clear();
        ChatInput.Clear();
        RefreshChatSessions(_currentConversationId);
        SetChatActivityStatus(IsArabic
            ? "بدأت محادثة جديدة؛ المحادثات السابقة ما زالت محفوظة ويمكن اختيارها من القائمة."
            : "New conversation started. Previous conversations remain saved in the selector.");
        AppRuntime.MascotWindow?.SetMood("excited");
        ChatInput.Focus();
    }

    private void ClearSavedChatHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null)
        {
            SetChatActivityStatus(IsArabic ? "أوقف الرد الحالي قبل مسح السجل." : "Cancel the active response before clearing history.");
            return;
        }

        var answer = MessageBox.Show(this,
            IsArabic
                ? "سيتم حذف كل المحادثات المحفوظة محليًا نهائيًا، بما فيها المحادثات السابقة. لا يمكن التراجع عن ذلك. هل تريد المتابعة؟"
                : "This permanently deletes all locally saved conversations, including archived chats. This cannot be undone. Continue?",
            IsArabic ? "تأكيد مسح سجل المحادثات" : "Confirm clear saved chat history",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var fresh = AppRuntime.ChatSessionService.ClearAll(IsArabic ? "محادثة جديدة" : "New conversation");
        _currentConversationId = fresh.Id;
        ChatMessages.Clear();
        ChatInput.Clear();
        RefreshChatSessions(_currentConversationId);
        SetChatActivityStatus(IsArabic ? "تم مسح سجل المحادثات المحفوظ وبدء محادثة جديدة." : "Saved chat history was cleared. A fresh conversation is ready.");
        AppRuntime.MascotWindow?.SetMood("success");
    }

    private void ChatHistorySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingChatSessions || ChatHistorySelector.SelectedItem is not ChatConversation selected)
            return;
        if (string.Equals(selected.Id, _currentConversationId, StringComparison.Ordinal))
            return;
        if (_chatCancellation is not null)
        {
            RefreshChatSessions(_currentConversationId);
            SetChatActivityStatus(IsArabic ? "أوقف الرد الحالي قبل تبديل المحادثة." : "Cancel the active response before switching conversations.");
            return;
        }

        var conversation = AppRuntime.ChatSessionService.SelectConversation(selected.Id);
        _currentConversationId = conversation.Id;
        ChatMessages.Clear();
        foreach (var message in conversation.Messages.TakeLast(100))
        {
            message.DisplayRole = message.Role switch
            {
                "user" => IsArabic ? "أنت" : "You",
                "assistant" => "Layal | ليال",
                _ => IsArabic ? "حالة النظام" : "System status"
            };
            message.Direction = ContainsArabic(message.Content) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            ChatMessages.Add(message);
        }
        ChatMessagesList.Items.Refresh();
        RefreshChatSessions(_currentConversationId);
        SetChatActivityStatus(IsArabic ? "تم تحميل المحادثة المحفوظة." : "Saved conversation loaded.");
    }

    private void CancelResponseButton_Click(object sender, RoutedEventArgs e) => _chatCancellation?.Cancel();

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

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
                var answer = MessageBox.Show(
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
                var answer = MessageBox.Show(this,
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

    private async void RunAgentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var goal = TaskInput.Text.Trim();
        if (goal.Length == 0)
        {
            SetAccessibleStatusText(TaskResult, IsArabic ? "اكتب وصف المهمة أولًا." : "Enter a task first.");

            return;
        }
        var key = AppRuntime.SecretStore.Load();
        if (string.IsNullOrWhiteSpace(key))
        {
            SetAccessibleStatusText(TaskResult, IsArabic ? "لا يوجد مفتاح API محفوظ. افتح الإعدادات واختبر الاتصال أولًا." : "No saved API key. Configure and test the provider in Settings first.");

            MainTabs.SelectedItem = SettingsTab;
            return;
        }
        if (_agentTaskCancellation is not null) return;

        var selectedSkills = SkillsList.SelectedItems.Cast<SkillRecord>().Take(3).ToList();
        if (SkillsList.SelectedItems.Count > 3)
        {
            SetAccessibleStatusText(TaskResult, IsArabic ? "استخدم 3 مهارات كحد أقصى في المهمة الواحدة." : "Select no more than 3 skills for one task.");

            return;
        }

        TaskEvents.Clear();
        _rawTaskEvents.Clear();
        SetAccessibleStatusText(TaskResult, IsArabic ? "بدأت المهمة. راقب سجل التنفيذ والموافقات." : "Task started. Watch the activity log and approval prompts.");

        SetAccessibleStatusText(TaskCopyStatus, string.Empty);
        var cancellation = new CancellationTokenSource();
        _agentTaskCancellation = cancellation;
        RunAgentTaskButton.IsEnabled = false;
        CancelAgentTaskButton.IsEnabled = true;
        AppRuntime.MascotWindow?.SetTaskActive(true);
        AppRuntime.MascotWindow?.SetMood("thinking");

        var finalMood = "error";
        try
        {
            var record = await AppRuntime.AgentOrchestrator.RunTaskAsync(
                goal,
                AppRuntime.Settings.ProviderBaseUrl,
                AppRuntime.Settings.ModelId,
                key,
                selectedSkills,
                ConfirmToolActionAsync,
                AddTaskEvent,
                cancellation.Token,
                AppRuntime.Settings.Language);

            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTaskResult(record, IsArabic));
            finalMood = record.Status switch
            {
                "Completed" => "success",
                "CompletedWithWarnings" => "warning",
                "Cancelled" => "paused",
                _ => "error"
            };
            TaskResult.Foreground = record.Status == "Completed" ? ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen) :
                record.Status is "CompletedWithWarnings" or "Cancelled" ? ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod) :
                ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
            RefreshRecentTasks(record.Id);
        }
        catch (OperationCanceledException)
        {
            SetAccessibleStatusText(TaskResult, IsArabic ? "تم إلغاء المهمة." : "Task was cancelled.");
            TaskResult.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            AddTaskEvent("Task cancelled by the user.");
            finalMood = "paused";
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTechnicalError("فشلت المهمة", "Task failed", ex, IsArabic));
            TaskResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
            AddTaskEvent("Task failed: " + ex.Message);
            finalMood = "error";
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_agentTaskCancellation, cancellation))
                _agentTaskCancellation = null;
            RunAgentTaskButton.IsEnabled = true;
            CancelAgentTaskButton.IsEnabled = false;
            AppRuntime.MascotWindow?.SetTaskActive(false);
        }

        // Set terminal state only after task history and event controls finish updating.
        // The mascot resets success/error/paused to idle after displaying the outcome briefly.
        AppRuntime.MascotWindow?.SetMood(finalMood);

        NotifyLiveRegion(TaskEventsList);
    }

    private void CopyTaskResultButton_Click(object sender, RoutedEventArgs e) =>
        CopyTaskTextToClipboard(TaskResult.Text, IsArabic ? "النتيجة" : "task result");

    private void CopyTaskLogButton_Click(object sender, RoutedEventArgs e) =>
        CopyTaskTextToClipboard(string.Join(Environment.NewLine, TaskEvents), IsArabic ? "سجل التنفيذ" : "task log");

    private void CopyTaskTextToClipboard(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetAccessibleStatusText(TaskCopyStatus, IsArabic ? "لا يوجد نص لنسخه." : "There is no text to copy.");
            TaskCopyStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SetAccessibleStatusText(TaskCopyStatus, IsArabic ? $"تم نسخ {label} إلى الحافظة." : $"Copied {label} to the clipboard.");
            TaskCopyStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(TaskCopyStatus, (IsArabic ? "تعذر النسخ: " : "Copy failed: ") + ex.Message);
            TaskCopyStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private void CancelAgentTaskButton_Click(object sender, RoutedEventArgs e) => _agentTaskCancellation?.Cancel();

    private Task<bool> ConfirmToolActionAsync(string title, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var heading = IsArabic ? "موافقة مطلوبة من ليال" : "Layal requires approval";
        var localizedTitle = UiLocalization.LocalizeApprovalTitle(title, IsArabic);
        var localizedDetails = UiLocalization.LocalizeApprovalDetails(details, IsArabic);
        var text = (IsArabic ? "الإجراء التالي يحتاج موافقتك:\n\n" : "The following action requires your approval:\n\n") + localizedDetails;
        var result = MessageBox.Show(this, text, string.IsNullOrWhiteSpace(localizedTitle) ? heading : heading + " — " + localizedTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return Task.FromResult(result == MessageBoxResult.Yes && !cancellationToken.IsCancellationRequested);
    }

    private void AddTaskEvent(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddTaskEvent(text));
            return;
        }
        _rawTaskEvents.Add(text);
        while (_rawTaskEvents.Count > 300) _rawTaskEvents.RemoveAt(0);
        TaskEvents.Add(UiLocalization.FormatTaskEvent(text, IsArabic));
        while (TaskEvents.Count > 300) TaskEvents.RemoveAt(0);
        if (TaskEventsList.Items.Count > 0) TaskEventsList.ScrollIntoView(TaskEventsList.Items[^1]);

        // Drive facial acting from actual engine events through a unit-testable resolver.
        var mascotMood = TaskMoodResolver.Resolve(text, TaskEvents.TakeLast(4));
        if (mascotMood is not null)
            AppRuntime.MascotWindow?.SetMood(mascotMood,
                showReaction: !text.Contains("Tool result recorded:", StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshRecentTasks(string? selectTaskId = null)
    {
        var previouslySelectedId = (RecentTasksList.SelectedItem as TaskHistoryListItem)?.Id;
        var idToSelect = string.IsNullOrWhiteSpace(selectTaskId) ? previouslySelectedId : selectTaskId;
        _refreshingRecentTasks = true;
        try
        {
            RecentTasks.Clear();
            foreach (var task in AppRuntime.TaskHistoryService.GetRecent(30))
                RecentTasks.Add(new TaskHistoryListItem(task, IsArabic));
            if (!string.IsNullOrWhiteSpace(idToSelect))
                RecentTasksList.SelectedItem = RecentTasks.FirstOrDefault(t => t.Id == idToSelect);
        }
        finally { _refreshingRecentTasks = false; }
    }

    private void RecentTasksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingRecentTasks || RecentTasksList.SelectedItem is not TaskHistoryListItem selectedTask) return;
        var task = selectedTask.Record;
        TaskEvents.Clear();
        _rawTaskEvents.Clear();
        foreach (var line in task.Events.TakeLast(300)) AddTaskEvent(line);
        SetAccessibleStatusText(TaskResult, UiLocalization.FormatTaskResult(task, IsArabic));

        TaskResult.Foreground = task.Status == "Completed" ? ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen) :
            task.Status is "CompletedWithWarnings" or "Cancelled" ? ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod) :
            ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        AppRuntime.MascotWindow?.SetMood(task.Status switch
        {
            "Completed" => "success",
            "CompletedWithWarnings" => "warning",
            "Cancelled" => "paused",
            _ => "error"
        });
    }

    private void RefreshMemoryNotes(string? query = null)
    {
        MemoryNotes.Clear();
        var notes = string.IsNullOrWhiteSpace(query) ? AppRuntime.MemoryService.GetAll() : AppRuntime.MemoryService.Search(query);
        foreach (var note in notes) MemoryNotes.Add(note);
    }

    private void MemorySearchButton_Click(object sender, RoutedEventArgs e) => RefreshMemoryNotes(MemorySearchInput.Text);

    private void NewMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedMemoryId = null;
        MemoryList.SelectedItem = null;
        MemoryTitleInput.Clear();
        MemoryContentInput.Clear();
        SetAccessibleStatusText(MemoryResult, IsArabic ? "اكتب ملاحظة جديدة ثم اضغط حفظ." : "Write a new note and select Save.");
        MemoryTitleInput.Focus();
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is not MemoryNote note) return;
        _selectedMemoryId = note.Id;
        MemoryTitleInput.Text = note.Title;
        MemoryContentInput.Text = note.Content;
        SetAccessibleStatusText(MemoryResult, UiLocalization.FormatDateTime(note.UpdatedAt.LocalDateTime, IsArabic));
    }

    private void SaveMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = MemoryTitleInput.Text.Trim();
            var content = MemoryContentInput.Text.Trim();
            var answer = MessageBox.Show(this,
                IsArabic ? "سيتم حفظ هذه الملاحظة محليًا مشفرة باستخدام DPAPI لحساب Windows الحالي. لا تحفظ أسرارًا أو كلمات مرور. هل تريد المتابعة؟" : "This note will be saved locally encrypted with DPAPI for the current Windows user. Do not store secrets or passwords. Continue?",
                IsArabic ? "تأكيد حفظ الذاكرة" : "Confirm memory save", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            var note = AppRuntime.MemoryService.Save(title, content, _selectedMemoryId);
            _selectedMemoryId = note.Id;
            RefreshMemoryNotes(MemorySearchInput.Text);
            MemoryList.SelectedItem = MemoryNotes.FirstOrDefault(n => n.Id == note.Id);
            SetAccessibleStatusText(MemoryResult, IsArabic ? "تم حفظ الملاحظة محليًا بصورة مشفرة." : "Note saved locally with encryption.");
            MemoryResult.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(MemoryResult, UiLocalization.FormatTechnicalError("تعذر حفظ الملاحظة", "Could not save the note", ex, IsArabic));
            MemoryResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private void DeleteMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedMemoryId))
        {
            SetAccessibleStatusText(MemoryResult, IsArabic ? "حدد ملاحظة لحذفها." : "Select a note to delete.");
            return;
        }
        var answer = MessageBox.Show(this, IsArabic ? "هل تريد حذف الملاحظة المحددة نهائيًا؟" : "Permanently delete the selected memory note?",
            IsArabic ? "تأكيد الحذف" : "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        AppRuntime.MemoryService.Delete(_selectedMemoryId);
        _selectedMemoryId = null;
        MemoryTitleInput.Clear();
        MemoryContentInput.Clear();
        RefreshMemoryNotes(MemorySearchInput.Text);
        SetAccessibleStatusText(MemoryResult, IsArabic ? "تم حذف الملاحظة." : "Memory note deleted.");
    }

    private async void ExportMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = IsArabic ? "تصدير الذاكرة إلى JSON غير مشفر" : "Export memory to unencrypted JSON",
            Filter = IsArabic ? "ملفات JSON (*.json)|*.json" : "JSON files (*.json)|*.json",
            FileName = "layal-memory-export.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var confirm = MessageBox.Show(this,
            IsArabic ? "سيكون ملف JSON الناتج غير مشفر. احفظه في مكان آمن. هل تريد المتابعة؟" : "The exported JSON file will be unencrypted. Store it securely. Continue?",
            IsArabic ? "تنبيه خصوصية" : "Privacy warning", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, AppRuntime.MemoryService.ExportJson(), new UTF8Encoding(false));
            SetAccessibleStatusText(MemoryResult, IsArabic ? "تم تصدير الذاكرة. الملف غير مشفر." : "Memory exported. The file is not encrypted.");
            MemoryResult.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(MemoryResult, UiLocalization.FormatTechnicalError("تعذر تصدير الذاكرة", "Could not export memory", ex, IsArabic));
            MemoryResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _speechRecordingTimer.Stop();
        _speechCancellation?.Cancel();
        _voiceTestCancellation?.Cancel();
        _voiceConversationCancellation?.Cancel();
        if (!_speechIsStopping) _speechToText.CancelRecording();

        if (!AppRuntime.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _chatCancellation?.Cancel();
            _agentTaskCancellation?.Cancel();
        }
    }
}
