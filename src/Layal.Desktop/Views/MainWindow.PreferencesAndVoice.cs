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

namespace Layal.Views;

public partial class MainWindow
{
    private void SaveProviderButton_Click(object sender, RoutedEventArgs e)
    {
        string? originalSecret = null;
        string? unreadableSecretBackup = null;
        ApiConnectionProfile? originalProfile = null;
        var originalSecretExists = false;
        var secretWriteSucceeded = false;
        var profileWriteSucceeded = false;
        var submittedApiKey = string.Empty;
        string? profileNameToRefresh = null;

        try
        {
            originalSecretExists = AppRuntime.SecretStore.Exists;
            originalSecret = AppRuntime.SecretStore.Load();
            originalProfile = AppRuntime.ApiConnectionService.GetAll().FirstOrDefault(x => x.IsLayalProvider);

            var baseUrl = BaseUrlInput.Text.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException(IsArabic ? "اكتب عنوان HTTP/HTTPS صحيحًا دون بيانات دخول داخل الرابط." : "Enter a valid HTTP/HTTPS base URL without embedded credentials.");
            var model = ModelInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException(IsArabic ? "معرّف النموذج مطلوب." : "Model ID is required.");

            submittedApiKey = ApiKeyInput.Password.Trim();
            var profileSecret = !string.IsNullOrWhiteSpace(submittedApiKey) ? submittedApiKey : originalSecret;
            if (originalProfile is not null && string.IsNullOrWhiteSpace(profileSecret))
            {
                throw new InvalidOperationException(IsArabic
                    ? "تعذر قراءة مفتاح مزود API المحفوظ أو لم يعد موجودًا. لم يتم حفظ أي إعدادات؛ أدخل المفتاح مجددًا ثم أعد الحفظ."
                    : "The saved API provider key is missing or unreadable. No settings were saved; enter the key again and retry.");
            }

            // If the user is replacing an unreadable stored key, preserve its encrypted bytes
            // first so a later failure can restore the exact previous credential file.
            if (!string.IsNullOrWhiteSpace(submittedApiKey) &&
                originalSecretExists &&
                string.IsNullOrWhiteSpace(originalSecret))
            {
                unreadableSecretBackup = AppRuntime.SecretStore.PreserveUnreadableCopy();
                if (string.IsNullOrWhiteSpace(unreadableSecretBackup))
                {
                    throw new InvalidOperationException(IsArabic
                        ? "تعذر حفظ نسخة احتياطية من مفتاح API القديم غير المقروء؛ لم نغيّر المفتاح أو الإعدادات."
                        : "The unreadable API key could not be backed up; the key and settings were left unchanged.");
                }
            }

            var settingsToSave = AppRuntime.Settings.Clone();
            settingsToSave.ProviderBaseUrl = baseUrl;
            settingsToSave.ModelId = model;
            settingsToSave.VoiceRepliesEnabled = VoiceReplyCheckbox.IsChecked == true;

            // Save credentials and the linked API profile before committing settings.json.
            // This prevents a rejected profile save from leaving the visible settings half-updated.
            if (!string.IsNullOrWhiteSpace(submittedApiKey))
            {
                AppRuntime.SecretStore.Save(submittedApiKey);
                secretWriteSucceeded = true;
            }

            if (originalProfile is not null)
            {
                var layalProviderProfile = CloneApiConnectionProfile(originalProfile);
                layalProviderProfile.BaseUrl = baseUrl;
                layalProviderProfile.ModelId = model;
                layalProviderProfile.AuthSecret = profileSecret!;
                AppRuntime.ApiConnectionService.Save(layalProviderProfile);
                profileWriteSucceeded = true;
                profileNameToRefresh = layalProviderProfile.Name;
            }

            AppRuntime.SettingsStore.Save(settingsToSave);
            AppRuntime.Settings.CopyFrom(settingsToSave);
        }
        catch (Exception ex)
        {
            var rollbackFailures = new List<Exception>();

            if (profileWriteSucceeded && originalProfile is not null)
            {
                try { AppRuntime.ApiConnectionService.Save(originalProfile); }
                catch (Exception rollbackError) { rollbackFailures.Add(rollbackError); }
            }

            if (secretWriteSucceeded)
            {
                try
                {
                    if (originalSecretExists)
                    {
                        if (!string.IsNullOrWhiteSpace(originalSecret))
                            AppRuntime.SecretStore.Save(originalSecret);
                        else if (!string.IsNullOrWhiteSpace(unreadableSecretBackup))
                            AppRuntime.SecretStore.RestoreUnreadableCopy(unreadableSecretBackup);
                        else
                            throw new InvalidOperationException("The previous API key could not be restored because no readable secret or backup was available.");
                    }
                    else
                    {
                        AppRuntime.SecretStore.Delete();
                    }
                }
                catch (Exception rollbackError) { rollbackFailures.Add(rollbackError); }
            }

            Exception reportedError = ex;
            if (rollbackFailures.Count > 0)
            {
                rollbackFailures.Insert(0, ex);
                reportedError = new AggregateException("Saving provider settings failed and rollback was incomplete.", rollbackFailures);
            }

            SetAccessibleStatusText(ProviderActionStatus, UiLocalization.FormatTechnicalError("تعذر حفظ الإعدادات", "Could not save settings", reportedError, IsArabic));
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
            return;
        }

        if (!string.IsNullOrWhiteSpace(submittedApiKey)) ApiKeyInput.Clear();
        if (!string.IsNullOrWhiteSpace(profileNameToRefresh))
            RefreshApiConnections(profileNameToRefresh);
        SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم حفظ الإعدادات. لم يتم تأكيد الاتصال حتى الآن؛ استخدم اختبار الاتصال." : "Settings saved. Connectivity is not confirmed yet; run the connection test.");
        ProviderActionStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        RefreshProviderStatus();
    }

    private static ApiConnectionProfile CloneApiConnectionProfile(ApiConnectionProfile profile) => new()
    {
        Name = profile.Name,
        BaseUrl = profile.BaseUrl,
        ImagePath = profile.ImagePath,
        ModelId = profile.ModelId,
        IsLayalProvider = profile.IsLayalProvider,
        AuthenticationType = profile.AuthenticationType,
        AuthHeaderName = profile.AuthHeaderName,
        AuthSecret = profile.AuthSecret,
        AdditionalHeadersJson = profile.AdditionalHeadersJson,
        Instructions = profile.Instructions,
        TestPath = profile.TestPath
    };

    private void VoiceReplySettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppRuntime.Settings.VoiceRepliesEnabled = VoiceReplyCheckbox.IsChecked == true;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        SetAccessibleStatusText(ProviderActionStatus, IsArabic
            ? (AppRuntime.Settings.VoiceRepliesEnabled ? "تم تفعيل قراءة ليال لردودها صوتيًا." : "تم إيقاف الردود الصوتية لليال.")
            : (AppRuntime.Settings.VoiceRepliesEnabled ? "Layal voice replies are enabled." : "Layal voice replies are disabled."));
    }

    private void VoiceDeliveryStyleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || VoiceDeliveryStyleSelector.SelectedItem is not ComboBoxItem item) return;
        var style = VoiceDeliveryStylePolicy.Normalize(item.Tag?.ToString());
        var previous = AppRuntime.Settings.VoiceDeliveryStyleId;
        if (string.Equals(previous, style, StringComparison.Ordinal)) return;
        AppRuntime.Settings.VoiceDeliveryStyleId = style;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "تم حفظ نبرة الإلقاء الصوتي. هوية الصوت وأسلوب النص لم يتغيرا."
                : "Spoken delivery style saved. Voice identity and text style are unchanged.");
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.VoiceDeliveryStyleId = previous;
            VoiceDeliveryStyleSelector.SelectedValue = VoiceDeliveryStylePolicy.Normalize(previous);
            SetAccessibleStatusText(ProviderActionStatus,
                UiLocalization.FormatTechnicalError("تعذر حفظ نبرة الإلقاء", "Could not save delivery style", ex, IsArabic));
        }
    }

    private void CharacterDisplayModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || CharacterDisplayModeSelector.SelectedItem is not ComboBoxItem item) return;
        var mode = CharacterDisplayModePolicy.Normalize(item.Tag?.ToString());
        var previous = AppRuntime.Settings.MascotCharacterDisplayMode;
        if (string.Equals(previous, mode, StringComparison.Ordinal)) return;
        AppRuntime.Settings.MascotCharacterDisplayMode = mode;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            AppRuntime.MascotWindow?.ApplyCharacterDisplayMode(mode);
            SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم حفظ طريقة عرض الشخصية." : "Character display preference saved.");
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.MascotCharacterDisplayMode = previous;
            CharacterDisplayModeSelector.SelectedValue = CharacterDisplayModePolicy.Normalize(previous);
            AppRuntime.MascotWindow?.ApplyCharacterDisplayMode(previous);
            SetAccessibleStatusText(ProviderActionStatus,
                UiLocalization.FormatTechnicalError("تعذر حفظ طريقة عرض الشخصية", "Could not save character display preference", ex, IsArabic));
        }
    }

    private void AssistantPersonaSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || AssistantPersonaSelector.SelectedItem is not ComboBoxItem item)
            return;

        var persona = item.Tag?.ToString();
        if (persona is not ("warm" or "focused" or "professional"))
            persona = "warm";
        if (string.Equals(AppRuntime.Settings.AssistantPersona, persona, StringComparison.Ordinal))
            return;

        var previous = AppRuntime.Settings.AssistantPersona;
        AppRuntime.Settings.AssistantPersona = persona;
        try
        {
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            SetChatActivityStatus(IsArabic
                ? "تم حفظ نمط الرد. هذا الإعداد يغيّر الصياغة فقط ولا يغيّر الصلاحيات."
                : "Response style saved. This preference changes wording only, not permissions.");
        }
        catch (Exception ex)
        {
            AppRuntime.Settings.AssistantPersona = previous;
            AssistantPersonaSelector.SelectedItem = AssistantPersonaSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), previous, StringComparison.Ordinal));
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر حفظ نمط الرد", "Could not save response style", ex, IsArabic));
        }
    }

    private async void TestVoiceReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceTestCancellation is not null) return;
        if (_chatCancellation is not null)
        {
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "أوقف الرد الحالي قبل تجربة الصوت."
                : "Stop the active reply before previewing speech.");
            return;
        }

        await TestLocalVoiceAsync();
    }

    private async Task TestLocalVoiceAsync()
    {
        var cancellation = new CancellationTokenSource();
        _voiceTestCancellation = cancellation;
        TestVoiceReplyButton.IsEnabled = false;
        try
        {
            if (!await EnsureLocalAiSetupAsync(includeVoice: true, retryVoice: true, cancellationToken: cancellation.Token) ||
                !_localVoiceReady)
                return;
            await _speechLanguageRuntime.SpeakAsync(
                "أهلًا، أنا ليال. خلّينا نتكلم بهدوء، خطوة بخطوة. I will speak locally and clearly.",
                new SpeechSynthesisOptions(
                    AppRuntime.Settings.AudioOutputDeviceId,
                    AppRuntime.Settings.VoiceDeliveryStyleId),
                cancellation.Token);
            SetAccessibleStatusText(ProviderActionStatus, IsArabic
                ? "انتهت تجربة الصوت المحلي؛ لم يُرسل النص إلى خادم."
                : "Local voice preview completed; text was not sent to a server.");
        }
        catch (OperationCanceledException)
        {
            SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم إيقاف تجربة الصوت المحلي." : "Local voice preview cancelled.");
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(ProviderActionStatus,
                UiLocalization.FormatTechnicalError("تعذر تشغيل الصوت المحلي", "Could not play local voice", ex, IsArabic));
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
            var result = await _providerConnectionTester.ProbeAsync(baseUrl, key);
                    var testSummary = IsArabic ? $"نجح الاختبار: {result}" : $"Test succeeded: {result}";
            SetAccessibleStatusText(ProviderActionStatus, UiLocalization.TranslateKnownFeedback(testSummary, IsArabic));
            ProviderActionStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
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
        var result = LayalDialogService.Show(
            IsArabic ? "هل تريد حذف مفتاح AI المحفوظ على حساب Windows الحالي؟" : "Delete the saved AI key for the current Windows user?",
            IsArabic ? "تأكيد حذف المفتاح" : "Confirm key deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        AppRuntime.SecretStore.Delete();
        ApiKeyInput.Clear();
        SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم حذف المفتاح المحفوظ." : "Saved key deleted.");
        ProviderActionStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        RefreshProviderStatus();
    }

    private async void PerformanceMetricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (PerformanceMetricsButton is null || PerformanceMetricsStatus is null)
            return;

        PerformanceMetricsButton.IsEnabled = false;
        PerformanceMetricsStatus.Text = IsArabic
            ? "جارٍ قياس موارد عملية ليال محليًا لمدة ثانية..."
            : "Measuring Layal process resources locally for one second...";

        try
        {
            var sample = await PerformanceMetricsService.MeasureCurrentProcessAsync(
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            var workingSet = FormatMetricBytes(sample.WorkingSetBytes);
            var privateBytes = FormatMetricBytes(sample.PrivateBytes);
            var managedHeap = FormatMetricBytes(sample.ManagedHeapBytes);
            PerformanceMetricsStatus.Text = IsArabic
                ? $"CPU للعملية: {sample.CpuPercent.ToString("F1", CultureInfo.InvariantCulture)}% من سعة الأنوية المنطقية خلال {sample.SamplingDuration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} ث | الذاكرة الفعلية: {workingSet} | الذاكرة الخاصة: {privateBytes} | .NET Heap: {managedHeap} | الخيوط: {sample.ThreadCount} | عمليات GC خلال العينة (0/1/2): {sample.Gen0Collections}/{sample.Gen1Collections}/{sample.Gen2Collections}. قياس للعملية الحالية فقط؛ لا يشمل GPU أو التطبيقات الأخرى."
                : $"Process CPU: {sample.CpuPercent.ToString("F1", CultureInfo.InvariantCulture)}% of logical-processor capacity over {sample.SamplingDuration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s | Working set: {workingSet} | Private memory: {privateBytes} | .NET heap: {managedHeap} | Threads: {sample.ThreadCount} | GC collections during sample (Gen 0/1/2): {sample.Gen0Collections}/{sample.Gen1Collections}/{sample.Gen2Collections}. Current process only; no GPU or other applications.";
        }
        catch (Exception ex)
        {
            PerformanceMetricsStatus.Text = (IsArabic ? "تعذر قياس الأداء: " : "Performance measurement failed: ") + ex.Message;
        }
        finally
        {
            PerformanceMetricsButton.IsEnabled = true;
        }
    }

    private static string FormatMetricBytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    private void MascotSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppRuntime.Settings.MascotAlwaysOnTop = AlwaysOnTopCheckbox.IsChecked == true;
        AppRuntime.Settings.MascotAnimationsEnabled = AnimationsCheckbox.IsChecked == true;
        AppRuntime.Settings.PerformanceModeEnabled = PerformanceModeCheckbox.IsChecked == true;
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        if (AppRuntime.MascotWindow is not null)
        {
            AppRuntime.MascotWindow.Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
            AppRuntime.MascotWindow.ApplyPerformanceMode();
        }
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
        AppRuntime.Settings.PerformanceModeEnabled = PerformanceModeCheckbox.IsChecked == true;
        AppRuntime.Settings.MascotScale = Math.Clamp(MascotSizeSlider.Value, 0.65, 1.8);
        AppRuntime.Settings.MascotOpacity = Math.Clamp(MascotOpacitySlider.Value, 0.35, 1.0);
        AppRuntime.SettingsStore.Save(AppRuntime.Settings);
        if (AppRuntime.MascotWindow is not null)
        {
            AppRuntime.MascotWindow.Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
            AppRuntime.MascotWindow.Opacity = AppRuntime.Settings.MascotOpacity;
            AppRuntime.MascotWindow.ApplyScale(AppRuntime.Settings.MascotScale);
            AppRuntime.MascotWindow.ApplyPerformanceMode();
        }
        SetAccessibleStatusText(ProviderActionStatus, IsArabic ? "تم تطبيق إعدادات الشخصية." : "Mascot settings applied.");
    }

    private async void SpeechToTextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speechCancellation is not null)
        {
            _speechCancellation.Cancel();
            SetChatActivityStatus(IsArabic ? "جارٍ إلغاء تحويل الصوت المحلي..." : "Cancelling local speech transcription...");
            return;
        }
        if (_speechIsStopping) return;

        if (AppRuntime.Settings.PrivacyShieldEnabled && !_microphoneCapture.IsRecording)
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لا يمكن بدء تسجيل أو تحويل صوت جديد."
                : "Privacy shield is enabled; new microphone recording or transcription is blocked.");
            return;
        }

        if (_microphoneCapture.IsRecording)
        {
            _speechRecordingTimer.Stop();
            await StopSpeechRecordingAndTranscribeAsync();
            return;
        }

        if (!await EnsureLocalAiSetupAsync(includeVoice: true, retryVoice: true))
            return;
        if (IsCaptureBlockedByPrivacyShield())
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لم يبدأ تسجيل الميكروفون."
                : "The privacy shield is active; microphone recording did not start.");
            return;
        }
        if (!_localVoiceReady)
        {
            SetChatActivityStatus(IsArabic
                ? "موارد الصوت المحلي غير جاهزة. المحادثة والمهام النصية تعملان بالفعل دون نماذج."
                : "Local speech resources are not ready. Text chat and tasks already work without models.");
            return;
        }

        var microphoneConsent = LayalDialogService.Show(
            this,
            IsArabic
                ? "سيبدأ التسجيل بعد موافقتك. التسجيل يدوي وبحد أقصى دقيقتين. سيُعالج الصوت محليًا عبر Whisper، ولن يُرفع إلى أي مزود. النص الناتج لن يدخل المحادثة إلا بعد مراجعتك وضغط إرسال. هل تريد تشغيل الميكروفون؟"
                : "Recording starts after your consent and is limited to two minutes. Audio is transcribed locally with Whisper and is not uploaded to any provider. The transcript enters the conversation only after you review it and press Send. Start the microphone?",
            IsArabic ? "السماح بالتسجيل المحلي" : "Allow local microphone recording",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (microphoneConsent != MessageBoxResult.Yes) return;
        if (IsCaptureBlockedByPrivacyShield())
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لم يبدأ تسجيل الميكروفون."
                : "The privacy shield is active; microphone recording did not start.");
            return;
        }

        try
        {
            _microphoneCapture.StartRecording(AppRuntime.Settings.AudioInputDeviceId);
            _speechRecordingStartedAt = DateTimeOffset.UtcNow;
            _speechRecordingTimer.Start();
            UpdateSpeechToTextButton();
            SetChatActivityStatus(IsArabic
                ? "الميكروفون يعمل؛ التحويل محلي. اضغط «إيقاف وتحويل» عند الانتهاء."
                : "Microphone active; transcription is local. Press Stop & transcribe when done.");
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
        if (!_microphoneCapture.IsRecording) return;
        if (DateTimeOffset.UtcNow - _speechRecordingStartedAt < MicrophoneCaptureService.MaximumRecordingDuration)
            return;
        _speechRecordingTimer.Stop();
        if (_localVoiceRecordingActive)
            _ = StopLocalVoiceConversationAndSendAsync();
        else
            _ = StopSpeechRecordingAndTranscribeAsync();
    }

    private async Task StopSpeechRecordingAndTranscribeAsync()
    {
        if (_speechIsStopping || !_microphoneCapture.IsRecording) return;
        _discardSpeechAudioOnStop = false;
        _speechIsStopping = true;
        _speechRecordingTimer.Stop();
        UpdateSpeechToTextButton();
        byte[]? audio = null;
        CancellationTokenSource? transcription = null;
        try
        {
            var recording = await _microphoneCapture.StopRecordingAsync();
            audio = recording.WavBytes;
            _speechIsStopping = false;
            UpdateSpeechToTextButton();

            if (ShouldDiscardPendingSpeechAudio())
            {
                SetChatActivityStatus(IsArabic
                    ? "تم التخلص من التسجيل؛ درع الخصوصية أو طلب الإلغاء منع التحويل."
                    : "Recording discarded; privacy shield or cancellation prevented transcription.");
                return;
            }
            if (!_localVoiceReady)
            {
                SetChatActivityStatus(IsArabic
                    ? "موارد Whisper المحلية غير جاهزة؛ لم يتم إرسال الصوت إلى أي مزود."
                    : "Local Whisper resources are not ready; the audio was not sent to any provider.");
                return;
            }

            transcription = new CancellationTokenSource();
            _speechCancellation = transcription;
            UpdateSpeechToTextButton();
            SetChatActivityStatus(IsArabic
                ? "جارٍ تحويل التسجيل محليًا. لن يُرفع الصوت إلى خادم."
                : "Transcribing locally. Audio will not be uploaded to a server.");

            var recognition = await _speechLanguageRuntime.RecognizeAsync(audio, transcription.Token);
            var transcript = recognition.Text;
            if (ShouldDiscardPendingSpeechAudio() || transcription.IsCancellationRequested)
            {
                SetChatActivityStatus(IsArabic
                    ? "تم التخلص من نتيجة التحويل بعد تفعيل درع الخصوصية أو الإلغاء."
                    : "The transcript was discarded after the privacy shield or cancellation became active.");
                return;
            }

            ChatInput.Text = string.IsNullOrWhiteSpace(ChatInput.Text)
                ? transcript
                : ChatInput.Text.TrimEnd() + Environment.NewLine + transcript;
            ChatInput.CaretIndex = ChatInput.Text.Length;
            ChatInput.Focus();
            var detectedLanguage = recognition.Analysis.GetLanguageLabel(IsArabic);
            SetChatActivityStatus(IsArabic
                ? $"تم التحويل محليًا ({detectedLanguage}). راجع النص ثم اضغط إرسال؛ لم تُرسل الرسالة تلقائيًا."
                : $"Local transcription completed ({detectedLanguage}). Review the text and press Send; it was not sent automatically.");
        }
        catch (OperationCanceledException)
        {
            SetChatActivityStatus(IsArabic ? "تم إلغاء تحويل الصوت المحلي." : "Local transcription cancelled.");
        }
        catch (Exception ex)
        {
            SetChatActivityStatus((IsArabic ? "فشل تحويل الصوت المحلي: " : "Local transcription failed: ") + ex.Message);
        }
        finally
        {
            if (audio is not null) Array.Clear(audio, 0, audio.Length);
            if (ReferenceEquals(_speechCancellation, transcription))
                _speechCancellation = null;
            transcription?.Dispose();
            _discardSpeechAudioOnStop = false;
            _speechIsStopping = false;
            UpdateSpeechToTextButton();
        }
    }

    private bool ShouldDiscardPendingSpeechAudio() =>
        PrivacyShieldPolicy.ShouldDiscardCapturedAudio(
            AppRuntime.Settings.PrivacyShieldEnabled,
            IsVisible,
            AppRuntime.IsExiting,
            _discardSpeechAudioOnStop);

    private void UpdateSpeechToTextButton()
    {
        SpeechToTextButton.Content = _microphoneCapture.IsRecording
            ? (IsArabic ? "إيقاف وتحويل" : "Stop & transcribe")
            : _speechIsStopping
                ? (IsArabic ? "جارٍ إيقاف التسجيل…" : "Stopping recording…")
                : _speechCancellation is not null
                    ? (IsArabic ? "إلغاء التحويل" : "Cancel transcription")
                    : (IsArabic ? "تسجيل صوت" : "Record voice");
        SpeechToTextButton.IsEnabled = !_speechIsStopping &&
            (!AppRuntime.Settings.PrivacyShieldEnabled || _microphoneCapture.IsRecording || _speechCancellation is not null);
        SpeechToTextButton.ToolTip = _microphoneCapture.IsRecording
            ? AppRuntime.Settings.UseLocalAi
                ? (IsArabic ? "اضغط لإيقاف التسجيل وتحويله محليًا. لن يُرفع الصوت." : "Stop recording and transcribe locally. Audio will not be uploaded.")
                : (IsArabic ? "اضغط لإيقاف التسجيل. لن يُرسل الصوت إلا بعد تأكيد منفصل." : "Press to stop recording. Audio is uploaded only after separate confirmation.")
            : _speechCancellation is not null
                ? (IsArabic ? "إلغاء طلب تحويل الصوت الحالي." : "Cancel the current speech transcription request.")
                : AppRuntime.Settings.UseLocalAi
                    ? (IsArabic ? "تسجيل وتحويل محليان على هذا الجهاز." : "Record and transcribe locally on this device.")
                    : (IsArabic ? "تسجيل يدوي فقط؛ لن يُرسل الصوت قبل تأكيدك." : "Manual recording only; audio is not uploaded until you confirm.");
    }

    private async Task LocalVoiceConversationButton_ClickAsync()
    {
        if (_localVoiceRecordingActive)
        {
            await StopLocalVoiceConversationAndSendAsync();
            return;
        }
        if (_voiceConversationCancellation is not null)
        {
            _voiceConversationCancellation.Cancel();
            SetChatActivityStatus(IsArabic ? "جارٍ إيقاف معالجة الصوت المحلي..." : "Cancelling local voice processing...");
            return;
        }
        if (_chatCancellation is not null || _voiceTestCancellation is not null ||
            _microphoneCapture.IsRecording || _speechCancellation is not null || _speechIsStopping ||
            _cameraVisionWindow is { IsVisible: true })
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف العملية الحالية قبل بدء الكلام المحلي."
                : "Stop the current operation before starting local voice input.");
            return;
        }

        if (!await EnsureLocalAiSetupAsync(includeVoice: true, retryVoice: true)) return;
        if (IsCaptureBlockedByPrivacyShield())
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لم يبدأ تسجيل الميكروفون."
                : "The privacy shield is active; microphone recording did not start.");
            return;
        }
        if (!_localVoiceReady)
        {
            SetChatActivityStatus(IsArabic
                ? "تعذر تجهيز موارد الصوت المحلي. أعد المحاولة من الإعدادات."
                : "Local speech resources are not ready. Retry setup in Settings.");
            return;
        }

        var consent = LayalDialogService.Show(
            this,
            IsArabic
                ? "سيظل الميكروفون نشطًا حتى تضغط الزر مرة ثانية أو Esc. سيُعالج التسجيل على هذا الجهاز باستخدام نماذج محلية؛ قد يُكتب ملف مؤقت ثم يحذفه التطبيق. لن يُرسل الصوت إلى خادم سحابي. بعد التحويل سيُعالج النص داخل نواة ليال المستقلة دون استدعاء أي نموذج محادثة. هل تريد البدء؟"
                : "The microphone stays active until you press the button again or Esc. The recording is processed on this computer with local models; a temporary audio file may be created and then deleted. Audio is not sent to a cloud server. After transcription, the text is handled by Layal's native core without calling a chat model. Start?",
            IsArabic ? "بدء الكلام المحلي" : "Start local voice input",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
        if (consent != MessageBoxResult.Yes) return;
        if (IsCaptureBlockedByPrivacyShield())
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لم يبدأ تسجيل الميكروفون."
                : "The privacy shield is active; microphone recording did not start.");
            return;
        }

        var session = new CancellationTokenSource();
        try
        {
            _microphoneCapture.StartRecording(AppRuntime.Settings.AudioInputDeviceId);
            _voiceConversationCancellation = session;
            _localVoiceRecordingActive = true;
            _speechRecordingStartedAt = DateTimeOffset.UtcNow;
            _speechRecordingTimer.Start();
            ChatInput.IsEnabled = false;
            SendButton.IsEnabled = false;
            SpeechToTextButton.IsEnabled = false;
            CancelResponseButton.IsEnabled = false;
            NewConversationButton.IsEnabled = false;
            ClearSavedChatHistoryButton.IsEnabled = false;
            ChatHistorySelector.IsEnabled = false;
                ChatHistoryList.IsEnabled = false;
            UpdateVoiceConversationControls();
            UpdateSpeechToTextButton();
            SetChatActivityStatus(IsArabic
                ? "بتسمعك ليال محليًا… اضغط «إنهاء الكلام وإرسال» لإيقاف التسجيل وإرسال النص إلى الشات المحلي."
                : "Layal is listening locally… Press Finish speaking & send to stop recording and send the transcript to local chat.");
        }
        catch (Exception ex)
        {
            _localVoiceRecordingActive = false;
            if (ReferenceEquals(_voiceConversationCancellation, session)) _voiceConversationCancellation = null;
            session.Dispose();
            SetChatActivityStatus((IsArabic ? "تعذر بدء التسجيل المحلي: " : "Could not start local recording: ") + ex.Message);
            UpdateVoiceConversationControls();
            UpdateSpeechToTextButton();
        }
    }

    private async Task StopLocalVoiceConversationAndSendAsync()
    {
        if (!_localVoiceRecordingActive || _speechIsStopping) return;
        _localVoiceRecordingActive = false;
        _speechIsStopping = true;
        _speechRecordingTimer.Stop();
        UpdateVoiceConversationControls();
        UpdateSpeechToTextButton();
        byte[]? audio = null;
        var session = _voiceConversationCancellation;
        try
        {
            var recording = await _microphoneCapture.StopRecordingAsync();
            audio = recording.WavBytes;
            if (ShouldDiscardPendingSpeechAudio() || session?.IsCancellationRequested == true)
            {
                SetChatActivityStatus(IsArabic
                    ? "تم التخلص من التسجيل الصوتي المحلي بعد تفعيل درع الخصوصية أو الإلغاء."
                    : "The local voice recording was discarded after the privacy shield or cancellation became active.");
                return;
            }
            if (!IsVisible || AppRuntime.IsExiting)
            {
                SetChatActivityStatus(IsArabic ? "تم إلغاء التسجيل المحلي والتخلص من الصوت المؤقت." : "Local recording cancelled; temporary audio discarded.");
                return;
            }

            var recognition = await _speechLanguageRuntime.RecognizeAsync(audio, session?.Token ?? CancellationToken.None);
            var transcript = recognition.Text;
            if (ShouldDiscardPendingSpeechAudio() || session?.IsCancellationRequested == true)
            {
                SetChatActivityStatus(IsArabic
                    ? "تم التخلص من نص الصوت المحلي بعد تفعيل درع الخصوصية أو الإلغاء."
                    : "The local speech transcript was discarded after the privacy shield or cancellation became active.");
                return;
            }
            if (string.IsNullOrWhiteSpace(transcript))
            {
                SetChatActivityStatus(IsArabic ? "لم يُتعرّف على كلام واضح؛ حاول مرة أخرى." : "No clear speech was recognized; try again.");
                return;
            }

            ChatInput.Text = string.IsNullOrWhiteSpace(ChatInput.Text)
                ? transcript
                : ChatInput.Text.TrimEnd() + Environment.NewLine + transcript;
            ChatInput.CaretIndex = ChatInput.Text.Length;

            // Release the voice-session guard before SendMessageAsync takes ownership of the turn.
            if (ReferenceEquals(_voiceConversationCancellation, session))
            {
                _voiceConversationCancellation = null;
                session?.Dispose();
            }
            if (IsLoaded)
            {
                ChatInput.IsEnabled = true;
                SendButton.IsEnabled = _chatCancellation is null;
                NewConversationButton.IsEnabled = !_incognitoMode;
                ClearSavedChatHistoryButton.IsEnabled = !_incognitoMode;
                ChatHistorySelector.IsEnabled = !_incognitoMode;
        ChatHistoryList.IsEnabled = !_incognitoMode;
            }
            UpdateVoiceConversationControls();
            var detectedLanguage = recognition.Analysis.GetLanguageLabel(IsArabic);
            SetChatActivityStatus(IsArabic
                ? $"تم تحويل كلامك محليًا ({detectedLanguage})؛ جارٍ معالجة النص داخل نواة ليال المستقلة."
                : $"Local transcription complete ({detectedLanguage}); processing the text with Layal's native core.");
            await SendMessageAsync();
        }
        catch (OperationCanceledException)
        {
            SetChatActivityStatus(IsArabic ? "تم إلغاء التسجيل أو التحويل المحلي." : "Local recording or transcription cancelled.");
        }
        catch (Exception ex)
        {
            SetChatActivityStatus((IsArabic ? "تعذر إكمال المحادثة الصوتية المحلية: " : "Could not complete local voice conversation: ") + ex.Message);
        }
        finally
        {
            if (audio is not null) Array.Clear(audio, 0, audio.Length);
            if (ReferenceEquals(_voiceConversationCancellation, session))
            {
                _voiceConversationCancellation = null;
                session?.Dispose();
            }
            _localVoiceRecordingActive = false;
            _speechIsStopping = false;
            UpdateVoiceConversationControls();
            UpdateSpeechToTextButton();
            if (IsLoaded)
            {
                ChatInput.IsEnabled = true;
                SendButton.IsEnabled = _chatCancellation is null;
                NewConversationButton.IsEnabled = !_incognitoMode;
                ClearSavedChatHistoryButton.IsEnabled = !_incognitoMode;
                ChatHistorySelector.IsEnabled = !_incognitoMode;
        ChatHistoryList.IsEnabled = !_incognitoMode;
                RefreshProviderStatus();
            }
        }
    }

    private void CancelLocalVoiceRecording()
    {
        if (!_localVoiceRecordingActive) return;
        _localVoiceRecordingActive = false;
        _speechRecordingTimer.Stop();
        try { _microphoneCapture.CancelRecording(); } catch { /* The capture service may already have stopped. */ }
        var session = _voiceConversationCancellation;
        _voiceConversationCancellation = null;
        session?.Cancel();
        session?.Dispose();
        ChatInput.IsEnabled = true;
        SendButton.IsEnabled = _chatCancellation is null;
        NewConversationButton.IsEnabled = !_incognitoMode;
        ClearSavedChatHistoryButton.IsEnabled = !_incognitoMode;
        ChatHistorySelector.IsEnabled = !_incognitoMode;
        ChatHistoryList.IsEnabled = !_incognitoMode;
        SetChatActivityStatus(IsArabic ? "تم إلغاء التسجيل المحلي؛ لم يتم إرسال الصوت." : "Local recording cancelled; audio was not sent.");
        UpdateVoiceConversationControls();
        UpdateSpeechToTextButton();
    }

    private async void VoiceConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppRuntime.Settings.PrivacyShieldEnabled &&
            _voiceConversationCancellation is null && !_localVoiceRecordingActive)
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لا يمكن بدء محادثة صوتية أو تشغيل الميكروفون."
                : "Privacy shield is enabled; starting voice chat or the microphone is blocked.");
            return;
        }

        // Whisper performs local transcription; the native core generates the reply.
        await LocalVoiceConversationButton_ClickAsync();
    }

    private void UpdateVoiceConversationControls()
    {
        if (VoiceConversationButton is null) return;
        var active = _voiceConversationCancellation is not null;
        VoiceConversationButton.Content = _localVoiceRecordingActive
            ? (IsArabic ? "إنهاء الكلام وإرسال" : "Finish speaking & send")
            : active
                ? (IsArabic ? "جارٍ تحويل الصوت المحلي…" : "Transcribing local voice…")
                : (IsArabic ? "التحدث إلى ليال محليًا" : "Talk to Layal locally");
        VoiceConversationButton.ToolTip = _localVoiceRecordingActive
            ? (IsArabic ? "اضغط لإيقاف الميكروفون وتحويل التسجيل محليًا وإرسال النص إلى نواة ليال." : "Stop the microphone, transcribe locally, and send the text to Layal's native core.")
            : active
                ? (IsArabic ? "اضغط لإلغاء تحويل الصوت المحلي، أو اضغط Esc." : "Cancel local transcription, or press Esc.")
                : (IsArabic ? "محادثة صوتية بالتناوب: اضغط للتسجيل ثم اضغط مرة ثانية للإرسال. الرد يصدر من نواة ليال المحلية." : "Turn-based local voice: press to record, then press again to send. Replies come from Layal's native core.");
        AutomationProperties.SetName(VoiceConversationButton, VoiceConversationButton.Content?.ToString() ?? string.Empty);
        AutomationProperties.SetHelpText(VoiceConversationButton, IsArabic
            ? "اضغط للتسجيل ومرة ثانية لإرسال الكلام إلى نواة ليال المستقلة. لا يوجد بث صوتي سحابي."
            : "Press to record and press again to send speech to Layal's native core. There is no cloud audio stream.");
        VoiceConversationIndicator.Visibility = _localVoiceRecordingActive || active
            ? Visibility.Visible
            : Visibility.Collapsed;
        VoiceConversationMicDot.Fill = ThemeBrush(
            _localVoiceRecordingActive || active ? "Layal.Danger" : "Layal.Warning",
            _localVoiceRecordingActive || active ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkGoldenrod);
        VoiceConversationMicStatus.Text = _localVoiceRecordingActive
            ? (IsArabic ? "الميكروفون نشط محليًا" : "Microphone active locally")
            : active
                ? (IsArabic ? "جارٍ التحويل محليًا" : "Transcribing locally")
                : (IsArabic ? "الميكروفون متوقف" : "Microphone off");
        VoiceConversationButton.IsEnabled = !AppRuntime.Settings.PrivacyShieldEnabled ||
            _voiceConversationCancellation is not null || _localVoiceRecordingActive;
    }

}
