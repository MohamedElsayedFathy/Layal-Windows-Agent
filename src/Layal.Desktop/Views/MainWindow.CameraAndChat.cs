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
    private void CameraVisionButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppRuntime.Settings.PrivacyShieldEnabled)
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ فتح الكاميرا محظور."
                : "Privacy shield is enabled; opening the camera is blocked.");
            return;
        }

        if (_voiceConversationCancellation is not null)
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف المحادثة الصوتية قبل فتح الرؤية بالكاميرا."
                : "Stop the live voice conversation before opening camera vision.");
            return;
        }

        if (_cameraVisionWindow is { IsVisible: true })
        {
            _cameraVisionWindow.Activate();
            return;
        }

        var consent = LayalDialogService.Show(this,
            IsArabic
                ? "ستظهر معاينة الكاميرا على جهازك فقط. تحليل الصور غير متاح في النواة المستقلة حاليًا؛ أزرار التحليل والملاحظة التلقائية متوقفة، ولن تُرسل أي لقطة إلى نموذج أو جهة خارجية. هل تريد فتح المعاينة المحلية؟"
                : "The camera preview stays on this device. Image analysis is not yet available in Layal's native core; analysis and automatic-observation controls are disabled, and no frame is sent to a model or external service. Open the local preview?",
            IsArabic ? "موافقة على تشغيل الكاميرا" : "Confirm camera access",
            MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (consent != MessageBoxResult.Yes) return;
        if (IsCaptureBlockedByPrivacyShield())
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لم تُفتح الكاميرا."
                : "The privacy shield is active; the camera was not opened.");
            return;
        }

        var camera = new VisionCameraWindow(IsArabic) { Owner = this };
        camera.CameraStatusChanged += CameraVisionWindow_CameraStatusChanged;
        camera.AnalyzeRequested += CameraVisionWindow_AnalyzeRequested;
        camera.ScanSurroundingsRequested += CameraVisionWindow_ScanSurroundingsRequested;
        camera.ObserveRequested += CameraVisionWindow_ObserveRequested;
        camera.AutomaticObservationChanged += CameraVisionWindow_AutomaticObservationChanged;
        camera.Closed += CameraVisionWindow_Closed;
        _cameraVisionWindow = camera;
        UpdateCameraVisionIndicator();
        camera.Show();
    }

    private async void CameraVisionWindow_AnalyzeRequested(byte[] imageBytes)
    {
        if (AppRuntime.Settings.PrivacyShieldEnabled)
        {
            Array.Clear(imageBytes, 0, imageBytes.Length);
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ تم رفض إطار الكاميرا ولم يُرسل."
                : "Privacy shield is enabled; the camera frame was rejected and not sent.");
            return;
        }

        if (_chatCancellation is not null)
        {
            PrivacyShieldPolicy.ClearCapturedBuffer(imageBytes);
            SetChatActivityStatus(IsArabic ? "انتظر انتهاء الطلب الحالي قبل تحليل لقطة أخرى." : "Wait for the active request to finish before analyzing another frame.");
            return;
        }
        var prompt = ChatInput.Text.Trim();
        await AnalyzeVisionImageAsync(
            imageBytes,
            "image/jpeg",
            IsArabic ? "لقطة من الكاميرا" : "camera snapshot",
            prompt,
            automaticObservation: false,
            clearChatInput: !string.IsNullOrWhiteSpace(prompt),
            capturedSource: true);
    }

    private async void CameraVisionWindow_ScanSurroundingsRequested(byte[] imageBytes)
    {
        if (AppRuntime.Settings.PrivacyShieldEnabled)
        {
            Array.Clear(imageBytes, 0, imageBytes.Length);
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ تم رفض إطار الكاميرا ولم يُرسل."
                : "Privacy shield is enabled; the camera frame was rejected and not sent.");
            return;
        }

        if (_chatCancellation is not null)
        {
            PrivacyShieldPolicy.ClearCapturedBuffer(imageBytes);
            SetChatActivityStatus(IsArabic ? "انتظر انتهاء الطلب الحالي قبل مسح المكان." : "Wait for the current request to finish before scanning surroundings.");
            return;
        }

        var extraQuestion = ChatInput.Text.Trim();
        var prompt = IsArabic
            ? "صف المكان الظاهر في الصورة المحيطة بالكاميرا: الغرفة أو المساحة، الأشياء والأثاث، الأبواب والنوافذ والعلامات، وأي كاميرات أو أجهزة استشعار ظاهرة، والعلاقات المكانية الواضحة. فرّق بين ما تراه فعلًا وما لا يمكن الجزم به من لقطة واحدة، ولا تحدد هوية أي شخص أو تستنتج معلومات حساسة. إذا كانت الصورة مظلمة أو محجوبة أو غير قابلة للتحليل فاذكر ذلك بوضوح."
            : "Describe the scene visible around the camera: the room or area, objects and furniture, visible doors/windows/signs, any visibly present cameras or sensors, and clear spatial relationships. Distinguish visible evidence from what cannot be established from one still image; do not identify people or infer sensitive traits. If the image is dark, obstructed, or cannot be analyzed, say so explicitly.";
        if (!string.IsNullOrWhiteSpace(extraQuestion))
            prompt += (IsArabic ? "\nطلب إضافي: " : "\nAdditional question: ") + extraQuestion;

        await AnalyzeVisionImageAsync(
            imageBytes,
            "image/jpeg",
            IsArabic ? "مسح المكان حول الكاميرا" : "surroundings scan",
            prompt,
            automaticObservation: false,
            clearChatInput: !string.IsNullOrWhiteSpace(extraQuestion),
            capturedSource: true);
    }

    private async void CameraVisionWindow_ObserveRequested(byte[] imageBytes)
    {
        if (AppRuntime.Settings.PrivacyShieldEnabled)
        {
            Array.Clear(imageBytes, 0, imageBytes.Length);
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ تم رفض إطار الكاميرا ولم يُرسل."
                : "Privacy shield is enabled; the camera frame was rejected and not sent.");
            return;
        }

        // Automatic observation never interrupts a typed request or live voice session.
        if (_chatCancellation is not null || _voiceConversationCancellation is not null ||
            _cameraVisionWindow is not { IsVisible: true })
        {
            PrivacyShieldPolicy.ClearCapturedBuffer(imageBytes);
            return;
        }

        await AnalyzeVisionImageAsync(
            imageBytes,
            "image/jpeg",
            IsArabic ? "ملاحظة بصرية تلقائية" : "automatic camera observation",
            string.Empty,
            automaticObservation: true,
            capturedSource: true);
    }

    private void CameraVisionWindow_CameraStatusChanged(bool isRunning) => UpdateCameraVisionIndicator();

    private void CameraVisionWindow_AutomaticObservationChanged(bool enabled)
    {
        UpdateCameraVisionIndicator();
        SetChatActivityStatus(enabled
            ? (IsArabic ? "التحليل البصري التلقائي نشط: لقطة كل 10 ثوانٍ." : "Automatic visual observation is active: one still every 10 seconds.")
            : (IsArabic ? "التحليل البصري التلقائي متوقف." : "Automatic visual observation is off."));
    }

    private void CameraVisionWindow_Closed(object? sender, EventArgs e)
    {
        if (_cameraVisionWindow is not null)
        {
            _cameraVisionWindow.CameraStatusChanged -= CameraVisionWindow_CameraStatusChanged;
            _cameraVisionWindow.AnalyzeRequested -= CameraVisionWindow_AnalyzeRequested;
            _cameraVisionWindow.ScanSurroundingsRequested -= CameraVisionWindow_ScanSurroundingsRequested;
            _cameraVisionWindow.ObserveRequested -= CameraVisionWindow_ObserveRequested;
            _cameraVisionWindow.AutomaticObservationChanged -= CameraVisionWindow_AutomaticObservationChanged;
            _cameraVisionWindow.Closed -= CameraVisionWindow_Closed;
            _cameraVisionWindow = null;
        }
        UpdateCameraVisionIndicator();
        if (IsLoaded)
            SetChatActivityStatus(IsArabic ? "تم إيقاف الكاميرا." : "Camera stopped.");
    }

    private void UpdateCameraVisionIndicator()
    {
        if (CameraVisionIndicator is null) return;
        var active = _cameraVisionWindow is { CameraIsRunning: true };
        CameraVisionIndicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var automatic = active && _cameraVisionWindow?.IsLoaded == true &&
                        _cameraVisionWindow.FindName("ContinuousObservationCheckBox") is CheckBox checkBox &&
                        checkBox.IsChecked == true;
        CameraVisionDot.Fill = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        CameraVisionStatusText.Text = automatic
            ? (IsArabic ? "الكاميرا والتحليل التلقائي نشطان" : "Camera and auto-analysis active")
            : _cameraVisionWindow?.IsNetworkCamera == true
                ? (IsArabic ? "كاميرا الشبكة نشطة" : "Network camera active")
                : (IsArabic ? "كاميرا الكمبيوتر نشطة" : "Computer camera active");
        AutomationProperties.SetName(CameraVisionIndicator, automatic
            ? (IsArabic ? "الكاميرا والتحليل البصري التلقائي نشطان" : "Camera and automatic visual analysis are active")
            : (IsArabic ? "الكاميرا نشطة" : "Camera active"));
    }

    private Task AnalyzeVisionImageAsync(
        byte[] imageBytes,
        string contentType,
        string fileName,
        string prompt,
        bool automaticObservation,
        bool consumePendingAttachment = false,
        bool clearChatInput = false,
        bool capturedSource = false)
    {
        PrivacyShieldPolicy.ClearCapturedBuffer(imageBytes);
        if (consumePendingAttachment)
        {
            PrivacyShieldPolicy.ClearCapturedBuffer(_pendingVisionImageBytes);
            _pendingVisionImageBytes = null;
            _pendingVisionContentType = null;
            _pendingVisionFileName = null;
            UpdateVisionAttachmentControls();
        }
        if (automaticObservation)
            _cameraVisionWindow?.StopAutomaticObservation();
        else
            AddMessage("status", IsArabic
                ? "الرؤية الحاسوبية لم تُنفّذ بعد داخل نواة ليال المستقلة. لم يتم إرسال الصورة إلى أي نموذج."
                : "Computer vision is not yet implemented in Layal's independent core. The image was not sent to a model.");
        SetChatActivityStatus(IsArabic
            ? "تحليل الصور غير متاح في النواة المستقلة الحالية."
            : "Image analysis is unavailable in the current independent core.");
        return Task.CompletedTask;
    }

    private void ChatSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var prompt = button.Tag?.ToString() switch
        {
            "plan" => IsArabic
                ? "ساعدني في وضع خطة واضحة وقابلة للتنفيذ للهدف التالي: "
                : "Help me create a clear, actionable plan for this goal: ",
            "summary" => IsArabic
                ? "لخّص المحتوى التالي في نقاط واضحة، مع إبراز أهم النتائج: "
                : "Summarize the following content into clear points and highlight the key takeaways: ",
            "ideas" => IsArabic
                ? "اقترح أفكارًا عملية ومبتكرة بخصوص: "
                : "Suggest practical and creative ideas for: ",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(prompt))
            return;

        ChatInput.Text = prompt;
        ChatInput.Focus();
        ChatInput.CaretIndex = ChatInput.Text.Length;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async Task SendMessageAsync(bool suppressVoiceReply = false)
    {
        if (_voiceConversationCancellation is not null)
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف التسجيل الصوتي قبل إرسال رسالة نصية."
                : "Stop the voice recording before sending a text message.");
            return;
        }

        var text = ChatInput.Text.Trim();
        if (_chatCancellation is not null) return;
        var documentText = _pendingDocumentText;
        var documentName = _pendingDocumentFileName ?? "document";

        if (_pendingVisionImageBytes is { Length: > 0 })
        {
            SetChatActivityStatus(IsArabic
                ? "تحليل الصور لم يُنقل بعد إلى نواة ليال المستقلة. لم يتم إرسال الصورة إلى أي نموذج."
                : "Image understanding has not yet been moved into Layal's independent core. The image was not sent to any model.");
            return;
        }

        if (documentText is { Length: > 0 } && string.IsNullOrWhiteSpace(text))
            text = IsArabic
                ? "لخص الملف المرفق بأهم الجمل، أو ابحث داخله عن معلومات مرتبطة بسؤالي."
                : "Summarize the attached document using its key sentences, or find passages relevant to my question.";

        if (string.IsNullOrWhiteSpace(text)) return;

        ChatInput.Clear();
        AddMessage("user", text);
        if (documentText is { Length: > 0 })
        {
            _pendingDocumentText = null;
            _pendingDocumentFileName = null;
            _pendingDocumentNotice = null;
            UpdateDocumentAttachmentUi();
        }

        _chatCancellation = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        CancelResponseButton.IsEnabled = true;
        SetChatActivityStatus(IsArabic ? "ليال تفكر بالنواة المحلية..." : "Layal is reasoning with her native local core...");
        AppRuntime.MascotWindow?.SetMood("thinking");

        try
        {
            var response = await Task.Run(
                () => AppRuntime.NativeConversationEngine.Respond(
                    text,
                    AppRuntime.Settings.Language,
                    documentText,
                    documentName),
                _chatCancellation.Token);

            AddMessage("assistant", response);
            AppRuntime.MascotWindow?.SetMood(SelectChatMood(text, response));
            var completionStatus = IsArabic
                ? "اكتمل الرد من نواة ليال المحلية؛ لم يتم استدعاء نموذج ذكاء اصطناعي."
                : "Native local reply completed; no AI model was called.";
            if (!suppressVoiceReply && AppRuntime.Settings.VoiceRepliesEnabled)
            {
                if (_localVoiceReady)
                {
                    try
                    {
                        await _localVoiceService.SpeakAsync(
                            response,
                            _chatCancellation.Token,
                            AppRuntime.Settings.AudioOutputDeviceId,
                            AppRuntime.Settings.VoiceDeliveryStyleId);
                        completionStatus = IsArabic
                            ? "اكتمل الرد المحلي ونطقه بصوت Piper المحلي."
                            : "Native reply completed and spoken with local Piper speech.";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception speechError)
                    {
                        completionStatus += (IsArabic
                            ? " تعذر النطق المحلي: "
                            : " Local speech playback failed: ") + speechError.Message;
                    }
                }
                else
                {
                    completionStatus += IsArabic
                        ? " موارد الصوت غير مجهزة؛ يمكنك تجهيز Whisper/Piper اختياريًا من الإعدادات."
                        : " Speech resources are not set up; you can optionally install Whisper/Piper from Settings.";
                }
            }
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
            AddMessage("status", (IsArabic ? "تعذر على نواة ليال معالجة الرسالة: " : "Layal's native core could not process the message: ") + ex.Message);
            AppRuntime.MascotWindow?.SetMood("error");
            SetChatActivityStatus(IsArabic ? "فشل الطلب المحلي" : "Local request failed");
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
        while (ChatMessages.Count > ChatSessionService.MaxMessagesPerConversation) ChatMessages.RemoveAt(0);
        SaveCurrentChatMessages();
        ChatMessagesList.ScrollIntoView(ChatMessages.LastOrDefault());
        return message;
    }

    private void SaveCurrentChatMessages()
    {
        if (_incognitoMode)
            return;

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
        if (sender is not ContextMenu menu) return;
        menu.FlowDirection = IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault() is { } copyItem)
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
        if (_incognitoMode)
        {
            SetChatHistoryPanelOpen(false);
            try
            {
                ChatConversations.Clear();
                ChatHistorySelector.SelectedItem = null;
                ChatHistoryList.SelectedItem = null;
                ChatHistorySelector.IsEnabled = false;
                ChatHistoryList.IsEnabled = false;
            }
            finally
            {
                _refreshingChatSessions = false;
            }
            return;
        }
        try
        {
            ChatConversations.Clear();
            foreach (var conversation in AppRuntime.ChatSessionService.GetAll())
                ChatConversations.Add(conversation);
            var id = string.IsNullOrWhiteSpace(selectedId) ? _currentConversationId : selectedId;
            var selectedConversation = ChatConversations.FirstOrDefault(c => c.Id == id)
                ?? ChatConversations.FirstOrDefault();
            ChatHistorySelector.SelectedItem = selectedConversation;
            ChatHistoryList.SelectedItem = selectedConversation;
            ChatHistoryList.IsEnabled = !_incognitoMode;
        }
        finally { _refreshingChatSessions = false; }
    }

    private void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_incognitoMode)
        {
            SetChatActivityStatus(IsArabic ? "أنهِ المحادثة الخاصة ثم ابدأ محادثة محفوظة جديدة." : "Exit private chat before creating a saved conversation.");
            return;
        }
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
        if (_incognitoMode)
        {
            SetChatActivityStatus(IsArabic ? "مسح السجل المحفوظ غير متاح أثناء المحادثة الخاصة." : "Clearing saved history is unavailable during private chat.");
            return;
        }
        if (_chatCancellation is not null)
        {
            SetChatActivityStatus(IsArabic ? "أوقف الرد الحالي قبل مسح السجل." : "Cancel the active response before clearing history.");
            return;
        }

        var answer = LayalDialogService.Show(this,
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
        var selected = sender switch
        {
            ComboBox selector => selector.SelectedItem as ChatConversation,
            ListBox list => list.SelectedItem as ChatConversation,
            _ => null
        };

        if (sender is ListBox && !_refreshingChatSessions && !_incognitoMode)
        {
            _refreshingChatSessions = true;
            try { ChatHistorySelector.SelectedItem = selected; }
            finally { _refreshingChatSessions = false; }
        }

        if (_incognitoMode)
        {
            if (sender is ComboBox)
                ChatHistorySelector.SelectedItem = null;
            else if (sender is ListBox)
                ChatHistoryList.SelectedItem = null;
            return;
        }
        if (_refreshingChatSessions || selected is null)
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

    private void CancelResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_documentExtractionCancellation is not null)
        {
            _documentExtractionCancellation.Cancel();
            return;
        }
        _chatCancellation?.Cancel();
    }

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

}
