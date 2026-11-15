using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

public partial class MainWindow
{
    private readonly OllamaLocalModelSetupService _ollamaLocalModelSetupService = new();
    private CancellationTokenSource? _localChatSetupCancellation;
    // Voice turns stay in memory and are never mixed into, displayed in, or persisted with text chat.
    private readonly List<ChatMessage> _voiceConversationMessages = new();

    private void SaveLocalChatModelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = LocalChatModelService.ValidateBaseUrl(LocalChatModelBaseUrlInput.Text);
            var modelId = LocalChatModelService.NormalizeModelId(LocalChatModelIdInput.Text);
            var settings = AppRuntime.Settings.Clone();
            settings.LocalChatModelBaseUrl = baseUrl;
            settings.LocalChatModelId = modelId;
            AppRuntime.SettingsStore.Save(settings);
            AppRuntime.Settings.CopyFrom(settings);
            _voiceConversationMessages.Clear();
            UpdateLocalChatModelStatusText();
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                UiLocalization.FormatTechnicalError("تعذر حفظ إعداد النموذج المحلي", "Could not save local model settings", ex, IsArabic));
            LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private async void SetupLocalChatModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_localChatSetupCancellation is not null) return;
        if (_chatCancellation is not null || _voiceConversationCancellation is not null ||
            _voiceTestCancellation is not null || _localAiSetupCancellation is not null)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                IsArabic
                    ? "أوقف المحادثة أو إعداد الصوت الحالي قبل تجهيز نموذج المحادثة."
                    : "Stop the current chat or speech setup before preparing the chat model.");
            return;
        }

        var consent = LayalDialogService.Show(
            this,
            IsArabic
                ? "سيتم تثبيت Ollama إذا لم يكن موجودًا (باستخدام WinGet عند توفره)، ثم تنزيل نموذج Qwen3.5 2B بحجم متوقع 2.7 إلى 3.1 جيجابايت. سيعمل النموذج على هذا الجهاز، وستظل المحادثات على العنوان المحلي فقط. هل توافق على التثبيت والتنزيل؟"
                : "This will install Ollama if it is missing (using WinGet when available), then download the Qwen3.5 2B model (approximately 2.7–3.1 GB). The model runs on this PC and chat stays on the loopback endpoint. Continue?",
            IsArabic ? "تجهيز المحادثة المحلية" : "Set up local chat",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
        if (consent != MessageBoxResult.Yes) return;

        var cancellation = new CancellationTokenSource();
        _localChatSetupCancellation = cancellation;
        SetupLocalChatModelButton.IsEnabled = false;
        CancelLocalChatModelSetupButton.Visibility = Visibility.Visible;
        CancelLocalChatModelSetupButton.IsEnabled = true;
        LocalChatModelProgressBar.Visibility = Visibility.Visible;
        LocalChatModelProgressBar.IsIndeterminate = true;
        LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Cyan", System.Windows.Media.Brushes.SteelBlue);

        var progress = new Progress<string>(message =>
        {
            if (!ReferenceEquals(_localChatSetupCancellation, cancellation)) return;
            SetAccessibleStatusText(LocalChatModelStatusText, message);
        });

        try
        {
            await _ollamaLocalModelSetupService.InstallAndPrepareAsync(progress, cancellation.Token);

            var settings = AppRuntime.Settings.Clone();
            settings.LocalChatModelBaseUrl = LocalChatModelService.DefaultBaseUrl;
            settings.LocalChatModelId = OllamaLocalModelSetupService.DefaultModelId;
            AppRuntime.SettingsStore.Save(settings);
            AppRuntime.Settings.CopyFrom(settings);
            LocalChatModelBaseUrlInput.Text = settings.LocalChatModelBaseUrl;
            LocalChatModelIdInput.Text = settings.LocalChatModelId;
            _voiceConversationMessages.Clear();

            SetAccessibleStatusText(LocalChatModelStatusText,
                IsArabic
                    ? $"تم تجهيز {settings.LocalChatModelId} والتحقق من خادم Ollama المحلي. أصبحت المحادثة المفتوحة متاحة دون مزود سحابي."
                    : $"Prepared {settings.LocalChatModelId} and verified the local Ollama server. Open-ended chat is now available without a cloud provider.");
            LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                IsArabic ? "تم إلغاء تجهيز نموذج المحادثة. لم يُحذف أي نموذج موجود." : "Chat model setup was cancelled. Existing models were not removed.");
            LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                UiLocalization.FormatTechnicalError("تعذر تجهيز نموذج المحادثة المحلي", "Could not set up the local chat model", ex, IsArabic));
            LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
        finally
        {
            if (ReferenceEquals(_localChatSetupCancellation, cancellation))
                _localChatSetupCancellation = null;
            cancellation.Dispose();
            SetupLocalChatModelButton.IsEnabled = true;
            CancelLocalChatModelSetupButton.Visibility = Visibility.Collapsed;
            CancelLocalChatModelSetupButton.IsEnabled = false;
            LocalChatModelProgressBar.IsIndeterminate = false;
            LocalChatModelProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelLocalChatModelSetupButton_Click(object sender, RoutedEventArgs e)
    {
        _localChatSetupCancellation?.Cancel();
        CancelLocalChatModelSetupButton.IsEnabled = false;
        SetAccessibleStatusText(LocalChatModelStatusText,
            IsArabic ? "جارٍ إيقاف تجهيز نموذج المحادثة…" : "Cancelling local chat model setup…");
    }

    private async void TestLocalChatModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null || _localChatSetupCancellation is not null)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                IsArabic ? "أوقف المحادثة أو التجهيز الحالي قبل اختبار الخادم المحلي." : "Stop the current chat or setup before testing the local server.");
            return;
        }

        TestLocalChatModelButton.IsEnabled = false;
        try
        {
            var result = await _localChatModelService.TestConnectionAsync(LocalChatModelBaseUrlInput.Text.Trim());
            SetAccessibleStatusText(LocalChatModelStatusText,
                (IsArabic ? "اتصال الخادم المحلي ناجح. " : "Local server connection successful. ") + result +
                (IsArabic ? " تأكد أن معرّف النموذج المحفوظ يشير إلى نموذج مثبت." : " Ensure the saved model ID refers to an installed model."));
            LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                UiLocalization.FormatTechnicalError("فشل اختبار الخادم المحلي", "Local server test failed", ex, IsArabic));
            LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
        finally
        {
            TestLocalChatModelButton.IsEnabled = true;
        }
    }

    private void UpdateLocalChatModelStatusText()
    {
        if (LocalChatModelStatusText is null) return;
        var configured = LocalChatModelService.IsConfigured(
            AppRuntime.Settings.LocalChatModelBaseUrl,
            AppRuntime.Settings.LocalChatModelId);
        SetAccessibleStatusText(LocalChatModelStatusText, configured
            ? (IsArabic
                ? $"النموذج المحلي المحدد: {AppRuntime.Settings.LocalChatModelId}. استخدم اختبار الخادم للتأكد من أنه يعمل."
                : $"Configured local model: {AppRuntime.Settings.LocalChatModelId}. Test the server to confirm it is running.")
            : (IsArabic
                ? "لم يُجهز نموذج محادثة بعد؛ الشات يستخدم النواة المحدودة فقط. اضغط «تجهيز المحادثة المحلية» لتنزيل نموذج عام على الجهاز."
                : "No chat model is set up yet; chat uses the bounded native core only. Select Set up local chat to download a general-purpose model to this PC."));
        LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Muted", System.Windows.Media.Brushes.Gray);
    }

    private string BuildLocalChatSystemPrompt() => IsArabic
        ? "أنتِ ليال، مساعدة عامة تعمل بنموذج لغوي محلي على جهاز المستخدم. افهمي الطلب من معناه وسياقه، لا من مطابقة كلمات ثابتة. حددي لغة آخر رسالة من نصها لا من لغة واجهة التطبيق: أجيبي بالمصرية الطبيعية إذا كانت الرسالة بالمصرية، وبالفصحى إذا كانت بالفصحى، وبالإنجليزية إذا كانت بالإنجليزية، وحافظي على المزج الطبيعي إذا مزج المستخدم اللغتين. احتفظي بسياق المحادثة الحالية، واسألي سؤالًا محددًا عند نقص معلومة ضرورية. ميّزي الحقائق عن الاستنتاجات وصرّحي بعدم اليقين. لا تدّعي تنفيذ ملفات أو أوامر أو الوصول إلى الجهاز؛ لا توجد أدوات تنفيذ مرفقة. لا تطلبي كلمات مرور أو مفاتيح API. هذه محادثة محلية فقط."
        : "You are Layal, a general-purpose assistant running on a language model hosted locally on the user's PC. Understand requests from their meaning and context, not fixed keyword matching. Detect the language of the latest message itself, not the UI language: use natural Egyptian Arabic for Egyptian Arabic, MSA for MSA, English for English, and natural code-switching when the user mixes languages. Preserve the current conversation context and ask a focused question when essential information is missing. Distinguish facts from inferences and state uncertainty. Do not claim to execute files or commands or access the device; no tools are attached. Never ask for passwords or API keys. This is local chat only.";
}
