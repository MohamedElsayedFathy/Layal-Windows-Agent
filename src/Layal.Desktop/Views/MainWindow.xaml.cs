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
using Layal.SpeechLanguage;
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
    private GameModeHudWindow? _gameModeHudWindow;
    private bool _gameModeHudAutoLaunched;
    private string? _gameModeAutoLaunchedProcessName;
    private string? _lastAllowedForegroundProcessName;
    private bool _automaticGameModeSessionActive;
    private readonly GameModeAutoDismissalPolicy _gameModeAutoDismissalPolicy = new();
    private readonly ForegroundGameDetectionService _foregroundGameDetectionService = new();
    private readonly DispatcherTimer _gameModeDetectionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _weatherRefreshTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _proactiveCheckInTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly WeatherService _weatherService = new();
    private readonly DeviceLocationService _deviceLocationService = new();
    private CancellationTokenSource? _weatherCancellation;
    private bool _weatherRequestInProgress;
    private CurrentWeatherReport? _currentWeatherReport;

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

    private readonly ScreenRegionCaptureService _screenRegionCaptureService = new();
    private CancellationTokenSource? _screenCaptureCancellation;
    private CancellationTokenSource? _clipboardReadCancellation;
    private CancellationTokenSource? _hoveredElementInspectionCancellation;
    private readonly DocumentTextExtractionService _documentTextExtractionService = new();
    private CancellationTokenSource? _documentExtractionCancellation;
    private string? _pendingDocumentText;
    private string? _pendingDocumentFileName;
    private string? _pendingDocumentNotice;
    private readonly MicrophoneCaptureService _microphoneCapture = new();
    private readonly LocalVoiceService _localVoiceService = new();
    private readonly SpeechLanguageEngine _speechLanguageEngine = SpeechLanguageEngine.CreateDefault();
    private readonly LocalSpeechLanguageProvider _localSpeechLanguageProvider = new(_localVoiceService);
    private readonly SpeechLanguageRuntime _speechLanguageRuntime = new(
        _speechLanguageEngine, _localSpeechLanguageProvider, _localSpeechLanguageProvider);
    // Used only when the user explicitly selects Test external API; never used by chat or task planning.
    private readonly OpenAiCompatibleClient _providerConnectionTester = new();
    private bool _localVoiceReady;
    private bool _localVoiceSetupAttempted;
    private bool _localVoiceRecordingActive;
    private readonly SemaphoreSlim _localAiSetupGate = new(1, 1);
    private CancellationTokenSource? _localAiSetupCancellation;
    private readonly DispatcherTimer _speechRecordingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _speechCancellation;
    private bool _discardSpeechAudioOnStop;
    private CancellationTokenSource? _voiceTestCancellation;
    private CancellationTokenSource? _voiceConversationCancellation;
    private bool _refreshingAudioDeviceSelectors;
    private DateTimeOffset _speechRecordingStartedAt;
    private bool _speechIsStopping;
    private CancellationTokenSource? _chatCancellation;
    private VisionCameraWindow? _cameraVisionWindow;
    private byte[]? _pendingVisionImageBytes;
    private string? _pendingVisionContentType;
    private string? _pendingVisionFileName;
    private CancellationTokenSource? _agentTaskCancellation;
    private string? _selectedMemoryId;
    private bool _selectedMemoryIsArchived;
    private DateTimeOffset? _selectedMemoryUpdatedAt;
    private bool _refreshingMemoryCollection;
    private bool _refreshingMemoryCategoryFilter;
    private string _memoryEditorBaselineTitle = string.Empty;
    private string _memoryEditorBaselineContent = string.Empty;
    private string _memoryEditorBaselineCategory = "General";
    private const int MaxMemoryBackupBytes = 2 * 1024 * 1024;
    private string _currentConversationId = string.Empty;
    private bool _refreshingChatSessions;
    private ICollectionView? _apiConnectionsView;
    private string? _editingApiConnectionName;
    private GlobalHotkeyService? _globalHotkeyService;
    private QuickAskWindow? _quickAskWindow;
    private bool _incognitoMode;
    private bool _restoringProactiveCheckInInterval;
    private bool _reminderTimerWasEnabledBeforePrivateChat;
    private bool _proactiveTimerWasEnabledBeforePrivateChat;
    private bool _isBottomDockExpanded = true;
    private int? _chatWelcomeLayoutModeApplied;
    private bool? _compactWindowChromeApplied;
    private bool _isChatHistoryPanelOpen;
    private bool _isUiInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _isUiInitialized = true;
        AppRuntime.OpenTaskNotification = OpenTaskFromNotification;
        if (!Layal.App.SuppressStartupForTests)
            _globalHotkeyService = new GlobalHotkeyService(this, OpenQuickAskWindow, AppRuntime.Settings.QuickAskHotkeyPreset);
        SourceInitialized += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(UpdateQuickAskHotkeyStatus));
            ApplyWindowBackdrop();
        };
        // The chat is Layal's primary surface; Home remains available from the navigation rail.
        // Clock/date/weather live in the chat header so core context is visible immediately.
        MainTabs.SelectedItem = ChatTab;
        SystemEvents.UserPreferenceChanged += MainWindow_UserPreferenceChanged;
        Closed += MainWindow_Closed;
        _speechRecordingTimer.Tick += SpeechRecordingTimer_Tick;
        _clockTimer.Tick += ClockTimer_Tick;
        _weatherRefreshTimer.Tick += WeatherRefreshTimer_Tick;
        _reminderTimer.Tick += ReminderTimer_Tick;
        _proactiveCheckInTimer.Tick += ProactiveCheckInTimer_Tick;
        _gameModeDetectionTimer.Tick += GameModeDetectionTimer_Tick;
        DataContext = this;
        _apiConnectionsView = CollectionViewSource.GetDefaultView(ApiConnections);
        _apiConnectionsView.Filter = ApiConnectionMatchesSearch;
        ApiConnectionList.ItemsSource = _apiConnectionsView;
        AppRuntime.Settings.UseLocalAi = true;
        UseLocalAiCheckbox.IsChecked = true;
        UseLocalAiCheckbox.IsEnabled = false;
        BaseUrlInput.Text = AppRuntime.Settings.ProviderBaseUrl;
        WeatherLocationInput.Text = AppRuntime.Settings.WeatherLocation ?? string.Empty;
        ModelInput.Text = AppRuntime.Settings.ModelId;
        VoiceReplyCheckbox.IsChecked = AppRuntime.Settings.VoiceRepliesEnabled;
        // SettingsStore normalizes only invalid/empty IDs. Preserve every valid voice explicitly selected by the user.
        AssistantPersonaSelector.SelectedItem = AssistantPersonaSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), AppRuntime.Settings.AssistantPersona, StringComparison.Ordinal));
        VoiceDeliveryStyleSelector.SelectedValue = VoiceDeliveryStylePolicy.Normalize(AppRuntime.Settings.VoiceDeliveryStyleId);
        CharacterDisplayModeSelector.SelectedValue = CharacterDisplayModePolicy.Normalize(AppRuntime.Settings.MascotCharacterDisplayMode);
        AlwaysOnTopCheckbox.IsChecked = AppRuntime.Settings.MascotAlwaysOnTop;
        AnimationsCheckbox.IsChecked = AppRuntime.Settings.MascotAnimationsEnabled;
        PerformanceModeCheckbox.IsChecked = AppRuntime.Settings.PerformanceModeEnabled;
        MascotSizeSlider.Value = AppRuntime.Settings.MascotScale;
        MascotOpacitySlider.Value = AppRuntime.Settings.MascotOpacity;
        WindowInspectionCheckbox.IsChecked = AppRuntime.Settings.AllowWindowTitleInspection;
        ThemePresetSelector.SelectedItem = ThemePresetSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), AppRuntime.Settings.ThemePreset, StringComparison.Ordinal));
        QuickAskHotkeySelector.SelectedItem = QuickAskHotkeySelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), AppRuntime.Settings.QuickAskHotkeyPreset, StringComparison.Ordinal));
        ProactiveCheckInEnabledCheckBox.IsChecked = AppRuntime.Settings.ProactiveCheckInEnabled;
        ProactiveCheckInIntervalSelector.SelectedItem =
            ProactiveCheckInIntervalSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), AppRuntime.Settings.ProactiveCheckInIntervalMinutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var currentConversation = AppRuntime.ChatSessionService.GetCurrent();
        _currentConversationId = currentConversation.Id;
        foreach (var message in currentConversation.Messages.TakeLast(ChatSessionService.MaxMessagesPerConversation)) ChatMessages.Add(message);
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
        StateChanged += MainWindow_StateChanged;
        ApplyTheme();
        RefreshAudioDeviceSelectors();
        ApplyLanguage();
        RefreshProviderStatus();
        if (!Layal.App.SuppressStartupForTests && AppRuntime.ReminderService is not null)
            _reminderTimer.Start();
        if (!Layal.App.SuppressStartupForTests && AppRuntime.Settings.ProactiveCheckInEnabled)
        {
            if (AppRuntime.Settings.LastProactiveCheckInAt is null)
            {
                AppRuntime.Settings.LastProactiveCheckInAt = DateTimeOffset.Now;
                AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            }
            _proactiveCheckInTimer.Start();
        }
        if (!Layal.App.SuppressStartupForTests &&
            AppRuntime.Settings.GameModeAutoDetectionEnabled &&
            AppRuntime.Settings.GameModeAllowedProcessNames.Count > 0)
        {
            _gameModeDetectionTimer.Start();
            // Apply the game-session suppression gate immediately at startup; do not wait
            // for the first three-second timer tick when a game is already in the foreground.
            GameModeDetectionTimer_Tick(this, EventArgs.Empty);
        }
    }

}
