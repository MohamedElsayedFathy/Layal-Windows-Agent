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
        DataContext = this;
        BaseUrlInput.Text = AppRuntime.Settings.ProviderBaseUrl;
        ModelInput.Text = AppRuntime.Settings.ModelId;
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
            ? "الوظائف غير المكتملة لن تظهر كأنها تعمل: الصوت، والتحكم العام في Windows، وأتمتة المتصفح، والبث الحقيقي للكاميرات ستضاف في مراحل لاحقة."
            : "Unimplemented capabilities are not represented as working: voice, general desktop automation, browser automation, and real camera streaming remain future milestones.";

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
            ? "ما زالت وظائف الصوت الحقيقي، وأتمتة المتصفح، والتحكم العام بالماوس والكيبورد، وبث الكاميرات، وMCP غير منفذة. لا يتم عرضها كوظائف مكتملة."
            : "Real voice, browser automation, general mouse/keyboard control, camera streaming, and MCP are not implemented yet and are not presented as completed features.";
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

            AppRuntime.Settings.ProviderBaseUrl = baseUrl;
            AppRuntime.Settings.ModelId = model;
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

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async Task SendMessageAsync()
    {
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
            AppRuntime.MascotWindow?.SetMood(SelectChatMood(text, response));
            SetChatActivityStatus(IsArabic ? "اكتمل الرد" : "Response completed");
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
