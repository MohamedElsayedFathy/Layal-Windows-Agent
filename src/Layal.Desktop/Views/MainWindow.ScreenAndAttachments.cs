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
    private async void InspectHoveredElementButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppRuntime.IsExiting || AppRuntime.Settings.PrivacyShieldEnabled)
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل أو التطبيق يُغلق؛ فحص العناصر محظور."
                : "The privacy shield is enabled or Layal is exiting; UI element inspection is blocked.");
            return;
        }
        if (_hoveredElementInspectionCancellation is not null)
        {
            SetChatActivityStatus(IsArabic
                ? "فحص عنصر جارٍ بالفعل؛ انتظر اكتماله أو فعّل درع الخصوصية لإلغائه."
                : "An element inspection is already in progress; wait for it or enable the privacy shield to cancel it.");
            return;
        }

        var consent = LayalDialogService.Show(this,
            IsArabic
                ? "بعد موافقتك ستختفي نافذة ليال والماسكوت لمدة 5 ثوانٍ. حرّك المؤشر إلى العنصر المطلوب وثبّته عليه حتى تنتهي المهلة. ستُقرأ بيانات UI Automation محدودة فقط: النوع، ومعرّف الواجهة، والحدود، والحالة؛ لا يُقرأ اسم حقول الإدخال أو النصوص أو العناصر التي قد تحمل بيانات، ولا قيمة أي حقل. عناصر كلمات المرور تُرفض. لا تُلتقط صورة ولا تُنفذ نقرة أو كتابة. ستُضاف النتيجة إلى مسودة الشات ولن تُرسل إلا بعد ضغط إرسال. هل تريد المتابعة؟"
                : "After you agree, both Layal's main window and mascot will hide for 5 seconds. Move the pointer to the target element and keep it there until the countdown ends. Only bounded UI Automation metadata is read: control type, AutomationId, bounds, and enabled state. Names of editable/text/data elements and all field values are deliberately not read; password controls are rejected. No screenshot, click, or keystroke occurs. The result is added to the chat draft and is not sent until you press Send. Continue?",
            IsArabic ? "موافقة على فحص عنصر" : "Confirm UI element inspection",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (consent != MessageBoxResult.Yes)
            return;

        using var inspectionCancellation = new CancellationTokenSource();
        _hoveredElementInspectionCancellation = inspectionCancellation;
        InspectHoveredElementButton.IsEnabled = false;
        var previousWindowState = WindowState;
        var wasVisible = IsVisible;
        var mascotWindow = AppRuntime.MascotWindow;
        var mascotWasVisible = mascotWindow?.IsVisible == true;
        try
        {
            SetChatActivityStatus(IsArabic
                ? "ستختفي ليال والماسكوت الآن. وجّه المؤشر إلى العنصر المطلوب خلال 5 ثوانٍ."
                : "Layal and the mascot will hide now. Point at the target element within 5 seconds.");
            Hide();
            if (mascotWasVisible)
                mascotWindow!.Hide();

            await Task.Delay(TimeSpan.FromSeconds(5), inspectionCancellation.Token);
            inspectionCancellation.Token.ThrowIfCancellationRequested();

            if (AppRuntime.IsExiting)
                return;
            if (AppRuntime.Settings.PrivacyShieldEnabled)
                throw new OperationCanceledException(inspectionCancellation.Token);

            var cursor = System.Windows.Forms.Cursor.Position;
            var inspection = await WindowsUiAutomationService.InspectElementAtPointAsync(
                cursor.X, cursor.Y, Environment.ProcessId, inspectionCancellation.Token);
            inspectionCancellation.Token.ThrowIfCancellationRequested();

            // Re-check after the asynchronous UIA call so enabling the shield during inspection
            // cannot disclose even metadata to the local chat draft.
            if (AppRuntime.IsExiting)
                return;
            if (AppRuntime.Settings.PrivacyShieldEnabled)
                throw new OperationCanceledException(inspectionCancellation.Token);

            var details = (IsArabic
                ? "اشرح لي وظيفة هذا العنصر وكيف أستخدمه.\n"
                : "Explain what this element does and how I can use it.\n") +
                WindowsUiAutomationService.FormatHoveredElementDraft(inspection, IsArabic);
            ChatInput.Text = string.IsNullOrWhiteSpace(ChatInput.Text)
                ? details
                : ChatInput.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + details;
            ChatInput.CaretIndex = ChatInput.Text.Length;
            SetChatActivityStatus(IsArabic
                ? "تمت إضافة بيانات العنصر والسؤال إلى مسودة الشات المحلية؛ لم يتم الإرسال."
                : "Element metadata and a question were added to the local chat draft; nothing was sent.");
        }
        catch (OperationCanceledException) when (inspectionCancellation.IsCancellationRequested)
        {
            if (!AppRuntime.IsExiting)
                SetChatActivityStatus(IsArabic
                    ? "تم إلغاء فحص العنصر؛ لم تُضف بيانات إلى المسودة."
                    : "Element inspection was cancelled; nothing was added to the draft.");
        }
        catch (Exception ex)
        {
            if (!AppRuntime.IsExiting)
                SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                    "تعذر فحص عنصر الواجهة", "Could not inspect the UI element", ex, IsArabic));
        }
        finally
        {
            if (ReferenceEquals(_hoveredElementInspectionCancellation, inspectionCancellation))
                _hoveredElementInspectionCancellation = null;
            if (!AppRuntime.IsExiting && IsLoaded)
            {
                UpdatePrivacyShieldUi();
                if (wasVisible && !IsVisible)
                    Show();
                WindowState = previousWindowState;

                if (mascotWasVisible && mascotWindow is not null &&
                    mascotWindow.IsLoaded && !mascotWindow.IsVisible)
                    mascotWindow.Show();

                if (wasVisible)
                {
                    Activate();
                    ChatInput.Focus();
                }
            }
        }
    }

    private async void CaptureScreenRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppRuntime.Settings.PrivacyShieldEnabled)
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ قص الشاشة محظور. عطّل الدرع أولًا إذا كنت تريد التقاطًا بموافقة صريحة."
                : "Privacy shield is enabled; screen cropping is blocked. Disable the shield first to capture with explicit consent.");
            return;
        }

        if (_pendingDocumentText is { Length: > 0 })
        {
            SetChatActivityStatus(IsArabic
                ? "أزل الملف النصي المرفق أولًا؛ يمكن إرفاق صورة أو ملف نصي واحد في كل مرة."
                : "Remove the attached document first; attach one image or one text document at a time.");
            return;
        }

        if (_chatCancellation is not null || _agentTaskCancellation is not null ||
            _voiceConversationCancellation is not null || _speechCancellation is not null ||
            _voiceTestCancellation is not null || _localAiSetupCancellation is not null ||
            _screenCaptureCancellation is not null ||
            _localVoiceRecordingActive || _microphoneCapture.IsRecording || _speechIsStopping ||
            _cameraVisionWindow is { IsVisible: true } || _quickAskWindow is { IsVisible: true })
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف العمليات النشطة وأغلق نافذة السؤال السريع أو الكاميرا قبل التقاط الشاشة."
                : "Stop active work and close Quick Ask or the camera before capturing the screen.");
            return;
        }

        var consent = LayalDialogService.Show(this,
            IsArabic
                ? "ستُلتقط الشاشة الموجودة أسفل المؤشر بعد إخفاء ليال مؤقتًا وتُعرض معاينة محلية لقص المنطقة المطلوبة. الرؤية المستقلة غير متاحة حاليًا؛ لن تُرسل اللقطة إلى أي نموذج. راجع المنطقة المختارة بحثًا عن بيانات حساسة قبل الاحتفاظ بها محليًا. هل تريد المتابعة؟"
                : "Layal will temporarily hide and capture the display under the pointer for a local crop preview. Independent vision is unavailable; the capture will not be sent to any model. Review the selected region for sensitive data before keeping it locally. Continue?",
            IsArabic ? "موافقة على التقاط الشاشة" : "Confirm screen capture",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (consent != MessageBoxResult.Yes)
            return;

        // The shield may have been enabled while the consent dialog was open.
        if (AppRuntime.Settings.PrivacyShieldEnabled)
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ لم يتم التقاط الشاشة."
                : "Privacy shield is enabled; no screen image was captured.");
            return;
        }

        var previousWindowState = WindowState;
        var wasVisible = IsVisible;
        var captureCancellation = new CancellationTokenSource();
        _screenCaptureCancellation = captureCancellation;
        CaptureScreenRegionButton.IsEnabled = false;
        try
        {
            Hide();
            // Let Windows expose the app beneath Layal before copying pixels.
            await Task.Delay(250, captureCancellation.Token);

            var capture = _screenRegionCaptureService.CaptureRegionUnderCursor(
                IsArabic,
                captureCancellation.Token);
            if (capture is null)
            {
                SetChatActivityStatus(IsArabic ? "تم إلغاء قص الشاشة؛ لم يتم إرفاق أي صورة." : "Screen crop cancelled; no image was attached.");
                return;
            }

            // Recheck after the modal preview: the privacy shield may have cancelled capture
            // while the user was selecting a crop. Do not leave even a pending attachment.
            if (captureCancellation.IsCancellationRequested || AppRuntime.Settings.PrivacyShieldEnabled)
            {
                Array.Clear(capture.PngBytes, 0, capture.PngBytes.Length);
                SetChatActivityStatus(IsArabic
                    ? "أوقف درع الخصوصية التقاط الشاشة؛ لم يتم إرفاق أي صورة."
                    : "The privacy shield stopped screen capture; no image was attached.");
                return;
            }

            _pendingVisionImageBytes = capture.PngBytes;
            _pendingVisionContentType = "image/png";
            _pendingVisionFileName = $"screen-region-{capture.Width}x{capture.Height}.png";
            UpdateVisionAttachmentControls();
            SetChatActivityStatus(IsArabic
                ? $"تم إرفاق قصاصة شاشة ({capture.Width} × {capture.Height} بكسل) بعد المعاينة. لم تُرسل؛ اكتب سؤالك واضغط إرسال."
                : $"Screen crop attached ({capture.Width} × {capture.Height} pixels) after preview. It has not been sent; ask a question and press Send.");
            ChatInput.Focus();
        }
        catch (OperationCanceledException) when (captureCancellation.IsCancellationRequested)
        {
            SetChatActivityStatus(IsArabic
                ? (AppRuntime.Settings.PrivacyShieldEnabled
                    ? "درع الخصوصية ألغى التقاط الشاشة؛ لم يتم إرفاق أو إرسال أي صورة."
                    : "تم إلغاء التقاط الشاشة؛ لم يتم إرفاق أو إرسال أي صورة.")
                : (AppRuntime.Settings.PrivacyShieldEnabled
                    ? "The privacy shield cancelled screen capture; no image was attached or sent."
                    : "Screen capture was cancelled; no image was attached or sent."));
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر التقاط الشاشة", "Screen capture failed", ex, IsArabic));
        }
        finally
        {
            if (ReferenceEquals(_screenCaptureCancellation, captureCancellation))
                _screenCaptureCancellation = null;
            captureCancellation.Dispose();

            if (wasVisible && !IsVisible)
                Show();
            WindowState = previousWindowState;
            if (wasVisible)
            {
                Activate();
                ChatInput.Focus();
            }
            UpdateVisionAttachmentControls();
        }
    }

    private CancellationTokenSource? TryBeginClipboardRead(bool image)
    {
        if (_clipboardReadCancellation is not null)
        {
            SetChatActivityStatus(IsArabic
                ? "قراءة الحافظة جارية بالفعل؛ انتظر اكتمالها."
                : "A clipboard read is already in progress; wait for it to finish.");
            return null;
        }

        if (AppRuntime.Settings.PrivacyShieldEnabled)
        {
            SetChatActivityStatus(IsArabic
                ? "درع الخصوصية مفعّل؛ قراءة الحافظة محظورة."
                : "Privacy shield is enabled; clipboard reads are blocked.");
            return null;
        }

        if (_chatCancellation is not null || _agentTaskCancellation is not null ||
            _voiceConversationCancellation is not null || _speechCancellation is not null ||
            _voiceTestCancellation is not null || _localAiSetupCancellation is not null ||
            _documentExtractionCancellation is not null ||
            _localVoiceRecordingActive || _microphoneCapture.IsRecording || _speechIsStopping ||
            _cameraVisionWindow is { IsVisible: true } || _quickAskWindow is { IsVisible: true })
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف العمليات النشطة وأغلق نافذة السؤال السريع أو الكاميرا قبل قراءة الحافظة."
                : "Stop active work and close Quick Ask or the camera before reading the clipboard.");
            return null;
        }

        if (_pendingVisionImageBytes is { Length: > 0 } || !string.IsNullOrWhiteSpace(_pendingDocumentText))
        {
            SetChatActivityStatus(image
                ? (IsArabic ? "أزل المرفق الحالي أولًا قبل إرفاق صورة الحافظة." : "Remove the current attachment before attaching a clipboard image.")
                : (IsArabic ? "أزل المرفق الحالي أولًا قبل إرفاق نص الحافظة." : "Remove the current attachment before attaching clipboard text."));
            return null;
        }

        _clipboardReadCancellation = new CancellationTokenSource();
        UpdateClipboardReadButtons();
        return _clipboardReadCancellation;
    }

    private void CompleteClipboardRead(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_clipboardReadCancellation, cancellation))
            _clipboardReadCancellation = null;
        cancellation.Dispose();
        UpdateClipboardReadButtons();
    }

    private void UpdateClipboardReadButtons()
    {
        if (ReadClipboardButton is null || PasteClipboardImageButton is null)
            return;

        var operationActive = _clipboardReadCancellation is not null ||
            _chatCancellation is not null || _agentTaskCancellation is not null ||
            _voiceConversationCancellation is not null || _speechCancellation is not null ||
            _voiceTestCancellation is not null || _localAiSetupCancellation is not null ||
            _documentExtractionCancellation is not null ||
            _localVoiceRecordingActive || _microphoneCapture.IsRecording || _speechIsStopping ||
            _cameraVisionWindow is { IsVisible: true } || _quickAskWindow is { IsVisible: true };
        var hasAttachment = _pendingVisionImageBytes is { Length: > 0 } ||
            !string.IsNullOrWhiteSpace(_pendingDocumentText);
        var canRead = !operationActive && !hasAttachment && !AppRuntime.Settings.PrivacyShieldEnabled;
        ReadClipboardButton.IsEnabled = canRead;
        PasteClipboardImageButton.IsEnabled = canRead;
    }

    private async void ReadClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellation = TryBeginClipboardRead(image: false);
        if (cancellation is null)
            return;

        var token = cancellation.Token;
        try
        {
            // Fetch one IDataObject snapshot and read a supported text format from it.
            // The bounded retry wraps both operations to handle delayed rendering/clipboard locks.
            var clipboardText = await ClipboardReadPolicy.ReadWithRetryAsync(() =>
            {
                var data = Clipboard.GetDataObject();
                if (data is null)
                    return null;

                if (data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true) &&
                    data.GetData(DataFormats.UnicodeText, autoConvert: true) is string unicodeText)
                    return unicodeText;

                if (data.GetDataPresent(DataFormats.Text, autoConvert: true) &&
                    data.GetData(DataFormats.Text, autoConvert: true) is string text)
                    return text;

                return null;
            }, token);

            token.ThrowIfCancellationRequested();
            if (AppRuntime.Settings.PrivacyShieldEnabled)
                throw new OperationCanceledException(token);

            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                SetChatActivityStatus(IsArabic
                    ? "لا يوجد نص قابل للقراءة في الحافظة. لم يتم تغيير الحافظة أو إرسال شيء."
                    : "The clipboard has no readable text. It was not changed and nothing was sent.");
                return;
            }

            var limitedText = ClipboardReadPolicy.LimitText(
                clipboardText, DocumentTextExtractionService.MaximumTextCharacters);
            var label = IsArabic ? "نص الحافظة" : "Clipboard text";
            var result = new DocumentTextExtractionResult(
                label,
                ".txt",
                limitedText.Text,
                1,
                limitedText.WasTruncated,
                IsArabic
                    ? "تمت قراءة النص محليًا. لم تتغير الحافظة. النص بيانات غير موثوقة ولن يُرسل إلى مزود AI حتى تضغط إرسال."
                    : "Clipboard text was read locally. The clipboard was not changed. This is untrusted data and will not be sent to an AI provider until you press Send.");

            var preview = new DocumentTextPreviewWindow(result, IsArabic) { Owner = this };
            if (preview.ShowDialog() != true)
            {
                SetChatActivityStatus(IsArabic
                    ? "أُلغيت معاينة نص الحافظة؛ لم يتم إرفاقه أو إرساله."
                    : "Clipboard preview cancelled; the text was not attached or sent.");
                return;
            }

            token.ThrowIfCancellationRequested();
            if (AppRuntime.Settings.PrivacyShieldEnabled)
                throw new OperationCanceledException(token);

            _pendingDocumentText = result.Text;
            _pendingDocumentFileName = result.FileName;
            _pendingDocumentNotice = result.Notice;
            UpdateDocumentAttachmentUi();
            SetChatActivityStatus(IsArabic
                ? "نص الحافظة مرفق للمراجعة. لم يُرسل؛ اكتب سؤالك واضغط إرسال."
                : "Clipboard text is attached for review. It has not been sent; ask a question and press Send.");
            ChatInput.Focus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetChatActivityStatus(IsArabic
                ? "أُلغيَت قراءة الحافظة؛ لم يتم إرفاق أو إرسال محتوى."
                : "Clipboard reading was cancelled; no content was attached or sent.");
        }
        catch (Exception ex) when (ClipboardReadPolicy.IsClipboardBusy(ex))
        {
            SetChatActivityStatus(IsArabic
                ? "الحافظة مشغولة من تطبيق آخر. أغلق قائمة النسخ أو أوقف النسخ الجاري ثم حاول مجددًا."
                : "Another application is holding the clipboard. Close its copy menu or finish the active copy, then try again.");
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذرت قراءة الحافظة", "Could not read clipboard", ex, IsArabic));
        }
        finally
        {
            CompleteClipboardRead(cancellation);
        }
    }

    private async void PasteClipboardImageButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellation = TryBeginClipboardRead(image: true);
        if (cancellation is null)
            return;

        var token = cancellation.Token;
        try
        {
            var consent = LayalDialogService.Show(this,
                IsArabic
                    ? "ستُقرأ صورة الحافظة الحالية بعد موافقتك وتُحوّل إلى PNG في الذاكرة للمعاينة المحلية فقط. قد تحتوي على بيانات حساسة؛ راجعها قبل الاحتفاظ بها. لن تتغير الحافظة ولن تُرسل الصورة إلى أي نموذج؛ تحليل الصور المستقل غير متاح حاليًا. هل تريد المتابعة؟"
                    : "With your consent, the current clipboard image will be read and encoded as PNG in memory for local preview only. It may contain sensitive data; review it before keeping it. The clipboard will not be modified and the image will not be sent to any model; independent vision is not available yet. Continue?",
                IsArabic ? "موافقة على قراءة صورة الحافظة" : "Confirm clipboard image read",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (consent != MessageBoxResult.Yes)
            {
                SetChatActivityStatus(IsArabic
                    ? "تم إلغاء قراءة صورة الحافظة؛ لم يُقرأ أي محتوى."
                    : "Clipboard image read cancelled; no image was read.");
                return;
            }

            var image = await ClipboardReadPolicy.ReadWithRetryAsync(Clipboard.GetImage, token);
            token.ThrowIfCancellationRequested();
            if (AppRuntime.Settings.PrivacyShieldEnabled)
                throw new OperationCanceledException(token);

            if (image is null)
            {
                SetChatActivityStatus(IsArabic
                    ? "لا توجد صورة قابلة للقراءة في الحافظة."
                    : "The clipboard does not contain a readable image.");
                return;
            }

            var pngBytes = ClipboardImageAttachmentEncoder.EncodePng(image);
            token.ThrowIfCancellationRequested();
            if (AppRuntime.Settings.PrivacyShieldEnabled)
            {
                Array.Clear(pngBytes, 0, pngBytes.Length);
                throw new OperationCanceledException(token);
            }

            _pendingVisionImageBytes = pngBytes;
            _pendingVisionContentType = "image/png";
            _pendingVisionFileName = $"clipboard-image-{image.PixelWidth}x{image.PixelHeight}.png";
            UpdateVisionAttachmentControls();
            SetChatActivityStatus(IsArabic
                ? $"صورة الحافظة ({image.PixelWidth} × {image.PixelHeight} بكسل) جاهزة للمعاينة المحلية فقط. لن تُرسل إلى أي نموذج؛ تحليل الصور غير متاح."
                : $"Clipboard image ({image.PixelWidth} × {image.PixelHeight} pixels) is ready for local preview only. It will not be sent to any model; image analysis is unavailable.");
            ChatInput.Focus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetChatActivityStatus(IsArabic
                ? "أُلغيَت قراءة صورة الحافظة؛ لم يتم إرفاق أو إرسال أي صورة."
                : "Clipboard image reading was cancelled; no image was attached or sent.");
        }
        catch (Exception ex) when (ClipboardReadPolicy.IsClipboardBusy(ex))
        {
            SetChatActivityStatus(IsArabic
                ? "الحافظة مشغولة من تطبيق آخر. أغلق قائمة النسخ أو أوقف النسخ الجاري ثم حاول مجددًا."
                : "Another application is holding the clipboard. Close its copy menu or finish the active copy, then try again.");
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذرت قراءة صورة الحافظة", "Could not read clipboard image", ex, IsArabic));
        }
        finally
        {
            CompleteClipboardRead(cancellation);
        }
    }

    private async void ChooseDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null || _agentTaskCancellation is not null ||
            _voiceConversationCancellation is not null || _speechCancellation is not null ||
            _voiceTestCancellation is not null || _localAiSetupCancellation is not null ||
            _localVoiceRecordingActive || _microphoneCapture.IsRecording || _speechIsStopping ||
            _cameraVisionWindow is { IsVisible: true } || _quickAskWindow is { IsVisible: true } ||
            _documentExtractionCancellation is not null)
        {
            SetChatActivityStatus(IsArabic
                ? "أوقف العمليات النشطة وأغلق نافذة السؤال السريع أو الكاميرا قبل اختيار ملف."
                : "Stop active work and close Quick Ask or the camera before selecting a document.");
            return;
        }

        if (_pendingVisionImageBytes is { Length: > 0 })
        {
            SetChatActivityStatus(IsArabic
                ? "أزل الصورة المرفقة أولًا؛ يمكن إرفاق صورة أو ملف نصي واحد في كل مرة."
                : "Remove the attached image first; attach one image or one text document at a time.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = IsArabic ? "اختيار صورة أو PDF أو ملف لمراجعته قبل الإرفاق" : "Choose an image, PDF, or document to review",
            Filter = IsArabic
                ? "صور وملفات مدعومة (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.htm;*.docx;*.xlsx)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.htm;*.docx;*.xlsx|كل الملفات (*.*)|*.*"
                : "Images and documents (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.htm;*.docx;*.xlsx)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.htm;*.docx;*.xlsx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        _documentExtractionCancellation = new CancellationTokenSource();
        ChooseDocumentButton.IsEnabled = false;
        ChooseImageButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        CancelResponseButton.IsEnabled = true;
        AutomationProperties.SetHelpText(ChooseDocumentButton, IsArabic
            ? "يجري استخراج النص محليًا. اضغط إيقاف لإلغاء العملية؛ لن يُرسل الملف تلقائيًا."
            : "Text is being extracted locally. Press Stop to cancel; the file is never sent automatically.");
        SetChatActivityStatus(IsArabic
            ? "جارٍ استخراج النص محليًا بمحرك OCR المضمّن للصور وصفحات PDF الممسوحة. اضغط إيقاف للإلغاء؛ لن يُرسل شيء."
            : "Extracting text locally with the bundled OCR engine for images and scanned PDF pages. Press Stop to cancel; nothing will be sent.");

        try
        {
            var extraction = await _documentTextExtractionService.ExtractFileAsync(
                dialog.FileName,
                _documentExtractionCancellation.Token);
            _documentExtractionCancellation.Token.ThrowIfCancellationRequested();

            var preview = new DocumentTextPreviewWindow(extraction, IsArabic) { Owner = this };
            if (preview.ShowDialog() != true)
            {
                SetChatActivityStatus(IsArabic ? "تم إلغاء إرفاق الملف؛ لم يُرسل شيء." : "Document attachment cancelled; nothing was sent.");
                return;
            }

            _documentExtractionCancellation.Token.ThrowIfCancellationRequested();
            if (IsCaptureBlockedByPrivacyShield())
            {
                SetChatActivityStatus(IsArabic
                    ? "تم إلغاء استخراج الملف؛ درع الخصوصية منع إرفاق النتيجة."
                    : "Document extraction was cancelled; the privacy shield prevented attaching its result.");
                return;
            }

            _pendingDocumentText = extraction.Text;
            _pendingDocumentFileName = extraction.FileName;
            _pendingDocumentNotice = extraction.Notice;
            UpdateDocumentAttachmentUi();
            SetChatActivityStatus(IsArabic
                ? $"تم استخراج النص محليًا من {extraction.FileName} وإرفاقه بعد المراجعة. لم يُرسل؛ راجع السؤال واضغط إرسال."
                : $"Text from {extraction.FileName} was extracted locally and attached after review. It has not been sent; review the prompt and press Send.");
            ChatInput.Focus();
        }
        catch (OperationCanceledException)
        {
            SetChatActivityStatus(IsArabic
                ? "تم إلغاء استخراج الملف؛ لم يُرفق ولم يُرسل أي محتوى."
                : "Document extraction was cancelled; no content was attached or sent.");
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError(
                "تعذر قراءة الصورة أو الملف", "Could not read image or document", ex, IsArabic));
        }
        finally
        {
            _documentExtractionCancellation?.Dispose();
            _documentExtractionCancellation = null;
            SendButton.IsEnabled = _chatCancellation is null;
            CancelResponseButton.IsEnabled = _chatCancellation is not null;
            UpdateDocumentAttachmentUi();
            UpdateVisionAttachmentControls();
        }
    }

    private void ClearDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingDocumentText = null;
        _pendingDocumentFileName = null;
        _pendingDocumentNotice = null;
        UpdateDocumentAttachmentUi();
        SetChatActivityStatus(IsArabic ? "تمت إزالة الملف المرفق محليًا؛ لم يتم إرسال محتواه." : "The local document attachment was removed; its content was not sent.");
    }

    private void UpdateDocumentAttachmentUi()
    {
        if (DocumentAttachmentStatus is null) return;
        var hasDocument = !string.IsNullOrWhiteSpace(_pendingDocumentText);
        var hasImage = _pendingVisionImageBytes is { Length: > 0 };
        ChooseDocumentButton.IsEnabled = _chatCancellation is null && _documentExtractionCancellation is null && !hasImage;
        ChooseImageButton.IsEnabled = _chatCancellation is null && _documentExtractionCancellation is null && !hasDocument;
        CaptureScreenRegionButton.IsEnabled = !AppRuntime.Settings.PrivacyShieldEnabled && _chatCancellation is null && _documentExtractionCancellation is null && !hasDocument;
        ClearDocumentButton.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        ClearDocumentButton.IsEnabled = _chatCancellation is null;
        UpdateClipboardReadButtons();
        DocumentAttachmentStatus.Text = hasDocument
            ? (IsArabic ? "جاهز للمراجعة والإرسال: " : "Ready for review and send: ") + (_pendingDocumentFileName ?? (IsArabic ? "ملف" : "document"))
            : (IsArabic ? "لا يوجد ملف مرفق" : "No document attached");
        AutomationProperties.SetHelpText(DocumentAttachmentStatus, hasDocument
            ? (_pendingDocumentNotice ?? string.Empty) + (IsArabic
                ? " لن يُرسل المحتوى إلا بعد ضغط إرسال."
                : " Content is not sent until you press Send.")
            : (IsArabic ? "لا يوجد محتوى ملف ينتظر الإرسال." : "No document content is queued for sending."));
    }

    private void ChooseImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDocumentText is { Length: > 0 })
        {
            SetChatActivityStatus(IsArabic
                ? "أزل الملف النصي المرفق أولًا؛ يمكن إرفاق صورة أو ملف نصي واحد في كل مرة."
                : "Remove the attached document first; attach one image or one text document at a time.");
            return;
        }

        if (_chatCancellation is not null)
        {
            SetChatActivityStatus(IsArabic ? "أوقف الطلب الحالي قبل تغيير الصورة." : "Cancel the active request before changing the image.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = IsArabic ? "اختيار صورة أو لقطة شاشة" : "Choose an image or screenshot",
            Filter = IsArabic
                ? "صور مدعومة (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|كل الملفات (*.*)|*.*"
                : "Supported images (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length <= 0 || info.Length > 10 * 1024 * 1024)
            {
                SetChatActivityStatus(IsArabic
                    ? "حجم الصورة يجب أن يكون أكبر من صفر وألا يتجاوز 10 ميجابايت."
                    : "The image must be larger than zero and no bigger than 10 MB.");
                return;
            }

            var contentType = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(contentType))
            {
                SetChatActivityStatus(IsArabic ? "صيغة الصورة غير مدعومة." : "This image format is not supported.");
                return;
            }

            _pendingVisionImageBytes = File.ReadAllBytes(dialog.FileName);
            _pendingVisionContentType = contentType;
            _pendingVisionFileName = Path.GetFileName(dialog.FileName);
            UpdateVisionAttachmentControls();
            SetChatActivityStatus(IsArabic
                ? "تم تجهيز الصورة للمعاينة المحلية فقط. تحليل الصور المستقل غير متاح ولن تُرسل إلى أي نموذج."
                : "The image is ready for local preview only. Independent image analysis is unavailable; it will not be sent to any model.");
            ChatInput.Focus();
        }
        catch (Exception ex)
        {
            SetChatActivityStatus(UiLocalization.FormatTechnicalError("تعذر قراءة الصورة", "Could not read image", ex, IsArabic));
        }
    }

    private void ClearVisionImageButton_Click(object sender, RoutedEventArgs e)
    {
        PrivacyShieldPolicy.ClearCapturedBuffer(_pendingVisionImageBytes);
        _pendingVisionImageBytes = null;
        _pendingVisionContentType = null;
        _pendingVisionFileName = null;
        UpdateVisionAttachmentControls();
        SetChatActivityStatus(IsArabic ? "تمت إزالة الصورة المرفقة." : "The attached image was removed.");
    }

    private void UpdateVisionAttachmentControls()
    {
        if (VisionAttachmentStatus is null) return;
        var hasImage = _pendingVisionImageBytes is { Length: > 0 };
        var hasDocument = !string.IsNullOrWhiteSpace(_pendingDocumentText);
        var previewLoaded = false;
        if (hasImage)
        {
            try
            {
                using var stream = new MemoryStream(_pendingVisionImageBytes!, writable: false);
                var preview = new BitmapImage();
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.DecodePixelWidth = 128;
                preview.StreamSource = stream;
                preview.EndInit();
                preview.Freeze();
                VisionAttachmentPreview.Source = preview;
                previewLoaded = true;
            }
            catch
            {
                VisionAttachmentPreview.Source = null;
            }
        }
        else
        {
            VisionAttachmentPreview.Source = null;
        }

        VisionAttachmentPreview.Visibility = hasImage && previewLoaded ? Visibility.Visible : Visibility.Collapsed;
        VisionAttachmentStatus.Text = hasImage
            ? (IsArabic ? "معاينة محلية فقط: " : "Local preview only: ") + (_pendingVisionFileName ?? (IsArabic ? "صورة" : "image")) +
              (previewLoaded ? string.Empty : (IsArabic ? " (تعذرت المعاينة)" : " (preview unavailable)"))
            : (IsArabic ? "لا توجد صورة مرفقة" : "No image attached");
        ClearVisionImageButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        ChooseImageButton.IsEnabled = _chatCancellation is null && !hasDocument;
        CaptureScreenRegionButton.IsEnabled = !AppRuntime.Settings.PrivacyShieldEnabled && _chatCancellation is null && !hasDocument;
        ClearVisionImageButton.IsEnabled = _chatCancellation is null;
        ChooseDocumentButton.IsEnabled = _chatCancellation is null && !hasImage;
        ClearDocumentButton.IsEnabled = _chatCancellation is null;
        UpdateClipboardReadButtons();
    }

}
