using System.Collections.ObjectModel;
using System.Text;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Layal.Models;
using Layal.Services;
using Microsoft.Win32;

namespace Layal.Views;

public partial class MainWindow : Window
{
    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();
    public ObservableCollection<ChatConversation> ChatConversations { get; } = new();
    public ObservableCollection<SkillRecord> InstalledSkills { get; } = new();
    public ObservableCollection<string> TaskEvents { get; } = new();
    public ObservableCollection<AgentTaskRecord> RecentTasks { get; } = new();
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
            foreach (var line in recent.Events.TakeLast(100)) TaskEvents.Add(line);
            TaskResult.Text = $"{recent.Status}: {recent.Result}";
        }
        RefreshRecentTasks();
        RefreshSkills();
        RefreshMemoryNotes();
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

    private void ApplyLanguage()
    {
        var ar = IsArabic;
        FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ChatInput.FlowDirection = TaskInput.FlowDirection = MemoryTitleInput.FlowDirection = MemoryContentInput.FlowDirection = MemorySearchInput.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
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
        ShowMascotButton.Content = ar ? "إظهار الشخصية" : "Show mascot";

        HomeTab.Header = ar ? "⌂  الرئيسية" : "⌂  Home";
        ChatTab.Header = ar ? "✦  المحادثة" : "✦  Chat";
        TasksTab.Header = ar ? "▣  المهام" : "▣  Tasks";
        SkillsTab.Header = ar ? "◇  المهارات" : "◇  Skills";
        CamerasTab.Header = ar ? "◉  الكاميرات" : "◉  Cameras";
        MemoryTab.Header = ar ? "▤  الذاكرة" : "▤  Memory";
        SettingsTab.Header = ar ? "⚙  الإعدادات" : "⚙  Settings";

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
        NewConversationButton.Content = ar ? "محادثة جديدة" : "New conversation";
        ClearSavedChatHistoryButton.Content = ar ? "مسح السجل المحفوظ" : "Clear saved history";
        ChatHistorySelector.ToolTip = ar ? "المحادثات المحفوظة — اختر محادثة للمتابعة" : "Saved conversations — select one to resume";
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
        TaskSkillHint.Text = ar ? "يمكن تحديد حتى 3 مهارات من تبويب المهارات؛ نصوص المهارات تُعامل كمراجع غير موثوقة." : "Select up to 3 skills in Skills; skill text is treated as untrusted reference material.";
        CopyTaskResultButton.Content = ar ? "نسخ النتيجة" : "Copy result";
        CopyTaskLogButton.Content = ar ? "نسخ سجل التنفيذ" : "Copy task log";
        if (string.IsNullOrEmpty(TaskResult.Text)) TaskResult.Text = ar ? "لم تُنفذ أي عملية بعد." : "No operation has been run yet.";

        SkillsHeading.Text = ar ? "مدير المهارات" : "Skills manager";
        SkillsDescription.Text = ar ? "استيراد حزمة ZIP والتحقق من SKILL.md. لا يتم استخراج الحزمة أو تشغيل سكربتاتها." : "Import a ZIP and validate SKILL.md. The package is stored without extraction or script execution.";
        ImportSkillButton.Content = ar ? "استيراد ZIP" : "Import ZIP";
        RefreshSkillsButton.Content = ar ? "تحديث القائمة" : "Refresh list";
        RecentTasksGroup.Header = ar ? "المهام السابقة" : "Recent tasks";
        ImportGitHubSkillButton.Content = ar ? "استيراد من GitHub" : "Import from GitHub";
        GitHubSkillUrlInput.ToolTip = ar ? "رابط مستودع أو مجلد يحتوي على SKILL.md" : "Repository or directory URL containing SKILL.md";

        CamerasHeading.Text = ar ? "الكاميرات والرؤية الحاسوبية" : "Cameras and computer vision";
        CameraStatus.Text = ar ? "غير متصل — التكامل الحقيقي غير منفذ في إصدار الأساس" : "Not connected — real camera integration is not implemented in this foundation release";
        CameraDescription.Text = ar
            ? "سيُضاف اتصال RTSP/ONVIF ومعالجة الفيديو بعد إنشاء خدمة الكاميرات واختبارها على Windows مع جهاز مصرح به. لا يعرض هذا الإصدار بثًا وهميًا."
            : "RTSP/ONVIF and video processing will be added after the camera service is implemented and tested on Windows with an authorized device. This release does not show fake streams.";

        SettingsHeading.Text = ar ? "الإعدادات والاتصال" : "Settings and connections";
        LanguageSettingLabel.Text = ar ? "لغة الواجهة" : "Interface language";
        ArabicLanguageButton.Content = "العربية";
        EnglishLanguageButton.Content = "English";
        ProviderSectionTitle.Text = ar ? "مزود ذكاء اصطناعي متوافق مع OpenAI API" : "OpenAI-compatible AI provider";
        EndpointLabel.Text = ar ? "عنوان API الأساسي (Base URL)" : "API base URL";
        ModelLabel.Text = ar ? "معرّف النموذج" : "Model ID";
        ApiKeyLabel.Text = ar ? "مفتاح API / Unified API Key" : "API key / Unified API key";
        KeyStorageNote.Text = ar ? "يُحفظ المفتاح باستخدام Windows DPAPI للمستخدم الحالي، ولا يظهر في ملف الإعدادات النصي." : "The key is encrypted with Windows DPAPI for the current user and is not stored in the plain settings file.";
        SaveProviderButton.Content = ar ? "حفظ الإعدادات" : "Save settings";
        TestProviderButton.Content = ar ? "اختبار الاتصال" : "Test connection";
        DeleteKeyButton.Content = ar ? "حذف المفتاح المحفوظ" : "Delete saved key";
        MascotSettingsTitle.Text = ar ? "إعدادات الشخصية العائمة" : "Floating mascot settings";
        AlwaysOnTopCheckbox.Content = ar ? "إبقاء الشخصية فوق النوافذ الأخرى" : "Keep mascot above other windows";
        AnimationsCheckbox.Content = ar ? "تفعيل حركة الشخصية والرمش" : "Enable mascot movement and blinking";
        MascotSizeLabel.Text = ar ? "حجم الشخصية" : "Mascot size";
        MascotOpacityLabel.Text = ar ? "شفافية الشخصية" : "Mascot opacity";
        ApplyMascotSettingsButton.Content = ar ? "تطبيق إعدادات الشخصية" : "Apply mascot settings";
        SettingsLimitations.Text = ar
            ? "ما زالت وظائف الصوت الحقيقي، وأتمتة المتصفح، والتحكم العام بالماوس والكيبورد، وبث الكاميرات، وMCP غير منفذة. لا يتم عرضها كوظائف مكتملة."
            : "Real voice, browser automation, general mouse/keyboard control, camera streaming, and MCP are not implemented yet and are not presented as completed features.";
        WindowInspectionCheckbox.Content = ar ? "السماح بقراءة عناوين النوافذ عند الطلب (تتطلب مشاركة العناوين موافقة)" : "Allow window-title inspection on request (sharing titles still requires approval)";
        MemoryHeading.Text = ar ? "الذاكرة الشخصية" : "Personal memory";
        MemoryDescription.Text = ar ? "ملاحظات تحفظها أنت. تُشفّر محليًا بحماية Windows DPAPI؛ مشاركة نتائج البحث مع AI تتطلب موافقة." : "User-managed notes encrypted with Windows DPAPI. Sharing search results with AI requires approval.";
        MemorySearchInput.ToolTip = ar ? "بحث في الملاحظات" : "Search memory notes";
        MemorySearchButton.Content = ar ? "بحث" : "Search";
        NewMemoryButton.Content = ar ? "ملاحظة جديدة" : "New note";
        MemoryTitleLabel.Text = ar ? "العنوان" : "Title";
        MemoryContentLabel.Text = ar ? "المحتوى" : "Content";
        SaveMemoryButton.Content = ar ? "حفظ الملاحظة" : "Save note";
        DeleteMemoryButton.Content = ar ? "حذف الملاحظة" : "Delete note";
        ExportMemoryButton.Content = ar ? "تصدير JSON" : "Export JSON";
        MemoryPrivacyNote.Text = ar ? "لا تحفظ مفاتيح API أو كلمات المرور في الذاكرة. ملف JSON المُصدَّر غير مشفّر." : "Do not store API keys or passwords in memory. Exported JSON is unencrypted.";

        AppRuntime.Tray?.SetLanguage(AppRuntime.Settings.Language);
        AppRuntime.MascotWindow?.UpdateLanguage();
        RefreshProviderStatus();
    }

    private void RefreshProviderStatus()
    {
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
        ConnectionStatusText.Foreground = tested ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.DarkGoldenrod;
        CardAiValue.Text = tested ? (IsArabic ? "تم اختبار الاتصال" : "Connection tested") : ready ? (IsArabic ? "إعداد محفوظ — لم يُختبر" : "Configured — not tested") : (IsArabic ? "غير متصل" : "Not connected");
        CardAiValue.Foreground = tested ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.DarkGoldenrod;
        ChatProviderStatus.Text = tested ? (IsArabic ? "تم اختبار نقطة الاتصال في هذه الجلسة" : "Endpoint tested in this session")
            : ready ? (IsArabic ? "إعداد محفوظ؛ اختبر الاتصال" : "Configured; test connection")
            : (IsArabic ? "لا يوجد اتصال مختبَر" : "Not connected/tested");
    }

    private void SetLanguage(string language)
    {
        AppRuntime.Settings.Language = language;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        ApplyLanguage();
        AppRuntime.Tray?.SetLanguage(language);
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
            HomeResult.Text = result;
            TaskResult.Text = result;
            AppRuntime.MascotWindow?.SetMood("success");
        }
        catch (Exception ex)
        {
            var message = IsArabic ? $"تعذر فتح المفكرة: {ex.Message}" : $"Could not launch Notepad: {ex.Message}";
            HomeResult.Text = message;
            TaskResult.Text = message;
            AppRuntime.MascotWindow?.SetMood("error");
        }
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
            ProviderActionStatus.Text = IsArabic ? "تم حفظ الإعدادات. لم يتم تأكيد الاتصال حتى الآن؛ استخدم اختبار الاتصال." : "Settings saved. Connectivity is not confirmed yet; run the connection test.";
            ProviderActionStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
            RefreshProviderStatus();
        }
        catch (Exception ex)
        {
            ProviderActionStatus.Text = ex.Message;
            ProviderActionStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private async void TestProviderButton_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlInput.Text.Trim();
        var key = string.IsNullOrWhiteSpace(ApiKeyInput.Password) ? AppRuntime.SecretStore.Load() : ApiKeyInput.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ProviderActionStatus.Text = IsArabic ? "أدخل مفتاح API ثم اختبر الاتصال. لن يتم حفظه إلا إذا ضغطت حفظ الإعدادات." : "Enter an API key to test. It is saved only when you select Save settings.";
            ProviderActionStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            return;
        }
        TestProviderButton.IsEnabled = false;
        ProviderActionStatus.Text = IsArabic ? "جارٍ اختبار الاتصال الفعلي..." : "Testing the actual endpoint...";
        ProviderActionStatus.Foreground = System.Windows.Media.Brushes.SteelBlue;
        try
        {
            var result = await _aiClient.ProbeAsync(baseUrl, key);
            _providerTestPassed = true;
            _testedBaseUrl = baseUrl.TrimEnd('/');
            _testedModelId = ModelInput.Text.Trim();
            ProviderActionStatus.Text = IsArabic ? $"نجح الاختبار: {result}" : result;
            ProviderActionStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            _providerTestPassed = false;
            ProviderActionStatus.Text = (IsArabic ? "فشل اختبار الاتصال: " : "Connection test failed: ") + ex.Message;
            ProviderActionStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
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
        ProviderActionStatus.Text = IsArabic ? "تم حذف المفتاح المحفوظ." : "Saved key deleted.";
        ProviderActionStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
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
        ProviderActionStatus.Text = IsArabic ? "تم تطبيق إعدادات الشخصية." : "Mascot settings applied.";
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
            MainTabs.SelectedItem = ChatTab;
            return;
        }

        _chatCancellation = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        CancelResponseButton.IsEnabled = true;
        ChatProviderStatus.Text = IsArabic ? "جارٍ الاتصال بالنموذج..." : "Request in progress...";
        AppRuntime.MascotWindow?.SetMood("thinking");
        try
        {
            var history = new List<ChatMessage>
            {
                new() { Role = "system", Content = LayalIdentity.SystemPrompt }
            };
            history.AddRange(ChatMessages.Where(m => m.Role is "user" or "assistant"));
            var response = await _aiClient.ChatAsync(AppRuntime.Settings.ProviderBaseUrl, AppRuntime.Settings.ModelId, key, history, _chatCancellation.Token);
            AddMessage("assistant", response);
            AppRuntime.MascotWindow?.SetMood("success");
            ChatProviderStatus.Text = IsArabic ? "اكتمل طلب النموذج" : "Model request completed";
        }
        catch (OperationCanceledException)
        {
            AddMessage("status", IsArabic ? "تم إيقاف الطلب." : "Request cancelled.");
            AppRuntime.MascotWindow?.SetMood("paused");
            ChatProviderStatus.Text = IsArabic ? "تم الإيقاف" : "Cancelled";
        }
        catch (Exception ex)
        {
            AddMessage("status", (IsArabic ? "فشل طلب النموذج: " : "Model request failed: ") + ex.Message);
            AppRuntime.MascotWindow?.SetMood("error");
            ChatProviderStatus.Text = IsArabic ? "فشل الطلب" : "Request failed";
        }
        finally
        {
            _chatCancellation?.Dispose();
            _chatCancellation = null;
            SendButton.IsEnabled = true;
            CancelResponseButton.IsEnabled = false;
            ChatMessagesList.ScrollIntoView(ChatMessages.LastOrDefault());
            RefreshProviderStatus();
        }
    }

    private void AddMessage(string role, string content)
    {
        var ar = IsArabic;
        var display = role switch
        {
            "user" => ar ? "أنت" : "You",
            "assistant" => "Layal | ليال",
            _ => ar ? "حالة النظام" : "System status"
        };
        var direction = ContainsArabic(content) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ChatMessages.Add(new ChatMessage { Role = role, Content = content, DisplayRole = display, CreatedAt = DateTimeOffset.Now, Direction = direction });
        while (ChatMessages.Count > 100) ChatMessages.RemoveAt(0);
        try
        {
            AppRuntime.ChatSessionService.SaveCurrentMessages(ChatMessages.ToList());
            RefreshChatSessions(_currentConversationId);
        }
        catch { /* A history save failure must not block the current conversation. */ }
        ChatMessagesList.ScrollIntoView(ChatMessages.LastOrDefault());
    }

    private void CopyChatButton_Click(object sender, RoutedEventArgs e)
    {
        var transcript = ChatTranscriptFormatter.Format(ChatMessages);
        CopyChatTextToClipboard(transcript, IsArabic ? "المحادثة" : "chat");
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

        ChatCopyStatus.Text = IsArabic ? "تعذر تحديد الرسالة لنسخها." : "Could not identify the message to copy.";
        ChatCopyStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
    }

    private void CopyChatTextToClipboard(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ChatCopyStatus.Text = IsArabic ? "لا يوجد نص لنسخه." : "There is no text to copy.";
            ChatCopyStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            return;
        }

        try
        {
            Clipboard.SetText(text);
            ChatCopyStatus.Text = IsArabic ? $"تم نسخ {label} إلى الحافظة." : $"{label} copied to the clipboard.";
            ChatCopyStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            ChatCopyStatus.Text = (IsArabic ? "تعذر النسخ: " : "Copy failed: ") + ex.Message;
            ChatCopyStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
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
            ChatProviderStatus.Text = IsArabic ? "أوقف الرد الحالي قبل بدء محادثة جديدة." : "Cancel the active response before starting a new conversation.";
            return;
        }
        var conversation = AppRuntime.ChatSessionService.CreateNewConversation(IsArabic ? "محادثة جديدة" : "New conversation");
        _currentConversationId = conversation.Id;
        ChatMessages.Clear();
        ChatInput.Clear();
        RefreshChatSessions(_currentConversationId);
        ChatProviderStatus.Text = IsArabic
            ? "بدأت محادثة جديدة؛ المحادثات السابقة ما زالت محفوظة ويمكن اختيارها من القائمة."
            : "New conversation started. Previous conversations remain saved in the selector.";
        AppRuntime.MascotWindow?.SetMood("excited");
        ChatInput.Focus();
    }

    private void ClearSavedChatHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null)
        {
            ChatProviderStatus.Text = IsArabic ? "أوقف الرد الحالي قبل مسح السجل." : "Cancel the active response before clearing history.";
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
        ChatProviderStatus.Text = IsArabic ? "تم مسح سجل المحادثات المحفوظ وبدء محادثة جديدة." : "Saved chat history was cleared. A fresh conversation is ready.";
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
            ChatProviderStatus.Text = IsArabic ? "أوقف الرد الحالي قبل تبديل المحادثة." : "Cancel the active response before switching conversations.";
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
        ChatProviderStatus.Text = IsArabic ? "تم تحميل المحادثة المحفوظة." : "Saved conversation loaded.";
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
            Filter = "Skill packages (*.zip)|*.zip|All files (*.*)|*.*",
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
            SkillsResult.Text = (IsArabic ? "تم استيراد بيانات المهارة: " : "Skill metadata imported: ") + record.Name;
            SkillsResult.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            SkillsResult.Text = (IsArabic ? "تعذر استيراد المهارة: " : "Skill import failed: ") + ex.Message;
            SkillsResult.Foreground = System.Windows.Media.Brushes.Firebrick;
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
            SkillsResult.Text = IsArabic ? "أدخل رابط GitHub أولًا." : "Enter a GitHub URL first.";
            SkillsResult.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            return;
        }
        ImportGitHubSkillButton.IsEnabled = false;
        SkillsResult.Text = IsArabic ? "جارٍ تنزيل المستودع وفحص ملفات المهارة..." : "Downloading repository and validating skill files...";
        SkillsResult.Foreground = System.Windows.Media.Brushes.SteelBlue;
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
            SkillsResult.Text = (IsArabic ? "تم استيراد المهارة: " : "Imported skill: ") + record.Name + Environment.NewLine + record.Description;
            SkillsResult.Foreground = System.Windows.Media.Brushes.ForestGreen;
            GitHubSkillUrlInput.Clear();
        }
        catch (Exception ex)
        {
            SkillsResult.Text = (IsArabic ? "تعذر استيراد المهارة من GitHub: " : "GitHub skill import failed: ") + ex.Message;
            SkillsResult.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
        finally { ImportGitHubSkillButton.IsEnabled = true; }
    }

    private async void RunAgentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var goal = TaskInput.Text.Trim();
        if (goal.Length == 0)
        {
            TaskResult.Text = IsArabic ? "اكتب وصف المهمة أولًا." : "Enter a task first.";
            return;
        }
        var key = AppRuntime.SecretStore.Load();
        if (string.IsNullOrWhiteSpace(key))
        {
            TaskResult.Text = IsArabic ? "لا يوجد مفتاح API محفوظ. افتح الإعدادات واختبر الاتصال أولًا." : "No saved API key. Configure and test the provider in Settings first.";
            MainTabs.SelectedItem = SettingsTab;
            return;
        }
        if (_agentTaskCancellation is not null) return;

        var selectedSkills = SkillsList.SelectedItems.Cast<SkillRecord>().Take(3).ToList();
        if (SkillsList.SelectedItems.Count > 3)
        {
            TaskResult.Text = IsArabic ? "استخدم 3 مهارات كحد أقصى في المهمة الواحدة." : "Select no more than 3 skills for one task.";
            return;
        }

        TaskEvents.Clear();
        TaskResult.Text = IsArabic ? "بدأت المهمة. راقب سجل التنفيذ والموافقات." : "Task started. Watch the activity log and approval prompts.";
        TaskCopyStatus.Text = string.Empty;
        var cancellation = new CancellationTokenSource();
        _agentTaskCancellation = cancellation;
        RunAgentTaskButton.IsEnabled = false;
        CancelAgentTaskButton.IsEnabled = true;
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
                cancellation.Token);

            TaskResult.Text = $"{record.Status}{Environment.NewLine}{record.Result}";
            finalMood = record.Status switch
            {
                "Completed" => "success",
                "CompletedWithWarnings" => "warning",
                "Cancelled" => "paused",
                _ => "error"
            };
            TaskResult.Foreground = record.Status == "Completed" ? System.Windows.Media.Brushes.ForestGreen :
                record.Status is "CompletedWithWarnings" or "Cancelled" ? System.Windows.Media.Brushes.DarkGoldenrod :
                System.Windows.Media.Brushes.Firebrick;
            RefreshRecentTasks(record.Id);
        }
        catch (OperationCanceledException)
        {
            TaskResult.Text = IsArabic ? "تم إلغاء المهمة." : "Task was cancelled.";
            TaskResult.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            AddTaskEvent("Task cancelled by the user.");
            finalMood = "paused";
        }
        catch (Exception ex)
        {
            TaskResult.Text = (IsArabic ? "فشلت المهمة: " : "Task failed: ") + ex.Message;
            TaskResult.Foreground = System.Windows.Media.Brushes.Firebrick;
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
        }

        // Set terminal state only after task history and event controls finish updating.
        // The mascot resets success/error/paused to idle after displaying the outcome briefly.
        AppRuntime.MascotWindow?.SetMood(finalMood);
    }

    private void CopyTaskResultButton_Click(object sender, RoutedEventArgs e) =>
        CopyTaskTextToClipboard(TaskResult.Text, IsArabic ? "النتيجة" : "task result");

    private void CopyTaskLogButton_Click(object sender, RoutedEventArgs e) =>
        CopyTaskTextToClipboard(string.Join(Environment.NewLine, TaskEvents), IsArabic ? "سجل التنفيذ" : "task log");

    private void CopyTaskTextToClipboard(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            TaskCopyStatus.Text = IsArabic ? "لا يوجد نص لنسخه." : "There is no text to copy.";
            TaskCopyStatus.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            return;
        }
        try
        {
            Clipboard.SetText(text);
            TaskCopyStatus.Text = IsArabic ? $"تم نسخ {label} إلى الحافظة." : $"Copied {label} to the clipboard.";
            TaskCopyStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            TaskCopyStatus.Text = (IsArabic ? "تعذر النسخ: " : "Copy failed: ") + ex.Message;
            TaskCopyStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void CancelAgentTaskButton_Click(object sender, RoutedEventArgs e) => _agentTaskCancellation?.Cancel();

    private Task<bool> ConfirmToolActionAsync(string title, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var heading = IsArabic ? "موافقة مطلوبة من ليال" : "Layal requires approval";
        var text = (IsArabic ? "الإجراء التالي يحتاج موافقتك:\n\n" : "The following action requires your approval:\n\n") + details;
        var result = MessageBox.Show(this, text, string.IsNullOrWhiteSpace(title) ? heading : heading + " — " + title,
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
        TaskEvents.Add(text);
        while (TaskEvents.Count > 300) TaskEvents.RemoveAt(0);
        if (TaskEventsList.Items.Count > 0) TaskEventsList.ScrollIntoView(TaskEventsList.Items[^1]);

        // Keep the mascot's expression synchronized with real task-engine events.
        if (text.Contains("Tool requested", StringComparison.OrdinalIgnoreCase)) AppRuntime.MascotWindow?.SetMood("using_tool");
        else if (text.Contains("Requesting model turn", StringComparison.OrdinalIgnoreCase) || text.Contains("Task accepted", StringComparison.OrdinalIgnoreCase)) AppRuntime.MascotWindow?.SetMood("thinking");
        else if (text.Contains("Tool failed", StringComparison.OrdinalIgnoreCase) || text.Contains("Task failed", StringComparison.OrdinalIgnoreCase)) AppRuntime.MascotWindow?.SetMood("error");
        else if (text.Contains("approval", StringComparison.OrdinalIgnoreCase) || text.Contains("requires approval", StringComparison.OrdinalIgnoreCase)) AppRuntime.MascotWindow?.SetMood("warning");
    }

    private void RefreshRecentTasks(string? selectTaskId = null)
    {
        RecentTasks.Clear();
        foreach (var task in AppRuntime.TaskHistoryService.GetRecent(30)) RecentTasks.Add(task);
        if (!string.IsNullOrWhiteSpace(selectTaskId))
            RecentTasksList.SelectedItem = RecentTasks.FirstOrDefault(t => t.Id == selectTaskId);
    }

    private void RecentTasksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentTasksList.SelectedItem is not AgentTaskRecord task) return;
        TaskEvents.Clear();
        foreach (var line in task.Events.TakeLast(300)) AddTaskEvent(line);
        TaskResult.Text = $"{task.Status}{Environment.NewLine}{task.Result}";
        TaskResult.Foreground = task.Status == "Completed" ? System.Windows.Media.Brushes.ForestGreen :
            task.Status is "CompletedWithWarnings" or "Cancelled" ? System.Windows.Media.Brushes.DarkGoldenrod :
            System.Windows.Media.Brushes.Firebrick;
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
        MemoryResult.Text = IsArabic ? "اكتب ملاحظة جديدة ثم اضغط حفظ." : "Write a new note and select Save.";
        MemoryTitleInput.Focus();
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is not MemoryNote note) return;
        _selectedMemoryId = note.Id;
        MemoryTitleInput.Text = note.Title;
        MemoryContentInput.Text = note.Content;
        MemoryResult.Text = $"{note.UpdatedAt.LocalDateTime:g}";
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
            MemoryResult.Text = IsArabic ? "تم حفظ الملاحظة محليًا بصورة مشفرة." : "Note saved locally with encryption.";
            MemoryResult.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            MemoryResult.Text = ex.Message;
            MemoryResult.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void DeleteMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedMemoryId))
        {
            MemoryResult.Text = IsArabic ? "حدد ملاحظة لحذفها." : "Select a note to delete.";
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
        MemoryResult.Text = IsArabic ? "تم حذف الملاحظة." : "Memory note deleted.";
    }

    private async void ExportMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = IsArabic ? "تصدير الذاكرة إلى JSON غير مشفر" : "Export memory to unencrypted JSON",
            Filter = "JSON files (*.json)|*.json",
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
            MemoryResult.Text = IsArabic ? "تم تصدير الذاكرة. الملف غير مشفر." : "Memory exported. The file is not encrypted.";
            MemoryResult.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
        }
        catch (Exception ex)
        {
            MemoryResult.Text = ex.Message;
            MemoryResult.Foreground = System.Windows.Media.Brushes.Firebrick;
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
