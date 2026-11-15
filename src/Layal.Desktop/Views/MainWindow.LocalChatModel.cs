using System;
using System.Windows;
using Layal.Services;

namespace Layal.Views;

public partial class MainWindow
{
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
            UpdateLocalChatModelStatusText();
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                UiLocalization.FormatTechnicalError("تعذر حفظ إعداد النموذج المحلي", "Could not save local model settings", ex, IsArabic));
            LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private async void TestLocalChatModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is not null)
        {
            SetAccessibleStatusText(LocalChatModelStatusText,
                IsArabic ? "أوقف المحادثة الحالية قبل اختبار الخادم المحلي." : "Stop the current chat before testing the local server.");
            return;
        }

        TestLocalChatModelButton.IsEnabled = false;
        try
        {
            var result = await _localChatModelService.TestConnectionAsync(LocalChatModelBaseUrlInput.Text.Trim());
            SetAccessibleStatusText(LocalChatModelStatusText,
                (IsArabic ? "اتصال الخادم المحلي ناجح. " : "Local server connection successful. ") + result +
                (IsArabic ? " أدخل معرّف نموذج مثبتًا واحفظ الإعداد لتفعيل المحادثة." : " Enter an installed model ID and save to enable model-backed chat."));
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
                ? $"النموذج المحلي المحدد: {AppRuntime.Settings.LocalChatModelId}. اختبر الخادم المحلي للتأكد من أنه يعمل."
                : $"Configured local model: {AppRuntime.Settings.LocalChatModelId}. Test the local server to confirm it is running.")
            : (IsArabic
                ? "لم يتم إعداد نموذج محادثة محلي. ستظل النواة الرمزية المحدودة متاحة، ولن تُرسل الرسائل إلى الإنترنت."
                : "No local chat model is configured. The bounded native core remains available; messages will not be sent to the internet."));
        LocalChatModelStatusText.Foreground = ThemeBrush("Layal.Muted", System.Windows.Media.Brushes.Gray);
    }

    private string BuildLocalChatSystemPrompt() => IsArabic
        ? "أنت ليال، مساعدة مكتبية تعمل محليًا على جهاز المستخدم. أجيبي بالعربية المصرية الطبيعية عندما يكتب المستخدم بالمصرية، وبالفصحى عندما يكتب بالفصحى، وبالإنجليزية عندما يكتب بالإنجليزية. حافظي على سياق الرسائل السابقة، واطلبي توضيحًا عند نقص المعلومات. لا تدّعي تنفيذ ملفات أو أوامر أو الوصول إلى الجهاز؛ لا توجد أدوات تنفيذ مرفقة بهذا الطلب. ميّزي بين الحقائق والتخمين، وكوني صريحة بشأن عدم اليقين. لا تطلبي أسرارًا أو مفاتيح API. هذه محادثة نصية محلية فقط."
        : "You are Layal, a desktop assistant running through a model hosted on the user's own computer. Reply in the language and register used by the user, preserve prior conversation context, and ask for clarification when needed. Do not claim to have executed files, commands, or inspected the device; no execution tools are attached to this chat request. Distinguish facts from guesses and state uncertainty. Never ask for passwords or API keys. This is local text chat only.";
}
