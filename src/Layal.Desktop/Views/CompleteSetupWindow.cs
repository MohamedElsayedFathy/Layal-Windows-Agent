using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Services;

namespace Layal.Views;

/// <summary>
/// A single-consent first-run workflow for local chat, speech and embedded OCR.
/// The window is shown before the normal Layal shell and never enables the microphone.
/// </summary>
public sealed class CompleteSetupWindow : Window
{
    private readonly bool _isArabic;
    private readonly LocalResourceManager _resourceManager = new();
    private readonly OllamaLocalModelSetupService _chatSetup = new();
    private readonly LocalEmbeddedResourceSetupService _ocrSetup = new();
    private readonly LocalVoiceService _voiceService;
    private readonly CompleteSetupPreflight _preflight;

    private readonly CheckBox _consent = new();
    private readonly Button _startButton = new();
    private readonly Button _limitedModeButton = new();
    private readonly Button _cancelButton = new();
    private readonly TextBlock _statusText = new();
    private readonly ProgressBar _progressBar = new();
    private readonly TextBox _diagnostics = new();
    private CancellationTokenSource? _setupCancellation;
    private bool _isRunning;

    public bool CanContinueToMainWindow { get; private set; }
    public bool SetupCompleted { get; private set; }

    public CompleteSetupWindow(string dataRoot, LocalVoiceService voiceService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));
        _isArabic = string.Equals(AppRuntime.Settings.Language, "ar", StringComparison.OrdinalIgnoreCase);
        _preflight = CompleteSetupPreflightService.Inspect(dataRoot);

        Title = T("إعداد ليال الكامل", "Layal complete setup");
        Width = 860;
        Height = 760;
        MinWidth = 680;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(243, 246, 255));
        FlowDirection = _isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Content = BuildContent();
        Closing += OnClosing;
        UpdateButtons();
    }

    private UIElement BuildContent()
    {
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(CreateText(
            T("خلّينا نجهّز ليال بالكامل", "Let's prepare Layal completely"),
            27, FontWeights.Bold, new Thickness(0, 0, 0, 8)));
        panel.Children.Add(CreateText(
            T(
                "سيُجهّز هذا المعالج المحادثة التوليدية المحلية، وفهم الكلام والنطق المحليين، وموارد OCR العربية والإنجليزية قبل فتح مساحة العمل.",
                "This setup prepares local generative chat, local speech recognition and synthesis, and Arabic/English OCR before opening the workspace."),
            15, FontWeights.Normal, new Thickness(0, 0, 0, 18)));

        var platformText = CreateText(
            T("فحص الجهاز والقرص", "Device and storage check"),
            16, FontWeights.SemiBold, new Thickness(0, 0, 0, 8));
        var preflightText = CreateText(
            _preflight.PlatformDescription + Environment.NewLine +
            _preflight.StorageDescription + Environment.NewLine +
            (_preflight.Warning ?? T(
                "سيُحدّث المعالج أحجام التنزيل من بيانات المصادر الفعلية؛ لا يفترض حجمًا ثابتًا للموارد التي لا تعلن حجمًا موثوقًا.",
                "Download sizes will be updated from source-reported data; no fixed size is assumed for resources without a reliable published size.")),
            13, FontWeights.Normal, new Thickness(0));
        panel.Children.Add(CreateCard(new StackPanel
        {
            Children = { platformText, preflightText }
        }));

        var resources = new StackPanel();
        resources.Children.Add(CreateText(
            T("الموارد التي سيجهزها المسار الكامل", "Resources included in full setup"),
            16, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        resources.Children.Add(CreateText(
            T("• المحادثة المحلية: Ollama + Qwen3.5 2B، مع اختبار استدلال محلي",
              "• Local chat: Ollama + Qwen3.5 2B, followed by a local inference test"),
            14, FontWeights.Normal, new Thickness(0, 3)));
        resources.Children.Add(CreateText(
            T("• الصوت المحلي: Python وPyTorch وOmniVoice وVoiceTut وأوزانه، بالإضافة إلى Whisper",
              "• Local speech: Python, PyTorch, OmniVoice, VoiceTut and its model weights, plus Whisper"),
            14, FontWeights.Normal, new Thickness(0, 3)));
        resources.Children.Add(CreateText(
            T("• OCR: التحقق من بيانات اللغة العربية والإنجليزية المضمّنة",
              "• OCR: verify the bundled Arabic and English language data"),
            14, FontWeights.Normal, new Thickness(0, 3)));
        panel.Children.Add(CreateCard(resources));

        _consent.Content = T(
            "أوافق على تنزيل وتثبيت الموارد المحلية المذكورة من مصادرها المعتمدة، وتشغيل اختبارات الصحة المحلية. لن يُشغّل المعالج الميكروفون ولن يرسل المحادثات إلى خدمة سحابية.",
            "I approve downloading and installing the listed local resources from their approved sources and running local health checks. Setup will not turn on the microphone or send conversations to a cloud service.");
        _consent.Margin = new Thickness(4, 14, 4, 14);
        _consent.FontSize = 14;
        _consent.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(_consent, T("الموافقة على إعداد الموارد المحلية", "Approve local resource setup"));
        _consent.Checked += (_, _) => UpdateButtons();
        _consent.Unchecked += (_, _) => UpdateButtons();
        panel.Children.Add(_consent);

        _statusText.Text = T("راجع التفاصيل ثم وافق لبدء الإعداد.", "Review the details, then approve to begin setup.");
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.FontSize = 14;
        _statusText.Margin = new Thickness(0, 4, 0, 8);
        AutomationProperties.SetName(_statusText, T("حالة إعداد ليال", "Layal setup status"));
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);
        panel.Children.Add(_statusText);

        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Height = 14;
        _progressBar.IsIndeterminate = true;
        _progressBar.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetName(_progressBar, T("تقدم الإعداد", "Setup progress"));
        panel.Children.Add(_progressBar);

        _diagnostics.IsReadOnly = true;
        _diagnostics.AcceptsReturn = true;
        _diagnostics.TextWrapping = TextWrapping.Wrap;
        _diagnostics.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _diagnostics.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _diagnostics.MinHeight = 112;
        _diagnostics.MaxHeight = 180;
        _diagnostics.FontFamily = new FontFamily("Consolas");
        _diagnostics.FontSize = 12;
        _diagnostics.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetName(_diagnostics, T("سجل تشخيص إعداد الموارد", "Resource setup diagnostics"));
        panel.Children.Add(_diagnostics);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _startButton.Content = T("ابدأ الإعداد الكامل", "Start full setup");
        _startButton.MinWidth = 170;
        _startButton.Padding = new Thickness(16, 10, 16, 10);
        _startButton.Margin = new Thickness(0, 0, 8, 0);
        _startButton.IsDefault = true;
        _startButton.Click += async (_, _) => await StartSetupAsync();

        _limitedModeButton.Content = T("المتابعة بالوضع المحدود", "Continue in limited mode");
        _limitedModeButton.MinWidth = 190;
        _limitedModeButton.Padding = new Thickness(16, 10, 16, 10);
        _limitedModeButton.Margin = new Thickness(0, 0, 8, 0);
        _limitedModeButton.Click += (_, _) => ContinueInLimitedMode();

        _cancelButton.Content = T("إغلاق", "Close");
        _cancelButton.MinWidth = 90;
        _cancelButton.Padding = new Thickness(16, 10, 16, 10);
        _cancelButton.Click += (_, _) => CancelOrClose();

        buttons.Children.Add(_startButton);
        buttons.Children.Add(_limitedModeButton);
        buttons.Children.Add(_cancelButton);
        panel.Children.Add(buttons);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
        return new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(213, 220, 240)),
            Background = Brushes.White,
            Child = scroll
        };
    }

    private async Task StartSetupAsync()
    {
        if (_isRunning || _consent.IsChecked != true || !_preflight.IsSupported)
            return;

        _setupCancellation = new CancellationTokenSource();
        var token = _setupCancellation.Token;
        _isRunning = true;
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        UpdateButtons();
        AppendLog(T("بدأ الإعداد الكامل بموافقة المستخدم.", "Full setup started with user consent."));

        var chatProgress = new Progress<string>(message =>
        {
            _statusText.Text = message;
        });
        var modelProgress = new Progress<LocalResourceDownloadProgress>(progress =>
        {
            if (progress.TotalBytes > 0)
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = Math.Clamp(
                    progress.DownloadedBytes * 100d / progress.TotalBytes, 0d, 100d);
                var speed = CompleteSetupPreflightService.FormatBytes(
                    (long)Math.Max(0, progress.BytesPerSecond)) + "/s";
                var eta = progress.RemainingSeconds.HasValue
                    ? TimeSpan.FromSeconds(Math.Clamp(progress.RemainingSeconds.Value, 0, 365 * 24 * 60 * 60))
                        .ToString(@"h\:mm\:ss")
                    : T("جارٍ الحساب", "calculating");
                _statusText.Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("تنزيل نموذج المحادثة: {0} من {1} — {2} — المتبقي {3}",
                      "Downloading chat model: {0} of {1} — {2} — remaining {3}"),
                    CompleteSetupPreflightService.FormatBytes(progress.DownloadedBytes),
                    CompleteSetupPreflightService.FormatBytes(progress.TotalBytes),
                    speed,
                    eta);
            }
        });
        var speechProgress = new Progress<LocalSpeechProgress>(progress =>
        {
            _statusText.Text = progress.Message;
            if (progress.Percent.HasValue)
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = Math.Clamp(progress.Percent.Value, 0, 100);
            }
            else
            {
                _progressBar.IsIndeterminate = true;
            }
        });

        try
        {
            _statusText.Text = T("التحقق من موارد OCR المضمّنة…", "Verifying bundled OCR resources…");
            await _ocrSetup.EnsureOcrResourcesReadyAsync(token).ConfigureAwait(true);
            AppendLog(T("تم التحقق من OCR العربي والإنجليزي.", "Arabic and English OCR resources verified."));

            _statusText.Text = T("تجهيز Ollama ونموذج المحادثة المحلي…", "Preparing Ollama and the local chat model…");
            await _chatSetup.InstallAndPrepareAsync(chatProgress, token, modelProgress).ConfigureAwait(true);
            AppRuntime.Settings.UseLocalAi = true;
            AppRuntime.Settings.LocalChatModelBaseUrl = LocalChatModelService.DefaultBaseUrl;
            AppRuntime.Settings.LocalChatModelId = OllamaLocalModelSetupService.DefaultModelId;
            AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            AppendLog(T("نجح اختبار المحادثة المحلية وحُفظت إعداداتها.", "Local chat smoke test passed and its settings were saved."));

            _statusText.Text = T("تجهيز Whisper وبيئة Python والصوت المحلي…", "Preparing Whisper, the Python environment and local speech…");
            _progressBar.IsIndeterminate = true;
            await _voiceService.EnsureReadyAsync(speechProgress, token).ConfigureAwait(true);
            AppendLog(T("تم تجهيز Whisper وVoiceTut والتحقق من التشغيل المحلي.", "Whisper and VoiceTut setup and local health checks completed."));

            var snapshot = await _resourceManager.GetSnapshotAsync(token).ConfigureAwait(true);
            var missing = CompleteSetupReadinessPolicy.GetMissingRequiredResources(snapshot);
            if (missing.Count > 0)
            {
                var names = string.Join(", ", missing.Select(item => item.DisplayName));
                throw new InvalidOperationException(
                    T("لم تصبح كل الموارد المطلوبة جاهزة بعد: ", "Some required resources are not ready yet: ") + names);
            }

            SetupCompleted = true;
            CanContinueToMainWindow = true;
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 100;
            _statusText.Text = T(
                "اكتمل الإعداد والتحقق. ستُفتح ليال الآن؛ الميكروفون ما زال متوقفًا حتى تطلب استخدامه.",
                "Setup and verification are complete. Layal will open now; the microphone remains off until you explicitly use it.");
            AppendLog(T("اكتملت كل بوابات الموارد المطلوبة.", "All required resource gates are ready."));
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _statusText.Text = T("تم إيقاف الإعداد بأمان. الموارد التي اكتمل التحقق منها محفوظة.", "Setup was safely cancelled. Resources that passed verification remain available.");
            AppendLog(_statusText.Text);
        }
        catch (Exception exception)
        {
            var detail = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            if (detail.Length > 1200) detail = detail[..1200] + "…";
            _statusText.Text = T("تعذر إكمال الإعداد: ", "Setup could not be completed: ") + detail;
            AppendLog(_statusText.Text);
        }
        finally
        {
            _isRunning = false;
            _setupCancellation?.Dispose();
            _setupCancellation = null;
            UpdateButtons();
        }
    }

    private void ContinueInLimitedMode()
    {
        if (_isRunning) return;
        CanContinueToMainWindow = true;
        SetupCompleted = false;
        DialogResult = true;
        Close();
    }

    private void CancelOrClose()
    {
        if (_isRunning)
        {
            _setupCancellation?.Cancel();
            _statusText.Text = T("جارٍ إيقاف الإعداد بأمان…", "Cancelling setup safely…");
            return;
        }

        CanContinueToMainWindow = false;
        DialogResult = false;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isRunning) return;
        e.Cancel = true;
        _setupCancellation?.Cancel();
        _statusText.Text = T("جارٍ إيقاف الإعداد قبل الإغلاق…", "Cancelling setup before closing…");
    }

    private void UpdateButtons()
    {
        _startButton.IsEnabled = !_isRunning && _preflight.IsSupported && _consent.IsChecked == true;
        _limitedModeButton.IsEnabled = !_isRunning;
        _cancelButton.IsEnabled = true;
        _cancelButton.Content = _isRunning ? T("إيقاف", "Stop") : T("إغلاق", "Close");
    }

    private Border CreateCard(UIElement child) => new()
    {
        Margin = new Thickness(0, 0, 0, 14),
        Padding = new Thickness(18),
        CornerRadius = new CornerRadius(14),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromRgb(213, 220, 240)),
        Background = new SolidColorBrush(Color.FromRgb(250, 251, 255)),
        Child = child
    };

    private TextBlock CreateText(string text, double size, FontWeight weight, Thickness margin) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = _isArabic ? TextAlignment.Right : TextAlignment.Left,
        Margin = margin,
        Foreground = new SolidColorBrush(Color.FromRgb(29, 43, 73))
    };

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var value = "[" + DateTimeOffset.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
        _diagnostics.AppendText(value);
        if (_diagnostics.Text.Length > 24000)
            _diagnostics.Text = _diagnostics.Text[^16000..];
        _diagnostics.ScrollToEnd();
    }

    private string T(string arabic, string english) => _isArabic ? arabic : english;
}
