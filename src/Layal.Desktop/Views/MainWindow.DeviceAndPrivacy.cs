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
    private void RefreshAudioDeviceSelectors()
    {
        if (AudioInputDeviceSelector is null || AudioOutputDeviceSelector is null)
            return;

        _refreshingAudioDeviceSelectors = true;
        try
        {
            var inputDevices = AudioDeviceService.GetInputDevices(IsArabic).ToList();
            var outputDevices = AudioDeviceService.GetOutputDevices(IsArabic).ToList();
            var inputId = string.IsNullOrWhiteSpace(AppRuntime.Settings.AudioInputDeviceId)
                ? AudioDeviceService.WindowsDefaultDeviceId
                : AppRuntime.Settings.AudioInputDeviceId;
            var outputId = string.IsNullOrWhiteSpace(AppRuntime.Settings.AudioOutputDeviceId)
                ? AudioDeviceService.WindowsDefaultDeviceId
                : AppRuntime.Settings.AudioOutputDeviceId;

            if (!inputDevices.Any(device => string.Equals(device.Id, inputId, StringComparison.Ordinal)))
            {
                if (!string.Equals(inputId, AudioDeviceService.WindowsDefaultDeviceId, StringComparison.Ordinal))
                    inputDevices.Add(AudioDeviceService.CreateUnavailableSelection(inputId, isInput: true, isArabic: IsArabic));
                else
                    inputId = AudioDeviceService.WindowsDefaultDeviceId;
            }
            if (!outputDevices.Any(device => string.Equals(device.Id, outputId, StringComparison.Ordinal)))
            {
                if (!string.Equals(outputId, AudioDeviceService.WindowsDefaultDeviceId, StringComparison.Ordinal))
                    outputDevices.Add(AudioDeviceService.CreateUnavailableSelection(outputId, isInput: false, isArabic: IsArabic));
                else
                    outputId = AudioDeviceService.WindowsDefaultDeviceId;
            }

            AudioInputDeviceSelector.ItemsSource = inputDevices;
            AudioOutputDeviceSelector.ItemsSource = outputDevices;
            AudioInputDeviceSelector.SelectedValue = inputId;
            AudioOutputDeviceSelector.SelectedValue = outputId;

            var unavailableInput = inputDevices.FirstOrDefault(device => device.Id == inputId && !device.IsAvailable);
            var unavailableOutput = outputDevices.FirstOrDefault(device => device.Id == outputId && !device.IsAvailable);
            var unavailable = unavailableInput is not null || unavailableOutput is not null;
            AudioDeviceStatus.Text = unavailable
                ? (IsArabic
                    ? "الجهاز المحفوظ غير متصل. وصّل الجهاز أو اختر جهازًا متاحًا؛ لن نبدّل الميكروفون أو السماعة بصمت."
                    : "A saved audio device is disconnected. Reconnect it or choose an available device; Layal will not silently switch endpoints.")
                : (IsArabic
                    ? "تُحفظ اختيارات الصوت لهذا المستخدم. اختر «افتراضي Windows» للعودة إلى الجهاز الافتراضي."
                    : "Audio selections are saved for this Windows user. Choose Windows default to restore automatic routing.");
            AudioDeviceStatus.Foreground = ThemeBrush(
                unavailable ? "Layal.Warning" : "Layal.Muted",
                unavailable ? System.Windows.Media.Brushes.DarkGoldenrod : System.Windows.Media.Brushes.Gray);
        }
        finally
        {
            _refreshingAudioDeviceSelectors = false;
        }
    }

    private void AudioInputDeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _refreshingAudioDeviceSelectors || AudioInputDeviceSelector.SelectedValue is not string selectedId)
            return;
        if (_microphoneCapture.IsRecording || _localVoiceRecordingActive ||
            _voiceConversationCancellation is not null || _speechIsStopping)
        {
            RefreshAudioDeviceSelectors();
            SetChatActivityStatus(IsArabic
                ? "أوقف استخدام الميكروفون قبل تغيير جهاز الإدخال."
                : "Stop microphone use before changing the input device.");
            return;
        }
        try
        {
            _ = AudioDeviceService.ResolveInputDeviceNumber(selectedId);
            AppRuntime.Settings.AudioInputDeviceId = selectedId;
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            RefreshAudioDeviceSelectors();
        }
        catch (Exception ex)
        {
            RefreshAudioDeviceSelectors();
            SetChatActivityStatus((IsArabic ? "تعذر اختيار الميكروفون: " : "Could not select microphone: ") + ex.Message);
        }
    }

    private void AudioOutputDeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _refreshingAudioDeviceSelectors || AudioOutputDeviceSelector.SelectedValue is not string selectedId)
            return;
        if (_voiceConversationCancellation is not null || _chatCancellation is not null || _voiceTestCancellation is not null)
        {
            RefreshAudioDeviceSelectors();
            SetChatActivityStatus(IsArabic
                ? "أوقف تشغيل الصوت الحالي قبل تغيير مخرج الصوت."
                : "Stop current audio playback before changing the output device.");
            return;
        }
        try
        {
            _ = AudioDeviceService.ResolveOutputDeviceNumber(selectedId);
            AppRuntime.Settings.AudioOutputDeviceId = selectedId;
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            RefreshAudioDeviceSelectors();
        }
        catch (Exception ex)
        {
            RefreshAudioDeviceSelectors();
            SetChatActivityStatus((IsArabic ? "تعذر اختيار مخرج الصوت: " : "Could not select audio output: ") + ex.Message);
        }
    }

    private void RefreshAudioDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAudioDeviceSelectors();
        SetChatActivityStatus(IsArabic ? "تم تحديث قائمة أجهزة الصوت." : "Audio device lists refreshed.");
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void QuickAskButton_Click(object sender, RoutedEventArgs e) => OpenQuickAskWindow();

    private void GmailInboxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_incognitoMode)
        {
            SetChatActivityStatus(IsArabic
                ? "تكامل Gmail غير متاح أثناء وضع Incognito. اخرج من الوضع الخاص أولًا."
                : "Gmail integration is unavailable while Incognito is active. Exit private mode first.");
            return;
        }

        try
        {
            var window = new GmailInboxWindow(
                AppRuntime.GmailReadOnlyService,
                AppRuntime.Settings.GmailOAuthClientId,
                IsArabic,
                clientId =>
                {
                    AppRuntime.Settings.GmailOAuthClientId = clientId;
                    AppRuntime.SettingsStore.Save(AppRuntime.Settings);
                },
                emailText =>
                {
                    MainTabs.SelectedItem = ChatTab;
                    ChatInput.Text = string.IsNullOrWhiteSpace(ChatInput.Text)
                        ? emailText
                        : ChatInput.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + emailText;
                    ChatInput.CaretIndex = ChatInput.Text.Length;
                    SetChatActivityStatus(IsArabic
                        ? "تمت إضافة البريد إلى مسودة الشات فقط؛ لم يُرسل بعد."
                        : "Email was added to the chat draft only; it has not been sent.");
                    ChatInput.Focus();
                })
            {
                Owner = this
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر فتح بريد Gmail", "Could not open Gmail inbox", ex, IsArabic));
        }
    }

    private void IncognitoModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_incognitoMode)
        {
            if (_chatCancellation is not null || _agentTaskCancellation is not null ||
                _voiceConversationCancellation is not null || _speechCancellation is not null ||
                _voiceTestCancellation is not null || _localAiSetupCancellation is not null ||
                _documentExtractionCancellation is not null || _clipboardReadCancellation is not null ||
                _screenCaptureCancellation is not null || _hoveredElementInspectionCancellation is not null ||
                _localVoiceRecordingActive || _microphoneCapture.IsRecording || _speechIsStopping ||
                _cameraVisionWindow is { IsVisible: true } || _quickAskWindow is { IsVisible: true })
            {
                SetChatActivityStatus(IsArabic
                    ? "أوقف العمليات النشطة وأغلق نافذة السؤال السريع والكاميرا قبل بدء المحادثة الخاصة."
                    : "Stop active operations and close Quick Ask and the camera before starting a private chat.");
                return;
            }

            var confirm = LayalDialogService.Show(this,
                IsArabic
                    ? "سيبدأ سياق دردشة مؤقت فارغ ولن تُحفظ رسائله الجديدة في سجل المحادثات المحلي. أثناء الوضع الخاص سيتم تعطيل تبويبي المهام والذاكرة ومنع بدء مهام وكيل جديدة أو الوصول للذاكرة الشخصية؛ البيانات المحفوظة سابقًا لن تُحذف وستعود بعد الخروج. قد تظل الرسائل التي ترسلها تصل إلى مزود الذكاء الاصطناعي وتخضع لسجلاته وسياسة احتفاظه. هل تريد المتابعة؟"
                    : "This starts a blank temporary chat and does not save its new messages in local chat history. While private mode is active, Tasks and Memory are disabled; new agent tasks and personal-memory access are blocked. Previously saved tasks and notes are preserved and become available again after exit. Messages may still be sent to the configured AI provider and remain subject to its own logging and retention policies. Continue?",
                IsArabic ? "بدء محادثة خاصة" : "Start private chat",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
                return;

            EnterPrivateChatMode();
            return;
        }

        if (_chatCancellation is not null || _agentTaskCancellation is not null ||
            _voiceConversationCancellation is not null || _speechCancellation is not null || _voiceTestCancellation is not null ||
            _localAiSetupCancellation is not null || _documentExtractionCancellation is not null ||
            _clipboardReadCancellation is not null || _screenCaptureCancellation is not null ||
            _hoveredElementInspectionCancellation is not null || _localVoiceRecordingActive ||
            _microphoneCapture.IsRecording || _speechIsStopping ||
            _cameraVisionWindow is { IsVisible: true } || _quickAskWindow is { IsVisible: true })
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف المحادثة والمهمة والتسجيلات النشطة وأغلق نافذة السؤال السريع أو الكاميرا قبل إنهاء المحادثة الخاصة."
                : "Stop active chat, agent-task, and audio work and close Quick Ask or the camera before exiting private chat.");
            return;
        }

        var exit = LayalDialogService.Show(this,
            IsArabic
                ? "سيتم التخلص من رسائل المحادثة الخاصة غير المحفوظة واستعادة آخر محادثة محفوظة. المهام والملاحظات المحفوظة لم تُحذف وستصبح متاحة مجددًا. لا يمكن استرجاع ما أُرسل بالفعل إلى مزود خارجي. هل تريد المتابعة؟"
                : "Unsaved private-chat messages will be discarded and the last saved conversation will be restored. Saved tasks and memory were preserved and will become available again. Data already sent to an external provider cannot be recalled. Continue?",
            IsArabic ? "إنهاء المحادثة الخاصة" : "Exit private chat",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (exit == MessageBoxResult.Yes)
            ExitPrivateChatMode();
    }

    private void EnterPrivateChatMode()
    {
        AppRuntime.ChatSessionService.BeginTemporarySession();
        _clipboardReadCancellation?.Cancel();
        _incognitoMode = true;
        AppRuntime.IsPrivateChatActive = true;
        _reminderTimerWasEnabledBeforePrivateChat = _reminderTimer.IsEnabled;
        _proactiveTimerWasEnabledBeforePrivateChat = _proactiveCheckInTimer.IsEnabled;
        _reminderTimer.Stop();
        _proactiveCheckInTimer.Stop();
        TrySynchronizeLocalNotificationSchedule();
        MainTabs.SelectedItem = ChatTab;

        // Clear personal/task content from visible controls for this temporary session.
        // DPAPI-backed task history and memory stores are deliberately preserved.
        MemoryNotes.Clear();
        MemoryList.SelectedItem = null;
        MemoryCategoryFilter.Items.Clear();
        MemoryCountText.Text = string.Empty;
        MemoryFilterNotice.Text = string.Empty;
        _selectedMemoryId = null;
        _selectedMemoryIsArchived = false;
        _selectedMemoryUpdatedAt = null;
        MemorySearchInput.Clear();
        MemoryCategoryInput.Text = "General";
        MemoryTitleInput.Clear();
        MemoryContentInput.Clear();
        SetMemoryEditorBaseline(null);
        UpdateMemoryEditorButtons();
        RecentTasks.Clear();
        TaskEvents.Clear();
        _rawTaskEvents.Clear();
        TaskInput.Clear();
        TaskResult.Text = string.Empty;
        TaskProgressStatus.Text = IsArabic ? "المهام غير متاحة أثناء المحادثة الخاصة." : "Tasks are unavailable during private chat.";
        TaskProgressBar.Value = 0;
        TaskEventsEmptyStateText.Text = string.Empty;
        RecentTasksEmptyStateText.Text = string.Empty;
        TaskCopyStatus.Text = string.Empty;

        MemoryResult.Text = string.Empty;
        MemoryEmptyStateText.Text = IsArabic ? "الذاكرة الشخصية غير متاحة أثناء المحادثة الخاصة." : "Personal memory is unavailable during private chat.";

        ChatMessages.Clear();
        ChatInput.Clear();
        ClearPendingChatAttachmentsForPrivateMode();
        ChatConversations.Clear();
        ChatHistorySelector.SelectedItem = null;
        UpdateVisionAttachmentControls();
        UpdateDocumentAttachmentUi();
        UpdatePrivateChatModeUi();
        SetChatActivityStatus(IsArabic
            ? "المحادثة الخاصة نشطة: الرسائل لا تُحفظ محليًا، والمهام وسجلها والذاكرة والروتينات والتذكيرات المرتبطة بالمهام ومساحة العمل والإشعارات متوقفة مؤقتًا. قد يستقبل مزود AI ما ترسله يدويًا."
            : "Private chat is active: messages are not saved locally. Tasks, task history, memory, saved routines, task-linked reminders, the workspace hub, and local notifications are paused. Your explicit messages may still reach the AI provider.");
    }

    private void ExitPrivateChatMode()
    {
        ClearPendingChatAttachmentsForPrivateMode();
        AppRuntime.ChatSessionService.EndTemporarySession();
        _incognitoMode = false;
        AppRuntime.IsPrivateChatActive = false;
        if (_reminderTimerWasEnabledBeforePrivateChat)
            _reminderTimer.Start();
        else
            _reminderTimer.Stop();
        if (_proactiveTimerWasEnabledBeforePrivateChat && AppRuntime.Settings.ProactiveCheckInEnabled)
            _proactiveCheckInTimer.Start();
        else
            _proactiveCheckInTimer.Stop();
        _reminderTimerWasEnabledBeforePrivateChat = false;
        _proactiveTimerWasEnabledBeforePrivateChat = false;
        TrySynchronizeLocalNotificationSchedule();
        UpdateVisionAttachmentControls();
        UpdateDocumentAttachmentUi();

        var conversation = AppRuntime.ChatSessionService.GetCurrent();
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
        ChatInput.Clear();
        ChatMessagesList.Items.Refresh();
        AppRuntime.ChatSessionService.SetDefaultTitle(IsArabic ? "محادثة جديدة" : "New conversation");
        RefreshChatSessions(_currentConversationId);
        RefreshRecentTasks();
        RefreshMemoryNotes();
        UpdatePrivateChatModeUi();
        RefreshProviderStatus();
        SetChatActivityStatus(IsArabic
            ? "تم التخلص من رسائل المحادثة الخاصة واستعادة المحادثة المحفوظة."
            : "Private-chat messages were discarded and the saved conversation was restored.");
    }

    private void UpdatePrivacyShieldUi()
    {
        var ar = IsArabic;
        PrivacyShieldButton.Content = AppRuntime.Settings.PrivacyShieldEnabled
            ? (ar ? "درع الخصوصية: مفعّل" : "Privacy shield: ON")
            : (ar ? "درع الخصوصية: مغلق" : "Privacy shield: OFF");
        PrivacyShieldButton.ToolTip = AppRuntime.Settings.PrivacyShieldEnabled
            ? (ar
                ? "درع الخصوصية نشط: لا يمكن بدء الميكروفون أو الكاميرا أو قص الشاشة أو فحص العنصر أو قراءة الحافظة."
                : "Privacy shield is active: microphone, camera, screen cropping, UI element inspection, and clipboard reads cannot be started.")
            : (ar
                ? "تفعيل مفتاح واحد يمنع بدء الميكروفون والكاميرا وقص الشاشة وفحص العناصر وقراءة الحافظة."
                : "One switch blocks starting the microphone, camera, screen cropping, UI element inspection, and clipboard reads.");
        AutomationProperties.SetName(PrivacyShieldButton, ar ? "مفتاح خصوصية شامل" : "Master privacy shield");
        AutomationProperties.SetHelpText(PrivacyShieldButton, ar
            ? "عند التفعيل يمنع بدء التسجيل والكاميرا وقص الشاشة وفحص العناصر وقراءة الحافظة، ويحاول إيقاف الالتقاطات الجارية. تظل الدردشة النصية متاحة. لا يمكن التراجع عن بيانات أُرسلت بالفعل."
            : "When enabled, blocks new recording, camera, screen crop, UI element inspection, and clipboard-read operations, and requests stopping active captures. Text chat remains available. Data already sent cannot be recalled.");
        var shieldEnabled = AppRuntime.Settings.PrivacyShieldEnabled;
        InspectHoveredElementButton.IsEnabled = !shieldEnabled;
        CaptureScreenRegionButton.IsEnabled = !shieldEnabled && _chatCancellation is null;
        UpdateClipboardReadButtons();
        CameraVisionButton.IsEnabled = !shieldEnabled;
        SpeechToTextButton.IsEnabled = !_speechIsStopping &&
            (!shieldEnabled || _microphoneCapture.IsRecording || _speechCancellation is not null);
        VoiceConversationButton.IsEnabled = !shieldEnabled ||
            _voiceConversationCancellation is not null || _localVoiceRecordingActive;
    }

    private void PrivacyShieldButton_Click(object sender, RoutedEventArgs e)
    {
        var enable = !AppRuntime.Settings.PrivacyShieldEnabled;
        var confirm = LayalDialogService.Show(this,
            enable
                ? (IsArabic
                    ? "سيمنع الدرع بدء تسجيل الميكروفون وفتح الكاميرا وقص الشاشة وفحص العناصر وقراءة الحافظة. سيحاول إيقاف الالتقاطات الجارية وإغلاق الكاميرا وإلغاء أي طلب نشط؛ وقد يحتاج إيقاف بعض موارد النظام لحظات. لن تُحذف بيانات أُرسلت إلى مزود أو حُفظت سابقًا. هل تريد التفعيل؟"
                    : "The shield will block microphone recording, camera opening, screen cropping, UI element inspection, and clipboard reads. It will request stopping active captures, close the camera, and cancel active requests; some system resources may take a moment to stop. Previously sent or saved data cannot be deleted by this switch. Enable it?")
                : (IsArabic
                    ? "سيُسمح مجددًا ببدء الميكروفون والكاميرا وقص الشاشة وفحص العناصر وقراءة الحافظة، مع بقاء الموافقة المطلوبة لكل عملية. هل تريد تعطيل الدرع؟"
                    : "Starting the microphone, camera, screen crop, UI element inspection, and clipboard reads will become available again, with the individual consent prompts still required. Disable the shield?"),
            enable
                ? (IsArabic ? "تفعيل درع الخصوصية" : "Enable privacy shield")
                : (IsArabic ? "تعطيل درع الخصوصية" : "Disable privacy shield"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        AppRuntime.Settings.PrivacyShieldEnabled = enable;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.PrivacyShieldEnabled = !enable;
            UpdatePrivacyShieldUi();
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر حفظ إعداد درع الخصوصية", "Could not save the privacy shield setting", ex, IsArabic));
            return;
        }

        if (enable)
            StopCaptureSourcesForPrivacyShield();

        // Re-evaluate immediately: an automatic HUD must not remain over a game after
        // the privacy shield is enabled. A manually opened HUD remains user-controlled.
        GameModeDetectionTimer_Tick(this, EventArgs.Empty);
        TrySynchronizeLocalNotificationSchedule();
        UpdatePrivacyShieldUi();
        SetChatActivityStatus(enable
            ? (IsArabic
                ? "درع الخصوصية مفعّل. التسجيل والكاميرا وقص الشاشة وقراءة الحافظة محظورة؛ الدردشة النصية ما زالت متاحة."
                : "Privacy shield enabled. Recording, camera, screen cropping, and clipboard reads are blocked; text chat remains available.")
            : (IsArabic
                ? "درع الخصوصية مغلق. تظل موافقة منفصلة مطلوبة قبل بدء أي التقاط."
                : "Privacy shield disabled. Separate consent is still required before any capture."));
    }

    private bool IsCaptureBlockedByPrivacyShield() =>
        !PrivacyShieldPolicy.CanStartCapture(
            AppRuntime.Settings.PrivacyShieldEnabled,
            AppRuntime.IsExiting);

    private void StopCaptureSourcesForPrivacyShield()
    {
        // A privacy-shield change must also close an in-flight local screen preview.
        // The capture service observes this token and never returns a crop for attachment.
        _screenCaptureCancellation?.Cancel();
        _clipboardReadCancellation?.Cancel();
        // Abort both the five-second pointer-targeting interval and any pending UIA result.
        _hoveredElementInspectionCancellation?.Cancel();
        _documentExtractionCancellation?.Cancel();
        _speechRecordingTimer.Stop();
        // StopRecordingAsync has no cancellation token. Mark its in-flight finalization so
        // the resulting audio is discarded before any local transcription or cloud upload.
        if (_speechIsStopping && _speechCancellation is null)
            _discardSpeechAudioOnStop = true;

        if (_localVoiceRecordingActive)
        {
            CancelLocalVoiceRecording();
        }
        else if (_microphoneCapture.IsRecording)
        {
            try { _microphoneCapture.CancelRecording(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Privacy shield could not cancel microphone capture: {0}", ex.Message); }
            UpdateSpeechToTextButton();
        }

        _speechCancellation?.Cancel();
        _voiceTestCancellation?.Cancel();
        _voiceConversationCancellation?.Cancel();
        // Also cancel a still-image analysis request that may be processing a captured camera frame.
        _chatCancellation?.Cancel();
        // Agent tasks may be between model turns and about to invoke UI/screen/clipboard
        // inspection tools. Cancel the entire task before another tool call can begin.
        if (PrivacyShieldPolicy.ShouldCancelAgentTask(AppRuntime.Settings.PrivacyShieldEnabled))
            _agentTaskCancellation?.Cancel();

        if (_cameraVisionWindow is { } camera)
        {
            try { camera.StopForPrivacyShield(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Privacy shield could not close the camera window: {0}", ex.Message); }
            _cameraVisionWindow = null;
        }

        var visionAttachmentIsCapture = _pendingVisionFileName?.StartsWith(
            "screen-region-", StringComparison.OrdinalIgnoreCase) == true;
        var visionAttachmentIsClipboard = _pendingVisionFileName?.StartsWith(
            "clipboard-image-", StringComparison.OrdinalIgnoreCase) == true;
        if ((visionAttachmentIsCapture || visionAttachmentIsClipboard) &&
            _pendingVisionImageBytes is { } pendingImageBytes)
        {
            PrivacyShieldPolicy.ClearCapturedBuffer(pendingImageBytes);
            _pendingVisionImageBytes = null;
            _pendingVisionContentType = null;
            _pendingVisionFileName = null;
            UpdateVisionAttachmentControls();
        }

        var documentAttachmentIsClipboard =
            string.Equals(_pendingDocumentFileName, "Clipboard text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_pendingDocumentFileName, "نص الحافظة", StringComparison.Ordinal);
        if (documentAttachmentIsClipboard && _pendingDocumentText is not null)
        {
            // Strings are immutable; dropping all UI/service references is the strongest
            // cleanup available for already-created clipboard text in managed memory.
            _pendingDocumentText = null;
            _pendingDocumentFileName = null;
            _pendingDocumentNotice = null;
            UpdateDocumentAttachmentUi();
        }

        UpdateVoiceConversationControls();
        UpdateCameraVisionIndicator();
    }

    private void ClearPendingChatAttachmentsForPrivateMode()
    {
        if (_pendingVisionImageBytes is { Length: > 0 } imageBytes)
            Array.Clear(imageBytes, 0, imageBytes.Length);

        _pendingVisionImageBytes = null;
        _pendingVisionContentType = null;
        _pendingVisionFileName = null;
        // .NET strings are immutable; dropping all application references is the available
        // cleanup mechanism for text extracted from user-selected files or clipboard data.
        _pendingDocumentText = null;
        _pendingDocumentFileName = null;
        _pendingDocumentNotice = null;
        if (VisionAttachmentPreview is not null)
            VisionAttachmentPreview.Source = null;
        UpdateVisionAttachmentControls();
        UpdateDocumentAttachmentUi();
    }

    private void UpdatePrivateChatModeUi()
    {
        var ar = IsArabic;
        IncognitoModeButton.Content = _incognitoMode
            ? (ar ? "إنهاء الوضع الخاص" : "Exit private chat")
            : (ar ? "محادثة خاصة" : "Private chat");
        IncognitoModeButton.ToolTip = _incognitoMode
            ? (ar ? "إنهاء الجلسة المؤقتة والتخلص من رسائلها غير المحفوظة." : "End this temporary session and discard its unsaved messages.")
            : (ar ? "بدء سياق فارغ لا يحفظ رسائل الدردشة محليًا. قد تُرسل الرسائل إلى مزودك حسب الإعدادات." : "Start a blank chat that does not save messages locally. Messages may still be sent to your provider.");
        AutomationProperties.SetName(IncognitoModeButton, _incognitoMode
            ? (ar ? "إنهاء المحادثة الخاصة" : "Exit private chat")
            : (ar ? "بدء محادثة خاصة" : "Start private chat"));
        AutomationProperties.SetHelpText(IncognitoModeButton, ar
            ? "جلسة مؤقتة لا تُكتب رسائلها أو تغييرات سجل المحادثات إلى القرص. تُحجب المهام وسجلها والروتينات والتذكيرات المرتبطة بها ومساحة العمل والذاكرة الشخصية. تبقى البيانات المحفوظة سابقًا كما هي وتعود بعد الخروج. قد تصل الرسائل المرسلة إلى مزود AI وتخضع لاحتفاظه."
            : "A temporary session: its messages and conversation-history changes are never written to disk. Tasks, task history, saved routines, task-linked reminders, the workspace hub, and personal memory are blocked. Previously saved data remains unchanged and returns on exit. Messages you send may still reach your AI provider and follow its retention policy.");
        var canAccessTasks = PrivateChatModePolicy.CanAccessTaskWorkspace(_incognitoMode);
        var canAccessMemory = PrivateChatModePolicy.CanAccessPersonalMemory(_incognitoMode);
        TasksTab.IsEnabled = canAccessTasks;
        MemoryTab.IsEnabled = canAccessMemory;
        TasksHeading.IsEnabled = canAccessTasks;
        TasksDescription.IsEnabled = canAccessTasks;
        TaskSkillHint.IsEnabled = canAccessTasks;
        TaskSafeToolTitle.IsEnabled = canAccessTasks;
        TaskSafeToolDescription.IsEnabled = canAccessTasks;
        TaskProgressStatus.IsEnabled = canAccessTasks;
        TaskProgressBar.IsEnabled = canAccessTasks;
        TaskEventsList.IsEnabled = canAccessTasks;
        TaskEventsEmptyStateText.IsEnabled = canAccessTasks;
        RecentTasksGroup.IsEnabled = canAccessTasks;
        RecentTasksEmptyStateText.IsEnabled = canAccessTasks;
        TaskCopyStatus.IsEnabled = canAccessTasks;
        GameModeButton.IsEnabled = canAccessTasks;
        GameModeSettingsButton.IsEnabled = canAccessTasks;
        ProactiveCheckInPrivacyNote.IsEnabled = canAccessTasks;

        MemoryHeading.IsEnabled = canAccessMemory;
        MemoryDescription.IsEnabled = canAccessMemory;
        MemoryCategoryFilterLabel.IsEnabled = canAccessMemory;
        MemoryCountText.IsEnabled = canAccessMemory;
        MemoryFilterNotice.IsEnabled = canAccessMemory;
        MemoryEmptyStateText.IsEnabled = canAccessMemory;
        MemoryCategoryLabel.IsEnabled = canAccessMemory;
        MemoryTitleLabel.IsEnabled = canAccessMemory;
        MemoryContentLabel.IsEnabled = canAccessMemory;
        MemoryResult.IsEnabled = canAccessMemory;
        MemoryPrivacyNote.IsEnabled = canAccessMemory;

        // Set each sensitive control explicitly as well as disabling its containing tab.
        // UI Automation can inspect child controls independently of tab navigation.
        TaskInput.IsEnabled = canAccessTasks && _agentTaskCancellation is null;
        RunAgentTaskButton.IsEnabled = canAccessTasks && _agentTaskCancellation is null;
        CancelAgentTaskButton.IsEnabled = canAccessTasks && _agentTaskCancellation is not null;
        TaskOpenNotepadButton.IsEnabled = canAccessTasks;
        RecentTasksList.IsEnabled = canAccessTasks;
        TaskResult.IsEnabled = canAccessTasks;
        CopyTaskLogButton.IsEnabled = canAccessTasks;
        CopyTaskResultButton.IsEnabled = canAccessTasks;
        ManageRemindersButton.IsEnabled = canAccessTasks &&
            PrivateChatModePolicy.CanAccessReminderManager(_incognitoMode);
        ManageRoutinesButton.IsEnabled = canAccessTasks &&
            PrivateChatModePolicy.CanAccessSavedRoutines(_incognitoMode);
        WorkspaceHubButton.IsEnabled = canAccessTasks &&
            PrivateChatModePolicy.CanAccessWorkspaceHub(_incognitoMode);

        MemoryList.IsEnabled = canAccessMemory;
        MemorySearchInput.IsEnabled = canAccessMemory;
        MemorySearchButton.IsEnabled = canAccessMemory;
        MemoryShowArchivedCheckBox.IsEnabled = canAccessMemory;
        MemoryCategoryFilter.IsEnabled = canAccessMemory;
        MemoryCategoryInput.IsEnabled = canAccessMemory;
        MemoryTitleInput.IsEnabled = canAccessMemory;
        MemoryContentInput.IsEnabled = canAccessMemory;
        NewMemoryButton.IsEnabled = canAccessMemory;
        SaveMemoryButton.IsEnabled = canAccessMemory;
        ArchiveMemoryButton.IsEnabled = canAccessMemory && !string.IsNullOrWhiteSpace(_selectedMemoryId);
        DeleteMemoryButton.IsEnabled = canAccessMemory && !string.IsNullOrWhiteSpace(_selectedMemoryId);
        ImportMemoryButton.IsEnabled = canAccessMemory;
        ExportMemoryButton.IsEnabled = canAccessMemory;
        LoreWebButton.IsEnabled = canAccessMemory;
        ClearMemoryFiltersButton.IsEnabled = canAccessMemory;

        ProactiveCheckInEnabledCheckBox.IsEnabled = !_incognitoMode;
        ProactiveCheckInIntervalSelector.IsEnabled = !_incognitoMode;
        ChatHistorySelector.IsEnabled = !_incognitoMode;
        ChatHistoryList.IsEnabled = !_incognitoMode;
        NewConversationButton.IsEnabled = !_incognitoMode && _chatCancellation is null;
        ClearSavedChatHistoryButton.IsEnabled = !_incognitoMode && _chatCancellation is null;
        QuickAskButton.IsEnabled = !_incognitoMode;
        QuickAskButton.ToolTip = _incognitoMode
            ? (ar ? "السؤال السريع متوقف أثناء المحادثة الخاصة." : "Quick Ask is disabled during private chat.")
            : (ar ? "افتح نافذة سؤال سريع مستقلة." : "Open a separate Quick Ask window.");
        GmailInboxButton.IsEnabled = !_incognitoMode;
        GmailInboxButton.ToolTip = _incognitoMode
            ? (ar ? "تكامل Gmail متوقف أثناء المحادثة الخاصة." : "Gmail is disabled during private chat.")
            : (ar ? "عرض بريد Gmail محليًا بصلاحية قراءة فقط؛ لا تتم مشاركته تلقائيًا." : "Preview Gmail locally with read-only access; messages are not shared automatically.");
        AutomationProperties.SetHelpText(GmailInboxButton, _incognitoMode
            ? (ar ? "غير متاح أثناء Incognito. اخرج من المحادثة الخاصة أولًا." : "Unavailable during Incognito. Exit private chat first.")
            : (ar ? "قراءة فقط. إضافة المحتوى إلى مسودة الشات تتطلب تأكيدًا، والإرسال إلى مزود AI يتطلب ضغط Send يدويًا." : "Read-only. Adding content to chat draft requires confirmation, and sending to the AI provider requires an explicit manual Send."));
    }

}
