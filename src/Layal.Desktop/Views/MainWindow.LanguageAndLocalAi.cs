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
    private void ApplyLanguage()
    {
        var ar = IsArabic;
        // Keep the root visual tree LTR: setting RTL on the entire Window mirrors glyphs and icons.
        // Apply RTL to input fields and Arabic text runs locally instead of flipping all visuals.
        FlowDirection = FlowDirection.LeftToRight;
        // Only the dock and chat-history surface follow the writing direction. The window,
        // content canvas, caption controls, and technical fields remain left-to-right.
        MainTabs.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        DockNavigationItems.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ChatHistoryPanel.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ChatHistoryList.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Grid.SetColumn(ChatHistoryPanel, ar ? 1 : 0);
        Grid.SetColumn(ChatConversationSurface, ar ? 0 : 1);
        // The conversation must occupy the star-sized column in both writing directions.
        // The saved-history panel stays in the Auto column so it only takes its own 270-DIP width
        // while visible instead of reserving the flexible column when hidden.
        ChatTabLayout.ColumnDefinitions[ar ? 0 : 1].Width = new GridLength(1, GridUnitType.Star);
        ChatTabLayout.ColumnDefinitions[ar ? 1 : 0].Width = GridLength.Auto;
        ChatHistoryPanel.Margin = ar ? new Thickness(8, 4, 0, 4) : new Thickness(0, 4, 8, 4);
        ChatHistoryPanelTransform.X = ar ? 10 : -10;
        ChatInput.FlowDirection = TaskInput.FlowDirection = MemoryTitleInput.FlowDirection = MemoryContentInput.FlowDirection = MemorySearchInput.FlowDirection = MemoryCategoryInput.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TaskResult.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        BaseUrlInput.FlowDirection = ModelInput.FlowDirection = ApiKeyInput.FlowDirection = GitHubSkillUrlInput.FlowDirection = FlowDirection.LeftToRight;
        WeatherLocationInput.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
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
        RefreshAudioDeviceSelectors();

        Title = ar ? "ليال | Layal" : "Layal | ليال";
        TitleText.Text = ar ? "ليال | \u2066Layal\u2069" : "Layal | Bilingual Desktop AI";
        TitleText.FlowDirection = FlowDirection.LeftToRight;
        TitleText.TextAlignment = ar ? TextAlignment.Right : TextAlignment.Left;
        SubtitleText.Text = ar ? "وكيل ذكاء اصطناعي مستقل لسطح المكتب" : "A bilingual Windows AI desktop companion";
        LanguageButton.Content = ar ? "\u2066English\u2069" : "العربية";
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
        AutomationProperties.SetHelpText(MainTabs, ar ? "استخدم شريط التنقل الجانبي أو Ctrl+1 إلى Ctrl+8 لاختيار الصفحة." : "Use the left navigation rail or Ctrl+1 through Ctrl+8 to choose a page.");
        AutomationProperties.SetName(DockNavigationItems, ar ? "شريط التنقل الجانبي" : "Left navigation rail");
        ToggleBottomDockButton.ToolTip = _isBottomDockExpanded
            ? (ar ? "طي شريط التنقل الجانبي" : "Collapse navigation rail")
            : (ar ? "توسيع شريط التنقل الجانبي" : "Expand navigation rail");
        AutomationProperties.SetName(ToggleBottomDockButton, ToggleBottomDockButton.ToolTip as string ?? string.Empty);
        AutomationProperties.SetName(ChatHistoryList, ar ? "سجل المحادثات المحفوظة" : "Saved conversation history");
        AutomationProperties.SetHelpText(ChatHistoryList, ar ? "اختر محادثة محفوظة لتحميلها في مساحة الشات." : "Choose a saved conversation to load into the chat.");
        AutomationProperties.SetName(ConnectionStatusText, ar ? "حالة نواة ليال المحلية" : "Layal native core status");
        AutomationProperties.SetName(ChatHistorySelector, ar ? "المحادثات المحفوظة" : "Saved conversations");
        AutomationProperties.SetHelpText(ChatHistorySelector, ar ? "اختر محادثة محفوظة لاستكمالها." : "Choose a saved conversation to resume it.");
        AutomationProperties.SetName(ChatMessagesList, ar ? "رسائل المحادثة" : "Chat messages");
        AutomationProperties.SetHelpText(ChatMessagesList, ar ? "استخدم الأسهم لاستعراض الرسائل." : "Use the arrow keys to review messages.");
        AutomationProperties.SetName(ChatInput, ar ? "الرسالة" : "Message");
        AutomationProperties.SetHelpText(ChatInput, ar ? "اكتب رسالة واضغط Enter للإرسال. Ctrl+1 ينقل التركيز إلى هذا الحقل." : "Type a message and press Enter to send. Ctrl+1 focuses this field.");
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

        HomeTab.Header = ar ? "⌂  نظرة عامة" : "⌂  Overview";
        ChatTab.Header = ar ? "✦  الرئيسية" : "✦  Home";
        TasksTab.Header = ar ? "▣  المهام" : "▣  Tasks";
        SkillsTab.Header = ar ? "◇  المهارات" : "◇  Skills";
        CamerasTab.Header = ar ? "◉  الكاميرات" : "◉  Cameras";
        MemoryTab.Header = ar ? "▤  الذاكرة" : "▤  Memory";
        SettingsTab.Header = ar ? "⚙  الإعدادات" : "⚙  Settings";
        System.Windows.Automation.AutomationProperties.SetName(HomeTab, ar ? "نظرة عامة" : "Overview");
        System.Windows.Automation.AutomationProperties.SetName(ChatTab, ar ? "الرئيسية" : "Home");
        System.Windows.Automation.AutomationProperties.SetName(TasksTab, ar ? "المهام" : "Tasks");
        System.Windows.Automation.AutomationProperties.SetName(SkillsTab, ar ? "المهارات" : "Skills");
        System.Windows.Automation.AutomationProperties.SetName(CamerasTab, ar ? "الكاميرات" : "Cameras");
        System.Windows.Automation.AutomationProperties.SetName(MemoryTab, ar ? "الذاكرة" : "Memory");
        System.Windows.Automation.AutomationProperties.SetName(SettingsTab, ar ? "الإعدادات" : "Settings");
        ToggleBottomDockButton.ToolTip = _isBottomDockExpanded
            ? (ar ? "طي شريط التنقل الجانبي" : "Collapse navigation rail")
            : (ar ? "توسيع شريط التنقل الجانبي" : "Expand navigation rail");
        AutomationProperties.SetName(ToggleBottomDockButton, ToggleBottomDockButton.ToolTip as string ?? string.Empty);
        ChatHistoryHeading.Text = ar ? "المحادثات السابقة" : "Previous conversations";
        ToggleChatHistoryButton.Content = ar ? "سجل المحادثات" : "Chat history";
        ToggleChatHistoryButton.ToolTip = ar ? "إظهار أو إخفاء سجل المحادثات" : "Show or hide chat history";
        AutomationProperties.SetName(ToggleChatHistoryButton, _isChatHistoryPanelOpen
            ? (ar ? "إخفاء سجل المحادثات" : "Hide chat history")
            : (ar ? "إظهار سجل المحادثات" : "Show chat history"));
        ChatHistoryEmptyStateText.Text = ar ? "لا توجد محادثات محفوظة بعد." : "No saved conversations yet.";
        AutomationProperties.SetName(CloseChatHistoryButton, ar ? "إغلاق سجل المحادثات" : "Close chat history");
        AutomationProperties.SetName(DockChatButton, ar ? "الرئيسية - المحادثة" : "Home - Chat");
        AutomationProperties.SetName(DockOverviewButton, ar ? "نظرة عامة" : "Overview");
        AutomationProperties.SetName(DockTasksButton, ar ? "المهام" : "Tasks");
        AutomationProperties.SetName(DockSkillsButton, ar ? "المهارات" : "Skills");
        AutomationProperties.SetName(DockCamerasButton, ar ? "الكاميرات" : "Cameras");
        AutomationProperties.SetName(DockMemoryButton, ar ? "الذاكرة" : "Memory");
        AutomationProperties.SetName(DockApiButton, ar ? "تكاملات API" : "API integrations");
        AutomationProperties.SetName(DockSettingsButton, ar ? "الإعدادات" : "Settings");
        UpdateDockSelection();
        System.Windows.Automation.AutomationProperties.SetName(LanguageButton, ar ? "تغيير لغة الواجهة" : "Change interface language");
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, ThemeButton.ToolTip as string ?? (ar ? "تغيير المظهر" : "Change theme"));
        System.Windows.Automation.AutomationProperties.SetName(ShowMascotButton, ar ? "إظهار الشخصية" : "Show mascot");

        HomeHeading.Text = ar ? "أهلًا، أنا ليال" : "Hello, I'm Layal";
        HomeDescription.Text = ar
            ? "هذه النسخة هي أساس Windows الأصلي. اربط نموذجًا متوافقًا من الإعدادات لتفعيل المحادثة الحقيقية."
            : "Layal’s native text chat and supported tasks work without an external model. Optional API profiles are tested only when you explicitly request it.";
        ClockSectionTitle.Text = ar ? "الوقت والتاريخ" : "Time & date";
        WeatherSectionTitle.Text = ar ? "الطقس" : "Weather";
        RefreshWeatherButton.Content = ar ? "تحديث الطقس" : "Refresh weather";
        WeatherSettingsTitle.Text = ar ? "حالة الطقس في الرئيسية" : "Weather on Home";
        WeatherLocationLabel.Text = ar ? "مدينة بديلة عند عدم إتاحة الموقع" : "Fallback city if location is unavailable";
        UseMyLocationButton.Content = ar ? "اكتشاف موقعي واستخدامه تلقائيًا" : "Detect my location and use it automatically";
        SaveWeatherLocationButton.Content = ar ? "استخدام المدينة يدويًا" : "Use city manually";
        WeatherPrivacyNote.Text = ar
            ? "لا يطلب التطبيق موقع الجهاز عند بدء التشغيل. لا يبدأ الطلب إلا بعد ضغط «اكتشاف موقعي» وتأكيد الإفصاح، ثم موافقة Windows. تُرسل إحداثيات تقريبية إلى Open-Meteo لحساب الطقس والمنطقة الزمنية، ولا تُحفظ في الإعدادات. يمكنك رفض الطلب أو استخدام مدينة يدويًا."
            : "Layal does not request device location at startup. It asks only after you select Detect my location and confirm this disclosure, followed by Windows permission. Approximate coordinates are sent to Open-Meteo for weather and time-zone data and are not saved in settings. You can decline or use a city manually.";
        WeatherLocationInput.ToolTip = ar ? "مدينة بديلة، مثل القاهرة، مصر أو Cairo, Egypt" : "Fallback city, e.g. Cairo, Egypt";
        AutomationProperties.SetName(UseMyLocationButton, ar ? "اكتشاف الموقع الحالي" : "Detect current location");
        AutomationProperties.SetHelpText(UseMyLocationButton, ar
            ? "بعد تأكيد الإفصاح، يطلب Windows إذنًا لقراءة موقع تقريبي وإرسال إحداثياته إلى Open-Meteo للطقس والمنطقة الزمنية."
            : "After explicit consent, requests Windows permission for approximate location; coordinates are sent to Open-Meteo for weather and time-zone data.");
        AutomationProperties.SetName(WeatherLocationInput, ar ? "مدينة الطقس البديلة" : "Fallback weather city");
        AutomationProperties.SetHelpText(WeatherLocationInput, ar
            ? "اختياري: اكتب مدينة لاستخدامها يدويًا بدلًا من تحديد موقع الجهاز."
            : "Optional: enter a city to use manually instead of detecting device location.");
        AutomationProperties.SetName(ClockTimeText, ar ? "الوقت المحلي" : "Local time");
        AutomationProperties.SetName(CurrentDateText, ar ? "التاريخ المحلي" : "Local date");
        AutomationProperties.SetName(WeatherTemperatureText, ar ? "درجة الحرارة الحالية" : "Current temperature");
        AutomationProperties.SetName(WeatherSummaryText, ar ? "وصف حالة الطقس" : "Weather conditions");
        AutomationProperties.SetName(RefreshWeatherButton, ar ? "تحديث حالة الطقس" : "Refresh weather");
        UpdateClockDisplay();
        UpdateWeatherDisplay();
        CardAiTitle.Text = ar ? "نواة ليال المستقلة" : "Layal native core";
        CardAiHint.Text = ar ? "المحادثة والمهام تعملان دون نموذج خارجي." : "Chat and tasks work without an external model.";
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
        GoSettingsButton.Content = ar ? "إعدادات الاتصال" : "Connection settings";
        HomeCaveat.Text = ar
            ? "الردود النصية والتخطيط والمهام والتعلم من الاختصارات تعمل داخل نواة ليال المحلية دون نموذج ذكاء اصطناعي خارجي. الصوت المحلي اختياري ويحتاج موارد Whisper/Piper فقط."
            : "Text replies, planning, tasks, and alias learning run inside Layal's native core without an external AI model. Optional local speech requires Whisper/Piper resources only.";

        ChatHeading.Text = ar ? "المحادثة مع ليال" : "Chat with Layal";
        ChatSectionEyebrow.Text = ar ? "LAYAL / مساعد سطح المكتب" : "LAYAL / DESKTOP ASSISTANT";
        ChatSectionEyebrow.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        AutomationProperties.SetName(ChatSectionEyebrow, ChatSectionEyebrow.Text);
        ChatWelcomeEyebrow.Text = ar ? "مساحة ليال الذكية" : "YOUR AI DESK";
        ChatWelcomeTitle.Text = ar ? "نبدأ بإيه النهارده؟" : "What would you like to do today?";
        AutomationProperties.SetName(ChatWelcomeTitle, ChatWelcomeTitle.Text);
        AutomationProperties.SetHelpText(ChatWelcomeTitle, ar
            ? "اختر اقتراحًا لملء حقل الرسالة أو اكتب سؤالك مباشرة."
            : "Choose a prompt starter to fill the message field, or type your question.");
        ChatNotice.Text = ar
            ? "ليال ترد وتخطط وتنفذ من نواتها المحلية؛ لا يُستدعى نموذج محادثة أو مزود سحابي."
            : "Layal replies, plans, and executes through her native local core; no chat model or cloud provider is called.";
        AutomationProperties.SetName(ConnectionStatusText, ar ? "حالة نواة ليال المحلية" : "Layal native core status");
        AutomationProperties.SetName(ChatProviderStatus, ar ? "حالة الردود النصية والمهام المحلية" : "Local chat and task status");
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
            ? "اكتب سؤالك مباشرة، أو اختر نقطة بداية وسأساعدك في تطويرها."
            : "Type a question or choose a prompt starter to fill the message field. Messages appear here after sending.";
        AutomationProperties.SetName(ChatEmptyStateText, ar ? "حالة المحادثة الفارغة" : "Chat empty state");
        AutomationProperties.SetHelpText(ChatEmptyStateText, ar ? "اكتب سؤالك أو اختر اقتراحًا لملء حقل الرسالة. ستظهر الرسائل هنا بعد الإرسال." : "Type a question or choose a prompt starter to fill the message field. Messages appear here after sending.");
        ChatSuggestionPlanButton.Content = ar ? "✦  ساعدني في التخطيط" : "✦  Help me plan";
        ChatSuggestionSummaryButton.Content = ar ? "▤  لخّص لي محتوى" : "▤  Summarize content";
        ChatSuggestionIdeasButton.Content = ar ? "◇  اقترح أفكارًا" : "◇  Suggest ideas";
        ApplyChatCompactLayout(MainTabs.ActualHeight);
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
        VoiceReplySectionTitle.Text = ar ? "صوت ليال" : "Layal voice";
        AudioInputDeviceLabel.Text = ar ? "الميكروفون المستخدم" : "Input microphone";
        AudioOutputDeviceLabel.Text = ar ? "مخرج صوت ليال" : "Layal audio output";
        RefreshAudioDevicesButton.Content = ar ? "تحديث أجهزة الصوت" : "Refresh audio devices";
        AutomationProperties.SetName(AudioInputDeviceSelector, ar ? "اختيار ميكروفون ليال" : "Choose Layal microphone");
        AutomationProperties.SetHelpText(AudioInputDeviceSelector, ar
            ? "حدد الميكروفون المستخدم للتسجيل والمحادثة المباشرة. لن يتم التبديل بصمت إلى جهاز آخر."
            : "Select the microphone for recording and live conversations. Layal never silently switches to another device.");
        AutomationProperties.SetName(AudioOutputDeviceSelector, ar ? "اختيار مخرج صوت ليال" : "Choose Layal audio output");
        AutomationProperties.SetHelpText(AudioOutputDeviceSelector, ar
            ? "حدد السماعة أو مخرج الصوت لردود ليال والمحادثة الصوتية."
            : "Select the speaker or audio output used for Layal's spoken replies and live conversations.");
        AutomationProperties.SetName(RefreshAudioDevicesButton, ar ? "تحديث قائمة أجهزة الصوت" : "Refresh audio devices");
        VoiceReplyCheckbox.Content = ar ? "نطق الردود بصوت Piper المحلي (اختياري)" : "Speak replies with optional local Piper voice";
        VoiceReplyDisclosureText.Text = ar
            ? "صوت ليال اصطناعي وليس صوت إنسان. يُولّد النطق محليًا عبر Piper؛ لا يُرسل نص الرد إلى أي خدمة صوتية."
            : "Layal's synthetic voice is not a human recording. Speech is generated locally with Piper; reply text is not sent to a voice service.";
        TestVoiceReplyButton.Content = ar ? "اختبار السماعة والنطق" : "Test speaker and speech";
        TestVoiceReplyButton.ToolTip = ar
            ? "يشغّل عينة عربية وإنجليزية عبر Piper المحلي على مخرج الصوت المحدد؛ استمع للتأكد من وضوح الصوت."
            : "Plays an Arabic and English sample with local Piper on the selected output; listen for clear playback.";
        TestMicrophoneButton.Content = ar ? "اختبار الميكروفون (3 ثوانٍ)" : "Test microphone (3 seconds)";
        TestMicrophoneButton.ToolTip = ar
            ? "يسجل عينة قصيرة في الذاكرة فقط، ويقيس مستوى الإشارة ثم يمسحها؛ لا يحفظ الصوت ولا يحوله إلى نص."
            : "Captures a short in-memory sample, measures its signal level, then clears it; audio is not saved or transcribed.";
        AutomationProperties.SetName(TestMicrophoneButton, ar ? "اختبار الميكروفون" : "Test microphone");
        AutomationProperties.SetHelpText(TestMicrophoneButton, ar
            ? "اختبار اختياري لمدة ثلاث ثوانٍ. تحدث أثناء الاختبار؛ تبقى العينة مؤقتًا في الذاكرة ولا تُحفظ أو تُرسل أو تُحوّل إلى نص."
            : "Optional three-second test. Speak during the test; the sample stays temporarily in memory and is not saved, uploaded, or transcribed.");
        AudioInputDiagnosticStatus.Text = string.Empty;
        AssistantPersonaLabel.Text = ar ? "أسلوب صياغة الرد" : "Text response style";
        AssistantPersonaDisclosure.Text = ar
            ? "يتحكم في صياغة النص فقط. اختيارات نبرة الإلقاء والصوت منفصلة عنه، ولا تتغير الهوية أو حدود الأمان أو الصلاحيات."
            : "Controls wording only. Spoken delivery style and voice identity are independent; identity, safety boundaries and permissions never change.";
        foreach (ComboBoxItem personaItem in AssistantPersonaSelector.Items)
        {
            personaItem.Content = (personaItem.Tag?.ToString() ?? string.Empty) switch
            {
                "warm" => ar ? "رفيقة دافئة" : "Warm companion",
                "focused" => ar ? "مركزة ومختصرة" : "Focused and concise",
                "professional" => ar ? "احترافية ومنظمة" : "Professional and structured",
                _ => personaItem.Tag?.ToString() ?? string.Empty
            };
        }
        AutomationProperties.SetName(AssistantPersonaSelector, ar ? "أسلوب صياغة الرد" : "Text response style");
        AutomationProperties.SetHelpText(AssistantPersonaSelector, ar
            ? "اختيار أسلوب النص منفصل عن نبرة الإلقاء واختيار هوية الصوت؛ لا يغيّر قواعد الأمان أو الموافقات أو الصلاحيات."
            : "Text style is independent from delivery style and voice identity; it never changes safety rules, confirmations, or tool permissions.");
        VoiceDeliveryStyleLabel.Text = ar ? "نبرة الإلقاء الصوتي" : "Spoken delivery style";
        VoiceDeliveryStyleDisclosure.Text = ar
            ? "طبيعية أو هادئة أو مشرقة أو واضحة. النطق المحلي عبر Piper يغيّر الإيقاع فقط؛ لا توجد تعليمات صوتية تُرسل إلى خادم."
            : "Natural, gentle, bright, or clear. Local Piper speech changes pace only; no voice instructions are sent to a server.";
        AutomationProperties.SetName(VoiceDeliveryStyleSelector, ar ? "نبرة الإلقاء الصوتي" : "Spoken delivery style");
        AutomationProperties.SetHelpText(VoiceDeliveryStyleSelector, ar
            ? "اختيار مستقل عن أسلوب النص وهوية الصوت. لا يغيّر قواعد الأمان أو صلاحيات الأدوات."
            : "Independent from text style and voice identity. Does not change safety rules or tool permissions.");
        foreach (ComboBoxItem styleItem in VoiceDeliveryStyleSelector.Items)
            styleItem.Content = VoiceDeliveryStylePolicy.GetDisplayName(styleItem.Tag?.ToString(), ar);
        CharacterDisplayModeLabel.Text = ar ? "الشخصية المعروضة" : "Displayed character";
        CharacterDisplayModeDisclosure.Text = ar
            ? "يغيّر الرسومات الظاهرة فقط؛ تظل حالات المساعد وتعبيراته الفعلية محدثة."
            : "Changes only which artwork is visible; actual assistant states and expressions continue to update.";
        AutomationProperties.SetName(CharacterDisplayModeSelector, ar ? "الشخصية المعروضة" : "Displayed character");
        AutomationProperties.SetHelpText(CharacterDisplayModeSelector, ar
            ? "اختر ليال أو الروبوت أو كليهما. هذا لا يغيّر شخصية المساعد أو صلاحياته."
            : "Choose Layal, the robot, or both. This does not change assistant identity or permissions.");
        foreach (ComboBoxItem modeItem in CharacterDisplayModeSelector.Items)
            modeItem.Content = CharacterDisplayModePolicy.GetModeName(modeItem.Tag?.ToString(), ar);
        AutomationProperties.SetName(VoiceReplyCheckbox, ar ? "الردود الصوتية لليال" : "Layal voice replies");
        AutomationProperties.SetHelpText(VoiceReplyCheckbox, ar
            ? "تشغيل قراءة الردود صوتيًا. في الوضع المحلي تُولّد على جهازك بعد تجهيز موارد الصوت."
            : "Read replies aloud. Local mode synthesizes speech on this device after local voice resources are prepared.");
        AutomationProperties.SetName(SpeechToTextButton, ar ? "تسجيل صوت وتحويله إلى نص" : "Record speech and transcribe it");
        AutomationProperties.SetHelpText(SpeechToTextButton, ar
            ? "التسجيل يدوي والتحويل محلي عبر Whisper بعد تجهيز موارده. راجع النص قبل إرساله إلى ليال. لا يُرفع الصوت إلى خادم."
            : "Manual recording and local Whisper transcription after setup. Review the transcript before sending it to Layal. Audio is never uploaded.");
        UpdateSpeechToTextButton();
        UpdateVoiceConversationControls();
        QuickAskButton.Content = ar ? "سؤال سريع" : "Quick Ask";
        GmailInboxButton.Content = ar ? "Gmail" : "Gmail";
        GmailInboxButton.ToolTip = ar
            ? "عرض بريد Gmail محليًا بصلاحية قراءة فقط. لا تتم مشاركته مع AI تلقائيًا."
            : "Preview Gmail locally with read-only access. Messages are not automatically shared with AI.";
        AutomationProperties.SetName(GmailInboxButton, ar ? "فتح بريد Gmail للقراءة فقط" : "Open read-only Gmail inbox");
        AutomationProperties.SetHelpText(GmailInboxButton, ar
            ? "يتطلب موافقة Google الرسمية. لا إرسال أو حذف أو تعديل؛ لن يصل محتوى البريد إلى AI إلا بعد إضافته لمسودة الشات ثم الضغط على إرسال."
            : "Requires Google's official consent. No sending, deleting, or modifying; email content reaches AI only after you add it to the chat draft and press Send.");
        UpdatePrivacyShieldUi();
        QuickAskButton.ToolTip = ar ? "اسأل كتابةً من أي برنامج. الاختصار Alt+Shift+S." : "Ask by typing from any app. Shortcut: Alt+Shift+S.";
        AutomationProperties.SetName(QuickAskButton, ar ? "سؤال سريع" : "Quick Ask");
        AutomationProperties.SetHelpText(QuickAskButton, ar ? "يفتح نافذة كتابة صغيرة. لا تُلتقط الشاشة أو تُرسل ملفات تلقائيًا." : "Opens a small typing window. No screen capture or files are sent automatically.");
        UpdatePrivateChatModeUi();
        CaptureScreenRegionButton.Content = ar ? "قص جزء من الشاشة" : "Crop screen region";
        InspectHoveredElementButton.Content = ar ? "السؤال عن عنصر" : "Ask about an element";
        InspectHoveredElementButton.ToolTip = ar
            ? "بعد موافقتك، اقرأ اسم ونوع عنصر Windows أسفل المؤشر محليًا دون التقاط الشاشة."
            : "After consent, locally read the name and type of the Windows UI element under the pointer without a screenshot.";
        AutomationProperties.SetName(InspectHoveredElementButton, ar ? "فحص عنصر أسفل المؤشر" : "Inspect UI element under pointer");
        AutomationProperties.SetHelpText(InspectHoveredElementButton, ar
            ? "قراءة محدودة لاسم العنصر ونوعه وحدوده فقط. لا تُقرأ قيم الحقول ولا يُرسل شيء إلا بعد ضغط إرسال."
            : "Only the element name, type, and bounds are read. Field values are not read, and nothing is sent until you press Send.");
        CaptureScreenRegionButton.ToolTip = ar
            ? "يخفي ليال مؤقتًا ويعرض معاينة محلية لقص جزء من الشاشة. الرؤية المستقلة غير متاحة حاليًا؛ لن تُرسل الصورة إلى أي نموذج."
            : "Temporarily hides Layal and shows a local screen preview for cropping. Independent vision is unavailable; the image will not be sent to any model.";
        AutomationProperties.SetName(CaptureScreenRegionButton, ar ? "التقاط وقص جزء من الشاشة" : "Capture and crop a screen region");
        AutomationProperties.SetHelpText(CaptureScreenRegionButton, ar
            ? "التقاط صريح للشاشة التي يوجد عليها المؤشر مع معاينة محلية. فهم الصور غير متاح في النواة المستقلة بعد؛ لن تُرسل اللقطة إلى أي نموذج."
            : "Explicitly captures the display under the pointer for local preview. Image understanding is not yet available in the native core; the capture will not be sent to any model.");
        ChooseImageButton.Content = ar ? "معاينة صورة محليًا" : "Preview image locally";
        ChooseImageButton.ToolTip = ar
            ? "اختر صورة لمعاينتها محليًا فقط؛ تحليل الصور المستقل غير متاح ولن تُرسل إلى أي نموذج."
            : "Choose an image for local preview only; independent image analysis is unavailable and it will not be sent to any model.";
        ClearVisionImageButton.Content = ar ? "إزالة الصورة" : "Remove image";
        ChooseDocumentButton.Content = ar ? "إرفاق ملف نصي/PDF/Office" : "Attach text/PDF/Office file";
        ChooseDocumentButton.ToolTip = ar
            ? "يُستخرج النص محليًا وتراجعه قبل الإرفاق؛ لا يُرسل إلا عند الضغط على إرسال."
            : "Text is extracted locally and reviewed before attachment; it is sent only when you press Send.";
        AutomationProperties.SetName(ChooseDocumentButton, ar ? "اختيار ملف لمراجعته وإرفاقه" : "Choose a document to review and attach");
        AutomationProperties.SetHelpText(ChooseDocumentButton, ar
            ? "TXT وMarkdown وCSV وJSON وXML وHTML وPDF وDOCX وXLSX وصور PNG/JPG/BMP/TIFF؛ حد الملف 20 ميجابايت والنص 10,000 حرف. OCR عربي/إنجليزي محلي للصور وصفحات PDF الممسوحة. لا تُنفّذ وحدات ماكرو."
            : "TXT, Markdown, CSV, JSON, XML, HTML, PDF, DOCX, XLSX, PNG/JPG/BMP/TIFF; 20 MB file and 10,000-character text limits. Arabic/English OCR runs locally for images and scanned PDF pages. Macros are never executed.");
        ClearDocumentButton.Content = ar ? "إزالة الملف" : "Remove document";
        ReadClipboardButton.Content = ar ? "قراءة الحافظة" : "Read clipboard";
        ReadClipboardButton.ToolTip = ar
            ? "اقرأ النص من الحافظة بطلب صريح، وراجعه محليًا قبل إرفاقه."
            : "Read clipboard text only after an explicit request, then review it locally before attaching.";
        AutomationProperties.SetName(ReadClipboardButton, ar ? "قراءة نص الحافظة لمراجعته" : "Read clipboard text for review");
        AutomationProperties.SetHelpText(ReadClipboardButton, ar
            ? "قراءة نص الحافظة لا تغيّر محتواها ولا ترسله. الإرسال لا يحدث إلا بعد الضغط على إرسال."
            : "Reading clipboard text does not modify or transmit it. Nothing is sent until you press Send.");
        PasteClipboardImageButton.Content = ar ? "معاينة صورة الحافظة محليًا" : "Preview clipboard image locally";
        PasteClipboardImageButton.ToolTip = ar
            ? "بعد موافقتك، تُقرأ صورة الحافظة وتظهر كمعاينة محلية فقط؛ لا يتوفر تحليل مستقل ولن تُرسل الصورة إلى أي نموذج."
            : "With your consent, read the clipboard image for local preview only; independent analysis is unavailable and the image will not be sent to any model.";
        AutomationProperties.SetName(PasteClipboardImageButton, ar ? "معاينة صورة الحافظة محليًا بعد الموافقة" : "Preview clipboard image locally with consent");
        AutomationProperties.SetHelpText(PasteClipboardImageButton, ar
            ? "حد 40 مليون بكسل و10 ميجابايت. تُعرض الصورة محليًا فقط؛ فهم الصور المستقل غير متاح ولن تُرسل إلى أي نموذج."
            : "40-million-pixel and 10 MB limits. The image is previewed locally only; independent vision is unavailable and it will not be sent to any model.");
        CameraVisionButton.Content = ar ? "فتح الكاميرا" : "Open camera";
        CameraVisionButton.ToolTip = ar
            ? "معاينة الكاميرا محليًا فقط؛ تحليل الصور غير متاح في النواة المستقلة حاليًا، ولا تُرسل أي لقطة إلى نموذج."
            : "Local camera preview only; image analysis is not yet available in the native core and no frame is sent to a model.";
        AutomationProperties.SetName(ChooseImageButton, ar ? "معاينة صورة محليًا" : "Preview image locally");
        AutomationProperties.SetHelpText(ChooseImageButton, ar
            ? "تظل الصورة على الجهاز للمعاينة فقط. تحليل الصور المستقل غير متاح؛ لن تُرسل الصورة إلى أي نموذج."
            : "The image remains on this device for preview only. Independent image analysis is unavailable; the image will not be sent to any model.");
        AutomationProperties.SetName(CameraVisionButton, ar ? "فتح كاميرا الرؤية" : "Open vision camera");
        AutomationProperties.SetHelpText(CameraVisionButton, ar
            ? "يفتح معاينة الكاميرا المحلية بعد موافقتك. التحليل اليدوي والتلقائي غير متاحين؛ لا تغادر أي لقطة الجهاز."
            : "Opens a local camera preview after consent. Manual and automatic analysis are unavailable; no camera frame leaves this device.");
        UpdateVisionAttachmentControls();
        UpdateDocumentAttachmentUi();
        UpdateCameraVisionIndicator();
        ApplyNavigationRailPresentation();
        UpdateExpanderHeaderAccessibility();

        TasksHeading.Text = ar ? "مركز المهام" : "Task center";
        ManageRemindersButton.Content = ar ? "التذكيرات" : "Reminders";
        ManageRemindersButton.ToolTip = ar
            ? "إدارة تذكيرات محلية مشفرة؛ يجدول Windows تشغيل ليال عند الموعد في جلسة المستخدم."
            : "Manage encrypted local reminders; Windows schedules Layal to launch at the due time in your signed-in session.";
        AutomationProperties.SetName(ManageRemindersButton, ar ? "إدارة التذكيرات المحلية" : "Manage local reminders");
        AutomationProperties.SetHelpText(ManageRemindersButton, ar
            ? "أنشئ تذكيرًا لمرة واحدة أو يوميًا أو أسبوعيًا. تُحفظ التذكيرات مشفرة محليًا؛ يجدول Windows تشغيل ليال بعد تسجيل الدخول ولا يوقظ الجهاز."
            : "Create one-time, daily, or weekly reminders. They are encrypted locally; Windows starts Layal after sign-in and does not wake the computer.");
        ManageRoutinesButton.Content = ar ? "الروتينات" : "Routines";
        UpdateGameModeButtonUi();
        GameModeSettingsButton.Content = ar ? "إعداد اكتشاف الألعاب" : "Game detection settings";
        GameModeSettingsButton.ToolTip = ar
            ? "كشف اختياري ومغلق افتراضيًا؛ يطابق اسم عملية النافذة الأمامية مع قائمة سماح تحددها."
            : "Optional and off by default; matches the foreground process name against your allowlist.";
        AutomationProperties.SetName(GameModeSettingsButton, ar ? "إعداد الكشف التلقائي للألعاب" : "Configure automatic game detection");
        AutomationProperties.SetHelpText(GameModeSettingsButton, ar
            ? "يقرأ اسم عملية النافذة الأمامية فقط. لا يقرأ العنوان أو سطر الأوامر أو ذاكرة اللعبة ولا يحقن مدخلات."
            : "Reads only the foreground process name. No window title, command line, game memory, or input injection.");
        WorkspaceHubButton.Content = ar ? "مساحة العمل" : "Workspace hub";
        WorkspaceHubButton.ToolTip = ar
            ? "بحث محلي موحد في المهام والملاحظات وملفات LayalWorkspace."
            : "Unified local search across tasks, memories, and LayalWorkspace files.";
        AutomationProperties.SetName(WorkspaceHubButton, ar ? "فتح مساحة عمل ليال الموحدة" : "Open Layal workspace hub");
        AutomationProperties.SetHelpText(WorkspaceHubButton, ar
            ? "يعرض المهام والملاحظات وبيانات الملفات محليًا. لا يقرأ محتوى الملفات تلقائيًا؛ التصدير غير مشفر بعد تأكيدك."
            : "Shows tasks, memories, and file metadata locally. File contents are not read automatically; export is unencrypted and requires confirmation.");
        ManageRoutinesButton.ToolTip = ar
            ? "احفظ أهدافًا نصية متكررة؛ الاختيار يملأ مسودة المهمة فقط."
            : "Save recurring text goals; choosing one only fills the task draft.";
        AutomationProperties.SetName(ManageRoutinesButton, ar ? "إدارة الروتينات المحفوظة" : "Manage saved routines");
        AutomationProperties.SetHelpText(ManageRoutinesButton, ar
            ? "قوالب نصية مشفرة محليًا وليست سكربتات؛ راجع الهدف واضغط تنفيذ المهمة بنفسك."
            : "Locally encrypted text templates, not scripts. Review the goal and press Run task yourself.");
        ManageRoutinesButton.Content = ar ? "الروتينات" : "Routines";
        ManageRoutinesButton.ToolTip = ar
            ? "احفظ أهدافًا نصية متكررة؛ الاختيار يملأ مسودة المهمة فقط."
            : "Save recurring text goals; choosing one only fills the task draft.";
        AutomationProperties.SetName(ManageRoutinesButton, ar ? "إدارة الروتينات المحفوظة" : "Manage saved routines");
        AutomationProperties.SetHelpText(ManageRoutinesButton, ar
            ? "قوالب نصية مشفرة محليًا وليست سكربتات؛ راجع الهدف واضغط تنفيذ المهمة بنفسك."
            : "Locally encrypted text templates, not scripts. Review the goal and press Run task yourself.");
        TasksDescription.Text = ar
            ? "اكتب مهمة بالعربية أو الإنجليزية. تستخدم ليال استدعاء الأدوات المحددة، وتطلب موافقتك قبل الإجراءات التي تغيّر الملفات أو تفتح تطبيقات أو تشارك بيانات."
            : "Describe a task in Arabic or English. Layal uses allow-listed tools and requests approval before file changes, application launches, or data disclosure.";
        TaskSafeToolTitle.Text = ar ? "أدوات Windows المحدودة" : "Bounded Windows tools";
        TaskSafeToolDescription.Text = ar ? "الأدوات المتاحة محددة؛ لا يوجد وصول عام غير مقيد إلى سطر الأوامر أو الملفات." : "Available tools are allow-listed; there is no unrestricted shell or filesystem access.";
        ProactiveCheckInEnabledCheckBox.Content = ar ? "تذكيرات متابعة استباقية" : "Proactive check-in reminders";
        ProactiveCheckInEnabledCheckBox.ToolTip = ar
            ? "إشعارات محلية حسب الفاصل الذي تختاره؛ لا تراقب الشاشة أو التطبيقات ولا تستدعي AI في الخلفية."
            : "Local notifications at your chosen interval; no screen/app monitoring or background AI calls.";
        AutomationProperties.SetName(ProactiveCheckInEnabledCheckBox, ar ? "تفعيل تذكيرات المتابعة المجدولة" : "Enable scheduled check-in reminders");
        AutomationProperties.SetHelpText(ProactiveCheckInEnabledCheckBox, ar
            ? "ميزة استباقية محدودة وآمنة: إشعار محلي فقط. لا تفحص الشاشة أو التطبيقات ولا تستخدم الشبكة."
            : "A limited privacy-safe proactive feature: local notifications only. It does not inspect the screen/apps or use the network.");
        var checkInIntervals = ProactiveCheckInIntervalSelector.Items.OfType<ComboBoxItem>().ToArray();
        var intervalLabels = ar ? new[] { "15 دقيقة", "30 دقيقة", "60 دقيقة", "120 دقيقة" } : new[] { "15 minutes", "30 minutes", "60 minutes", "120 minutes" };
        for (var i = 0; i < Math.Min(checkInIntervals.Length, intervalLabels.Length); i++)
            checkInIntervals[i].Content = intervalLabels[i];
        AutomationProperties.SetName(ProactiveCheckInIntervalSelector, ar ? "الفاصل الزمني لتذكير المتابعة" : "Check-in reminder interval");
        AutomationProperties.SetHelpText(ProactiveCheckInIntervalSelector, ar
            ? "إشعارات متابعة محلية كل 15 أو 30 أو 60 أو 120 دقيقة. يجدول Windows تشغيل ليال في جلسة المستخدم عند الموعد."
            : "Local check-in notifications every 15, 30, 60, or 120 minutes. Windows schedules Layal to launch in your signed-in session.");
        ProactiveCheckInPrivacyNote.Text = ar
            ? "الوضع الاستباقي هنا إشعار محلي فقط: لا يفحص النوافذ أو الشاشة أو الملفات ولا يستدعي نموذج AI."
            : "Proactive mode here is a local reminder only: no window, screen, or file inspection, and no AI model calls.";
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
            ? "أضف اتصالات REST/JSON للتكاملات الخارجية. كتالوج هذه الاتصالات منفصل عن نواة ليال؛ لن يستخدمها لتوليد الردود أو التخطيط أو اتخاذ القرارات."
            : "Add REST/JSON connections for external integrations. This catalog is separate from Layal's native core and is not used to generate replies, plan, or make decisions.";
        ApiConnectionsListLabel.Text = ar ? "التطبيقات المتصلة" : "Connected applications";
        ApiConnectionSearchInput.ToolTip = ar ? "ابحث بالاسم أو عنوان API أو التفاصيل" : "Search by name, API URL, or details";
        System.Windows.Automation.AutomationProperties.SetName(ApiConnectionSearchInput, ar ? "البحث في اتصالات API" : "Search API connections");
        System.Windows.Automation.AutomationProperties.SetHelpText(ApiConnectionSearchInput, ar
            ? "يبحث في الاسم والعنوان والتفاصيل فقط، ولا يبحث في مفاتيح المصادقة."
            : "Searches names, URLs and details only; authentication secrets are never searched.");
        NewApiConnectionButton.Content = ar ? "إضافة اتصال جديد" : "Add connection";
        ApiConnectionsPrivacyNote.Text = ar
            ? "لا تضع المفاتيح في المحادثة أو ملاحظات التوثيق. أدخلها هنا؛ تُحفظ بتشفير Windows DPAPI لهذا المستخدم."
            : "Do not paste API keys in chat or API notes. Enter them here; they are stored with Windows DPAPI for this Windows user.";
        ApiConnectionNameLabel.Text = ar ? "اسم التطبيق / الاتصال (إجباري)" : "Application / connection name (required)";
        ApiConnectionBaseUrlLabel.Text = ar ? "عنوان API الأساسي (إجباري)" : "API base URL (required)";
        ApiConnectionModelIdLabel.Text = ar ? "معرّف النموذج (اختياري، مطلوب عند استخدامه كـ API ليال)" : "Model ID (optional; required when used as Layal API)";
        ApiConnectionImageLabel.Text = ar ? "صورة التطبيق (اختياري)" : "Application image (optional)";
        ChooseApiImageButton.Content = ar ? "اختيار صورة..." : "Choose image...";
        ClearApiImageButton.Content = ar ? "مسح الصورة" : "Clear image";
        ApiAuthTypeLabel.Text = ar ? "طريقة المصادقة" : "Authentication type";
        ApiAuthHeaderNameLabel.Text = ar ? "اسم ترويسة المصادقة (Custom header)" : "Authentication header name (Custom header)";
        ApiAuthSecretLabel.Text = ar ? "Token / API key / قيمة الترويسة" : "Token / API key / header value";
        ApiAdditionalHeadersLabel.Text = ar ? "ترويسات HTTP إضافية غير سرية (JSON)" : "Additional non-secret HTTP headers (JSON)";
        ApiTestPathLabel.Text = ar ? "مسار اختبار الاتصال (GET)، مثل health أو users/me" : "Connection test path (GET), e.g. health or users/me";
        ApiInstructionsLabel.Text = ar ? "التفاصيل وملاحظات API (اختياري، بدون أسرار)" : "Details and API notes (optional; do not include secrets)";
        SaveApiConnectionButton.Content = ar ? "حفظ الاتصال" : "Save connection";
        UseAsLayalApiButton.Content = ar ? "استخدام هذا كـ API ليال" : "Use as Layal API";
        TestApiConnectionButton.Content = ar ? "اختبار API" : "Test API";
        DeleteApiConnectionButton.Content = ar ? "حذف الاتصال المحدد" : "Delete selected connection";
        ((ComboBoxItem)ApiAuthTypeComboBox.Items[0]).Content = ar ? "بدون مصادقة / None" : "No authentication";
        ((ComboBoxItem)ApiAuthTypeComboBox.Items[1]).Content = "Bearer token";
        ((ComboBoxItem)ApiAuthTypeComboBox.Items[2]).Content = ar ? "ترويسة مخصصة (API key)" : "Custom header (API key)";
        UpdateLayalApiButtonLabel();

        SettingsHeading.Text = ar ? "الإعدادات والاتصال" : "Settings and connections";
        LanguageSettingLabel.Text = ar ? "لغة الواجهة" : "Interface language";
        LocalAiSectionTitle.Text = ar ? "نواة ليال المستقلة والصوت المحلي الاختياري" : "Layal native core and optional local speech";
        UseLocalAiCheckbox.Content = ar ? "المعالجة المحلية فقط (ثابت)" : "Local processing only (fixed)";
        LocalAiDescription.Text = ar
            ? "الردود النصية والمهام لا تحتاج إلى تنزيل نماذج. يمكنك اختياريًا تنزيل Whisper/Piper لتحويل الكلام والنطق فقط؛ لن تُستخدم موارد الصوت للتفكير أو اتخاذ القرارات."
            : "Text replies and tasks need no model downloads. You may optionally install Whisper/Piper for speech input/output only; speech resources are not used for reasoning or decisions.";
        SetupLocalAiButton.Content = ar ? "تجهيز الصوت المحلي فقط" : "Set up local speech only";
        CancelLocalAiSetupButton.Content = ar ? "إلغاء التنزيل" : "Cancel downloads";
        AutomationProperties.SetName(CancelLocalAiSetupButton, ar ? "إلغاء تنزيل موارد الذكاء الاصطناعي المحلي" : "Cancel local AI downloads");
        AutomationProperties.SetName(UseLocalAiCheckbox, ar ? "وضع الذكاء الاصطناعي المحلي" : "Local AI mode");
        AutomationProperties.SetHelpText(UseLocalAiCheckbox, ar
            ? "الردود والتخطيط والمهام تستخدم نواة ليال المستقلة دائمًا. موارد الصوت المحلية اختيارية."
            : "Replies, planning, and tasks always use Layal's native core. Local speech resources are optional.");
        ProviderSectionTitle.Text = ar ? "اختبار اتصال API خارجي (لا يدخل في تفكير ليال أو ردودها أو تنفيذ مهامها)" : "External API connectivity test (not used by Layal for chat, reasoning, or task execution)";
        ArabicLanguageButton.Content = "العربية";
        EnglishLanguageButton.Content = "English";
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
        QuickAskHotkeyLabel.Text = ar ? "اختصار السؤال السريع العالمي" : "Global Quick Ask shortcut";
        var hotkeyItems = QuickAskHotkeySelector.Items.OfType<ComboBoxItem>().ToArray();
        for (var i = 0; i < hotkeyItems.Length; i++)
        {
            var preset = hotkeyItems[i].Tag?.ToString();
            hotkeyItems[i].Content = GlobalHotkeyService.GetDisplayName(preset);
        }
        AutomationProperties.SetName(QuickAskHotkeySelector, ar ? "اختصار السؤال السريع العالمي" : "Global Quick Ask shortcut");
        AutomationProperties.SetHelpText(QuickAskHotkeySelector, ar
            ? "اختر اختصارًا عالميًا. إذا كان مستخدمًا من تطبيق آخر، يظل زر السؤال السريع داخل ليال متاحًا."
            : "Choose a global shortcut. If another app owns it, the in-app Quick Ask button remains available.");
        ThemePresetLabel.Text = ar ? "مجموعة ألوان الواجهة" : "Interface color preset";
        ThemePresetPrivacyNote.Text = ar
            ? "مجموعة الألوان إعداد محلي. تظل إعدادات التباين العالي في Windows هي الأولوية."
            : "The color preset is local. Windows High Contrast always takes priority.";
        var themeItems = ThemePresetSelector.Items.OfType<ComboBoxItem>().ToArray();
        var themeLabels = ar
            ? new[] { "Classic — الكلاسيكي", "Ocean — المحيط", "Forest — الغابة", "Sunset — الغروب" }
            : new[] { "Classic", "Ocean", "Forest", "Sunset" };
        for (var i = 0; i < Math.Min(themeItems.Length, themeLabels.Length); i++)
            themeItems[i].Content = themeLabels[i];
        AutomationProperties.SetName(ThemePresetSelector, ar ? "مجموعة ألوان الواجهة" : "Interface color preset");
        AutomationProperties.SetHelpText(ThemePresetSelector, ar
            ? "اختر Classic أو Ocean أو Forest أو Sunset. الفاتح/الداكن مستقل، والتباين العالي في Windows له الأولوية."
            : "Choose Classic, Ocean, Forest, or Sunset. Light/dark is independent; Windows High Contrast takes priority.");
        MascotSettingsTitle.Text = ar ? "إعدادات الشخصية العائمة" : "Floating mascot settings";
        AlwaysOnTopCheckbox.Content = ar ? "إبقاء الشخصية فوق النوافذ الأخرى" : "Keep mascot above other windows";
        AnimationsCheckbox.Content = ar ? "تفعيل حركة الشخصية والرمش" : "Enable mascot movement and blinking";
        PerformanceModeCheckbox.Content = ar ? "وضع الأداء العالي (إيقاف المؤثرات الثقيلة)" : "High-performance mode (pause heavy effects)";
        PerformanceMetricsButton.Content = ar ? "قياس موارد ليال الآن" : "Measure Layal resources";
        AutomationProperties.SetName(PerformanceMetricsButton, ar ? "قياس موارد عملية ليال" : "Measure Layal process resources");
        AutomationProperties.SetHelpText(PerformanceMetricsButton, ar
            ? "عينة محلية لمدة ثانية واحدة لوقت CPU كنسبة من سعة الأنوية المنطقية، والذاكرة الفعلية والخاصة و.NET Heap وعدد الخيوط وفروق عدادات GC داخل العينة. لا تجمع بيانات عن تطبيقات أخرى ولا تقيس GPU."
            : "One-second local sample of process CPU normalized to logical-processor capacity, working set, private memory, .NET heap, thread count, and GC collections during the sample. No other processes or GPU telemetry are collected.");
        AutomationProperties.SetName(PerformanceMetricsStatus, ar ? "نتيجة آخر قياس أداء" : "Latest performance measurement");
        PerformanceModeCheckbox.ToolTip = ar
            ? "يوقف حركة الشخصية والرمش والجسيمات وفقاعات التعبير، يوقف معاينة GLB ثلاثية الأبعاد، ويخفض فحص تمرير النقرات إلى نحو 30 مرة/ثانية. لا يغيّر تفضيل الرسوم المتحركة المحفوظ."
            : "Pauses mascot motion, blinking, particles and reaction bubbles, stops the GLB 3D preview, and reduces click-through hit testing to about 30 checks per second. The saved animation preference is preserved.";
        AutomationProperties.SetName(PerformanceModeCheckbox, ar ? "تفعيل وضع الأداء العالي للشخصية" : "Enable mascot high-performance mode");
        AutomationProperties.SetHelpText(PerformanceModeCheckbox, ar
            ? "يوقف مؤقتات الحركة والرمش ويزيل المؤثرات المرئية فورًا ويحرر نموذج GLB الاختياري؛ يبقي فحص الشفافية للماوس بمعدل أقل أثناء ظهور الشخصية، ويوقف المؤقتات عند إخفائها. تعود الحركة ومعاينة GLB المحفوظة عند إيقاف الوضع. يحترم أيضًا إعداد Windows لتعطيل حركة عناصر الواجهة."
            : "Stops motion/blink timers, clears visible effects immediately, and disposes the optional GLB renderer; keeps transparent-window hit testing at a lower rate while visible and stops polling when hidden. Saved animation and GLB preferences are restored when disabled. Windows client-area animation settings are also respected.");
        MascotSizeLabel.Text = ar ? "حجم الشخصية" : "Mascot size";
        MascotOpacityLabel.Text = ar ? "شفافية الشخصية" : "Mascot opacity";
        System.Windows.Automation.AutomationProperties.SetName(MascotSizeSlider, MascotSizeLabel.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(MascotSizeSlider, ar ? "عدّل القيمة باستخدام الأسهم بعد الوصول إلى شريط التمرير." : "Use the arrow keys to adjust this slider when focused.");
        System.Windows.Automation.AutomationProperties.SetName(MascotOpacitySlider, MascotOpacityLabel.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(MascotOpacitySlider, ar ? "عدّل القيمة باستخدام الأسهم بعد الوصول إلى شريط التمرير." : "Use the arrow keys to adjust this slider when focused.");
        ApplyMascotSettingsButton.Content = ar ? "تطبيق إعدادات الشخصية" : "Apply mascot settings";
        SettingsLimitations.Text = ar
            ? "الردود النصية والمهام المحددة تعمل من نواة ليال المستقلة دون نموذج محادثة. الفهم العام والتدريب الإحصائي والرؤية الحاسوبية المستقلة ما زالت قيد التنفيذ. موارد الصوت المحلية اختيارية ولا تُستخدم للتفكير أو القرارات."
            : "Text replies and supported tasks run through Layal's native core without a chat model. General open-ended understanding, statistical training, and native computer vision remain under development. Optional local speech resources are not used for reasoning or decisions.";
        WindowInspectionCheckbox.Content = ar ? "السماح بفحص النوافذ وعناصر الواجهة وتنفيذ فعل واحد بموافقة" : "Allow window/UI inspection and one approved control action";
        MemoryHeading.Text = ar ? "الذاكرة الشخصية" : "Personal memory";
        LoreWebButton.Content = ar ? "خريطة الذاكرة 3D" : "3D memory web";
        LoreWebButton.ToolTip = ar
            ? "عرض الملاحظات وروابطها التقريبية محليًا؛ لا تُرسل إلى AI."
            : "View notes and approximate links locally; nothing is sent to AI.";
        AutomationProperties.SetName(LoreWebButton, ar ? "فتح خريطة الذاكرة ثلاثية الأبعاد" : "Open 3D memory web");
        AutomationProperties.SetHelpText(LoreWebButton, ar
            ? "خريطة محلية للملاحظات النشطة والمؤرشفة؛ تعرض التصنيف وتتيح الأرشفة والاستعادة. الروابط تقريبية بحسب التصنيف وكلمات العنوان والترتيب الزمني."
            : "Local map of active and archived notes; shows categories and supports archive/restore. Links are approximate, based on category, title terms and chronology.");
        MemoryDescription.Text = ar
            ? "ملاحظاتك الشخصية تُشفّر محليًا بحماية Windows DPAPI. يمكنك البحث حسب العنوان والمحتوى والتصنيف، وعرض الأرشيف واستعادة الملاحظات وتحريرها أو حذفها بعد مراجعة النص الكامل. لا تُشارك الملاحظات مع AI تلقائيًا."
            : "Your personal notes are encrypted locally with Windows DPAPI. Search titles, content, and categories; show archived notes; restore, edit, or delete after reviewing the complete note. Notes are never shared with AI automatically.";
        MemoryEmptyStateText.Text = ar
            ? "لا توجد ملاحظات محفوظة. أنشئ ملاحظة جديدة للبدء."
            : "No saved notes yet. Create a note to get started.";
        AutomationProperties.SetName(MemoryEmptyStateText, ar ? "حالة الذاكرة الفارغة" : "Memory empty state");
        AutomationProperties.SetHelpText(MemoryEmptyStateText, ar ? "استخدم زر ملاحظة جديدة لإضافة محتوى إلى الذاكرة." : "Use New note to add content to memory.");
        MemorySearchInput.ToolTip = ar ? "بحث في العناوين والمحتوى والتصنيفات" : "Search note titles, content, and categories";
        AutomationProperties.SetName(MemorySearchInput, ar ? "البحث في الذاكرة" : "Search memory");
        AutomationProperties.SetHelpText(MemorySearchInput, ar ? "اكتب عبارة واضغط Enter أو زر بحث. البحث محلي." : "Enter a phrase and press Enter or Search. Search is local.");
        MemorySearchButton.Content = ar ? "بحث" : "Search";
        AutomationProperties.SetName(MemorySearchButton, ar ? "بحث في الملاحظات المحلية" : "Search local notes");
        NewMemoryButton.Content = ar ? "ملاحظة جديدة" : "New note";
        AutomationProperties.SetName(NewMemoryButton, ar ? "إنشاء ملاحظة جديدة" : "Create a new note");
        ImportMemoryButton.Content = ar ? "استيراد JSON" : "Import JSON";
        AutomationProperties.SetName(ImportMemoryButton, ar ? "استيراد نسخة ذاكرة JSON" : "Import memory JSON backup");
        AutomationProperties.SetHelpText(ImportMemoryButton, ar ? "استيراد دمجي محدود الحجم؛ يتخطى معرّفات الملاحظات الموجودة ولا يستبدلها." : "Size-limited merge import; existing note IDs are skipped, never replaced.");
        ExportMemoryButton.Content = ar ? "تصدير JSON" : "Export JSON";
        AutomationProperties.SetName(ExportMemoryButton, ar ? "تصدير كل الملاحظات إلى JSON غير مشفر" : "Export all notes as unencrypted JSON");
        MemoryShowArchivedCheckBox.Content = ar ? "عرض المؤرشفة" : "Include archived";
        AutomationProperties.SetName(MemoryShowArchivedCheckBox, ar ? "عرض الملاحظات المؤرشفة" : "Include archived notes");
        AutomationProperties.SetHelpText(MemoryShowArchivedCheckBox, ar ? "أظهر الملاحظات المؤرشفة لعرضها أو استعادتها." : "Show archived notes so they can be viewed or restored.");
        MemoryCategoryFilterLabel.Text = ar ? "تصفية التصنيف" : "Category filter";
        AutomationProperties.SetName(MemoryCategoryFilterLabel, MemoryCategoryFilterLabel.Text);
        MemoryCategoryFilter.ToolTip = ar ? "اعرض ملاحظات تصنيف محدد" : "Show notes in a selected category";
        AutomationProperties.SetName(MemoryCategoryFilter, ar ? "تصفية حسب التصنيف" : "Filter by category");
        AutomationProperties.SetHelpText(MemoryCategoryFilter, ar ? "اختر تصنيفًا أو كل التصنيفات. تبقى البيانات محلية." : "Choose one category or All categories. Data remains local.");
        ClearMemoryFiltersButton.Content = ar ? "مسح عوامل التصفية" : "Clear filters";
        AutomationProperties.SetName(ClearMemoryFiltersButton, ar ? "مسح بحث وتصفيات الذاكرة" : "Clear memory search and filters");
        if (MemoryCategoryFilter.Items.Count > 0 && MemoryCategoryFilter.Items[0] is ComboBoxItem allCategories)
            allCategories.Content = ar ? "كل التصنيفات" : "All categories";
        MemoryCategoryLabel.Text = ar ? "التصنيف" : "Category";
        AutomationProperties.SetName(MemoryCategoryInput, MemoryCategoryLabel.Text);
        MemoryTitleLabel.Text = ar ? "العنوان" : "Title";
        MemoryContentLabel.Text = ar ? "المحتوى" : "Content";
        AutomationProperties.SetName(MemoryTitleInput, MemoryTitleLabel.Text);
        AutomationProperties.SetName(MemoryContentInput, MemoryContentLabel.Text);
        SaveMemoryButton.Content = ar ? "حفظ الملاحظة" : "Save note";
        ArchiveMemoryButton.Content = _selectedMemoryIsArchived
            ? (ar ? "استعادة" : "Restore")
            : (ar ? "أرشفة" : "Archive");
        DeleteMemoryButton.Content = ar ? "حذف الملاحظة" : "Delete note";
        AutomationProperties.SetName(MemoryList, ar ? "قائمة ملاحظات الذاكرة" : "Memory notes list");
        AutomationProperties.SetHelpText(MemoryList, ar ? "اختر ملاحظة للعرض والتحرير. قد تخفي عوامل التصفية الملاحظة المفتوحة في المحرر." : "Select a note to view and edit. Filters may hide the note currently open in the editor.");
        MemoryPrivacyNote.Text = ar
            ? "لا تحفظ مفاتيح API أو كلمات المرور. تصدير JSON غير مشفّر ويشمل المؤرشف والنشط؛ الاستيراد يدمج السجلات الجديدة ولا يستبدل الموجود. لا تُرسل أي ملاحظة إلى AI إلا بعد عرض النص الكامل وموافقتك."
            : "Do not store API keys or passwords. JSON exports are unencrypted and include active and archived notes; imports merge new records without replacing existing ones. Notes are shared with AI only after the full text is shown and you approve.";
        UpdateMemoryEditorButtons();
        RefreshMemoryNotes();
        UpdateMemoryCountDisplay();

        UpdateClockDisplay();
        UpdateWeatherDisplay();
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

    private void UpdateLocalAiStatusText()
    {
        if (LocalAiStatusText is null) return;
        LocalAiStatusText.Text = _localVoiceReady
            ? (IsArabic
                ? "نواة ليال النصية والمهام جاهزة دون أي نموذج خارجي. موارد الصوت المحلي جاهزة أيضًا."
                : "Layal's text and task core is ready without an external model. Local speech resources are ready too.")
            : (IsArabic
                ? "نواة ليال النصية والمهام جاهزة دون تنزيل نماذج. الصوت المحلي اختياري ويحتاج تجهيز Whisper/Piper فقط."
                : "Layal's text and task core is ready without model downloads. Local speech is optional and only needs Whisper/Piper resources.");
    }

    private async Task RefreshLocalAiStatusAsync()
    {
        try
        {
            _localVoiceReady = await _localVoiceService.ValidateCachedResourcesAsync();
            UpdateLocalAiStatusText();
            RefreshProviderStatus();
        }
        catch (Exception ex)
        {
            LocalAiStatusText.Text = (IsArabic ? "تعذر فحص موارد الصوت المحلي: " : "Could not inspect local speech resources: ") + ex.Message;
        }
    }

    private void LocalAiModeSettingChanged(object sender, RoutedEventArgs e)
    {
        // Legacy preference migration: cloud speech is no longer an execution fallback.
        AppRuntime.Settings.UseLocalAi = true;
        UseLocalAiCheckbox.IsChecked = true;
        UseLocalAiCheckbox.IsEnabled = false;
        if (IsLoaded)
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            ApplyLanguage();
            RefreshProviderStatus();
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "تم تثبيت المعالجة المحلية للصوت. الردود النصية والمهام تعمل دائمًا بنواة ليال المستقلة."
                : "Local speech processing is selected. Text replies and tasks always use Layal's native core.");
        }
    }

    private async void SetupLocalAiButton_Click(object sender, RoutedEventArgs e)
    {
        UseLocalAiCheckbox.IsChecked = true;
        AppRuntime.Settings.UseLocalAi = true;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        await EnsureLocalAiSetupAsync(includeVoice: true, retryVoice: true);
        ApplyLanguage();
        RefreshProviderStatus();
    }

    private void CancelLocalAiSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_localAiSetupCancellation is null) return;
        _localAiSetupCancellation.Cancel();
        CancelLocalAiSetupButton.IsEnabled = false;
        LocalAiStatusText.Text = IsArabic ? "جارٍ إلغاء الإعداد وتنظيف التنزيل المؤقت…" : "Cancelling setup and cleaning up temporary downloads…";
    }

    private async Task<bool> EnsureLocalAiSetupAsync(
        bool includeVoice,
        bool retryVoice = false,
        CancellationToken cancellationToken = default)
    {
        await _localAiSetupGate.WaitAsync(cancellationToken);
        CancellationTokenSource? setupCancellation = null;
        var priorButtonEnabled = SetupLocalAiButton.IsEnabled;
        var priorModeEnabled = UseLocalAiCheckbox.IsEnabled;
        try
        {
            setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _localAiSetupCancellation = setupCancellation;
            var setupToken = setupCancellation.Token;
            SetupLocalAiButton.IsEnabled = false;
            UseLocalAiCheckbox.IsEnabled = false;
            CancelLocalAiSetupButton.Visibility = Visibility.Visible;
            CancelLocalAiSetupButton.IsEnabled = true;
            LocalAiProgressBar.Visibility = Visibility.Visible;
            LocalAiProgressBar.IsIndeterminate = true;
            LocalAiProgressBar.Value = 0;

            // Text chat and task planning now run in Layal's own native core.
            // Only optional microphone/speaker resources are checked or downloaded here.
            _localVoiceReady = await _localVoiceService.ValidateCachedResourcesAsync(setupToken);
            var needsVoice = includeVoice && !_localVoiceReady && (!_localVoiceSetupAttempted || retryVoice);
            if (!needsVoice)
            {
                UpdateLocalAiStatusText();
                RefreshProviderStatus();
                return true;
            }

            var consent = LayalDialogService.Show(
                this,
                IsArabic
                    ? "المحادثة النصية وتنفيذ المهام يعملان بالفعل بنواة ليال المحلية ولا يحتاجان إلى Ollama أو Qwen أو مفتاح API. هذا التنزيل اختياري للصوت فقط: Whisper لتحويل الكلام إلى نص وPiper لنطق الردود. قد يحتاج تنزيل موارد الصوت إلى إنترنت ومساحة إضافية. لن تُستخدم هذه النماذج للتفكير أو اتخاذ القرارات أو تنفيذ المهام. هل توافق على تجهيز الصوت المحلي؟"
                    : "Text chat and task execution already run in Layal's native local core and do not need Ollama, Qwen, or an API key. This optional download is for speech only: Whisper for speech-to-text and Piper for speech output. It needs internet and additional disk space. These models will not be used for reasoning, decisions, or task execution. Approve local speech setup?",
                IsArabic ? "موافقة على تجهيز الصوت المحلي فقط" : "Approve local speech-only setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No);
            if (consent != MessageBoxResult.Yes)
            {
                LocalAiStatusText.Text = IsArabic
                    ? "تم إلغاء تجهيز الصوت؛ نواة المحادثة والمهام تظل جاهزة ولا تحتاج تنزيلًا."
                    : "Speech setup was cancelled; the conversation and task core remain ready and need no downloads.";
                return true;
            }

            var progress = new Progress<LocalSpeechProgress>(state =>
            {
                LocalAiStatusText.Text = state.Message;
                if (state.Percent.HasValue)
                {
                    LocalAiProgressBar.IsIndeterminate = false;
                    LocalAiProgressBar.Value = Math.Clamp(state.Percent.Value, 0, 100);
                }
                else
                {
                    LocalAiProgressBar.IsIndeterminate = true;
                }
            });

            _localVoiceSetupAttempted = true;
            try
            {
                await _localVoiceService.EnsureReadyAsync(progress, setupToken);
                _localVoiceReady = _localVoiceService.IsReady;
            }
            catch (OperationCanceledException) when (setupCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception voiceError)
            {
                _localVoiceReady = false;
                LocalAiStatusText.Text = (IsArabic
                    ? "نواة النص والمهام جاهزة، لكن تجهيز الصوت لم يكتمل. التفاصيل: "
                    : "The native text and task core is ready, but speech setup did not complete. Details: ") + voiceError.Message;
            }

            UpdateLocalAiStatusText();
            RefreshProviderStatus();
            return true;
        }
        catch (OperationCanceledException) when (setupCancellation?.IsCancellationRequested == true)
        {
            LocalAiStatusText.Text = IsArabic
                ? "تم إيقاف تجهيز الصوت المحلي. نواة النص والمهام لم تتأثر."
                : "Local speech setup cancelled. The text and task core is unaffected.";
            return false;
        }
        catch (Exception ex)
        {
            LocalAiStatusText.Text = (IsArabic ? "تعذر تجهيز الصوت المحلي: " : "Could not set up local speech: ") + ex.Message;
            SetAccessibleStatusText(ProviderActionStatus, LocalAiStatusText.Text);
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
            return false;
        }
        finally
        {
            LocalAiProgressBar.Visibility = Visibility.Collapsed;
            LocalAiProgressBar.IsIndeterminate = false;
            CancelLocalAiSetupButton.Visibility = Visibility.Collapsed;
            CancelLocalAiSetupButton.IsEnabled = true;
            SetupLocalAiButton.IsEnabled = priorButtonEnabled;
            UseLocalAiCheckbox.IsEnabled = priorModeEnabled;
            if (ReferenceEquals(_localAiSetupCancellation, setupCancellation))
                _localAiSetupCancellation = null;
            setupCancellation?.Dispose();
            _localAiSetupGate.Release();
            if (IsLoaded)
            {
                UpdateLocalAiStatusText();
                RefreshProviderStatus();
            }
        }
    }

    private void RefreshProviderStatus()
    {
        UpdateLocalAiStatusText();
        ConnectionStatusText.Text = IsArabic
            ? "النواة المعرفية المستقلة جاهزة"
            : "Native cognitive core ready";
        ConnectionStatusText.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        CardAiValue.Text = IsArabic ? "نواة ليال جاهزة" : "Layal core ready";
        CardAiValue.Foreground = ConnectionStatusText.Foreground;
        CardAiHint.Text = _localVoiceReady
            ? (IsArabic
                ? "النص والمهام محليان؛ موارد الصوت المحلية جاهزة اختياريًا."
                : "Text and tasks are native/local; optional local speech resources are ready.")
            : (IsArabic
                ? "النص والمهام لا يحتاجان نماذج. يمكن تجهيز الصوت وحده من الإعدادات."
                : "Text and tasks need no models. Speech can be set up separately in Settings.");
        ChatProviderStatus.Text = IsArabic
            ? "الردود النصية والمهام تعمل من نواة ليال دون مزود AI."
            : "Text replies and tasks run through Layal's core without an AI provider.";
        if (IsLoaded)
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

}
