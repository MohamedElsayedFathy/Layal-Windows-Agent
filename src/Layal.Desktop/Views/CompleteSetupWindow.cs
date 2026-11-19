using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Layal.Services;

namespace Layal.Views;

/// <summary>
/// A single-consent first-run workflow for local chat, speech and embedded OCR.
/// The window is shown before the normal Layal shell and never enables the microphone.
/// </summary>
public sealed class CompleteSetupWindow : Window
{
    private sealed record ResourceProgressRow(
        TextBlock StateText,
        ProgressBar ProgressBar,
        TextBlock DetailText);

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
    private readonly TextBlock _connectivityText = new();
    private readonly ProgressBar _progressBar = new();
    private readonly TextBox _diagnostics = new();
    private readonly StackPanel _resourceProgressPanel = new();
    private readonly Dictionary<string, ResourceProgressRow> _resourceProgressRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalSpeechProgress> _liveResourceProgress = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _resourceProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private TextBlock _resourceProgressSummary = new();
    private bool _isRefreshingResourceProgress;
    private readonly TextBlock _resourceDirectoryText = new();
    private readonly Button _chooseResourceDirectoryButton = new();
    private readonly List<TextBlock> _themeTextBlocks = new();
    private readonly List<Border> _themeCards = new();
    private Border? _rootBorder;
    private bool? _connectivityReachable;
    private bool _restartRequested;
    private CancellationTokenSource? _setupCancellation;
    private CancellationTokenSource? _connectivityCancellation;
    private bool _connectivityCheckInProgress;
    private bool _isRunning;
    private bool _hasAttemptedSetup;

    public bool CanContinueToMainWindow { get; private set; }
    public bool SetupCompleted { get; private set; }
    public bool RestartRequested => _restartRequested;

    public CompleteSetupWindow(string dataRoot, LocalVoiceService voiceService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _voiceService = voiceService ?? throw new ArgumentNullException(nameof(voiceService));
        _isArabic = string.Equals(AppRuntime.Settings.Language, "ar", StringComparison.OrdinalIgnoreCase);
        _preflight = CompleteSetupPreflightService.Inspect(LocalVoiceService.DataRoot);

        Title = T("إعداد ليال الكامل", "Layal complete setup");
        Width = 860;
        Height = 760;
        MinWidth = 680;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemParameters.HighContrast
            ? SystemColors.WindowBrush
            : new SolidColorBrush(Color.FromRgb(243, 246, 255));
        FlowDirection = _isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Content = BuildContent();
        SystemParameters.StaticPropertyChanged += OnSystemParametersStaticPropertyChanged;
        Closed += (_, _) =>
        {
            _resourceProgressTimer.Stop();
            SystemParameters.StaticPropertyChanged -= OnSystemParametersStaticPropertyChanged;
        };
        _resourceProgressTimer.Tick += async (_, _) => await RefreshResourceProgressAsync();
        Loaded += async (_, _) => await RefreshResourceProgressAsync();
        Closing += OnClosing;
        ApplySystemTheme();
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
            _preflight.CpuDescription + Environment.NewLine +
            "RAM: " + _preflight.MemoryDescription + Environment.NewLine +
            "GPU: " + _preflight.GpuDescription + Environment.NewLine +
            _preflight.StorageDescription + Environment.NewLine +
            (_preflight.Warning ?? T(
                "سيُحدّث المعالج أحجام التنزيل من بيانات المصادر الفعلية؛ لا يفترض حجمًا ثابتًا للموارد التي لا تعلن حجمًا موثوقًا.",
                "Download sizes will be updated from source-reported data; no fixed size is assumed for resources without a reliable published size.")),
            13, FontWeights.Normal, new Thickness(0));
        panel.Children.Add(CreateCard(new StackPanel
        {
            Children = { platformText, preflightText }
        }));

        var resourceLocationPanel = new StackPanel();
        resourceLocationPanel.Children.Add(CreateText(
            T("مجلد موارد الذكاء الاصطناعي", "AI resource folder"),
            16, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        _resourceDirectoryText.Text = LocalVoiceService.DataRoot;
        _resourceDirectoryText.TextWrapping = TextWrapping.Wrap;
        _resourceDirectoryText.FontSize = 12;
        AutomationProperties.SetName(
            _resourceDirectoryText,
            T("مسار تخزين موارد ليال المحلية", "Layal local resource storage path"));
        resourceLocationPanel.Children.Add(_resourceDirectoryText);
        _chooseResourceDirectoryButton.Content = T("اختيار مجلد أو قرص آخر", "Choose another folder or drive");
        _chooseResourceDirectoryButton.Margin = new Thickness(0, 10, 0, 0);
        _chooseResourceDirectoryButton.Padding = new Thickness(12, 7, 12, 7);
        AutomationProperties.SetName(
            _chooseResourceDirectoryButton,
            T("اختيار مجلد موارد محلي بديل", "Choose an alternative local resource folder"));
        AutomationProperties.SetHelpText(
            _chooseResourceDirectoryButton,
            T("ينقل موارد ليال الحالية إلى مجلد فارغ ويتحقق من كل ملف. لا يحذف المجلد الأصلي.",
              "Copies existing Layal resources to an empty folder and verifies every file. The original folder is retained."));
        _chooseResourceDirectoryButton.Click += async (_, _) => await ChooseResourceDirectoryAsync();
        resourceLocationPanel.Children.Add(_chooseResourceDirectoryButton);
        resourceLocationPanel.Children.Add(CreateText(
            T("يجب أن يكون المجلد على قرص محلي جاهز وفارغ. تُنقل أوزان النماذج وكاش pip وHugging Face وملفات نموذج Ollama. تُعاد إنشاء بيئة Python وvenv في المكان الجديد لأن مساراتها مرتبطة بالموقع. تظل المحادثات والذاكرة والمستندات في مكانها. أغلق Ollama أولًا.",
              "Choose an empty folder on a ready local drive. Model weights, pip/Hugging Face caches and the Ollama model store are copied. Python and its venv are recreated because their paths are location-bound. Chats, memory and documents stay where they are. Exit Ollama first."),
            12, FontWeights.Normal, new Thickness(0, 8, 0, 0)));
        panel.Children.Add(CreateCard(resourceLocationPanel));

        _connectivityText.Text = T(
            "سيتم فحص الاتصال بالمصادر الرسمية بعد موافقتك على الإعداد.",
            "Official source connectivity will be checked after you approve setup.");
        _connectivityText.TextWrapping = TextWrapping.Wrap;
        _connectivityText.FontSize = 13;
        AutomationProperties.SetName(
            _connectivityText,
            T("حالة الاتصال بمصادر التنزيل", "Official download source connectivity"));
        AutomationProperties.SetLiveSetting(_connectivityText, AutomationLiveSetting.Polite);
        panel.Children.Add(CreateCard(new StackPanel
        {
            Children =
            {
                CreateText(T("اتصال الإنترنت والمصادر", "Internet and source connectivity"),
                    16, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)),
                _connectivityText
            }
        }));

        var resources = new StackPanel();
        resources.Children.Add(CreateText(
            T("الموارد التي سيجهزها المسار الكامل", "Resources included in full setup"),
            16, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        resources.Children.Add(CreateText(
            T("• المحادثة المحلية: Ollama + Qwen3.5 2B، مع اختبار استدلال محلي",
              "• Local chat: Ollama + Qwen3.5 2B, followed by a local inference test"),
            14, FontWeights.Normal, new Thickness(0, 3, 0, 3)));
        resources.Children.Add(CreateText(
            T("• الصوت المحلي: Python وPyTorch وOmniVoice وVoiceTut وأوزانه، بالإضافة إلى Whisper",
              "• Local speech: Python, PyTorch, OmniVoice, VoiceTut and its model weights, plus Whisper"),
            14, FontWeights.Normal, new Thickness(0, 3, 0, 3)));
        resources.Children.Add(CreateText(
            T("• OCR: التحقق من بيانات اللغة العربية والإنجليزية المضمّنة",
              "• OCR: verify the bundled Arabic and English language data"),
            14, FontWeights.Normal, new Thickness(0, 3, 0, 3)));
        panel.Children.Add(CreateCard(resources));

        var resourceProgressCard = new StackPanel();
        resourceProgressCard.Children.Add(CreateText(
            T("حالة كل مورد على حدة", "Individual resource status"),
            16, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        _resourceProgressSummary = CreateText(
            T("جارٍ قراءة حالة الموارد المحفوظة…", "Reading saved resource states…"),
            12, FontWeights.Normal, new Thickness(0, 0, 0, 8));
        AutomationProperties.SetName(
            _resourceProgressSummary,
            T("ملخص جاهزية الموارد", "Resource readiness summary"));
        AutomationProperties.SetLiveSetting(_resourceProgressSummary, AutomationLiveSetting.Polite);
        resourceProgressCard.Children.Add(_resourceProgressSummary);
        foreach (var resource in LocalResourceCatalog.All
                     .OrderBy(item => item.Required ? 0 : 1)
                     .ThenBy(item => item.DisplayName, StringComparer.CurrentCulture))
        {
            var resourceName = GetLocalizedResourceName(resource);
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 7) };
            AutomationProperties.SetName(
                row,
                T("حالة المورد: ", "Resource status: ") + resourceName);
            var nameText = CreateText(resourceName, 13, FontWeights.SemiBold, new Thickness(0, 0, 0, 2));
            AutomationProperties.SetName(nameText, resourceName);
            var stateText = CreateText(
                T("بانتظار التحقق من الحالة", "Waiting for resource state"),
                12, FontWeights.Normal, new Thickness(0, 0, 0, 2));
            AutomationProperties.SetName(
                stateText,
                T("حالة ", "Status of ") + resourceName);
            AutomationProperties.SetLiveSetting(stateText, AutomationLiveSetting.Polite);
            var progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 8,
                Margin = new Thickness(0, 3, 0, 3),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetName(
                progress,
                T("تقدم المورد ", "Progress for ") + resourceName);
            var detailText = CreateText(string.Empty, 11, FontWeights.Normal, new Thickness(0));
            AutomationProperties.SetName(
                detailText,
                T("تفاصيل المورد ", "Details for ") + resourceName);
            row.Children.Add(nameText);
            row.Children.Add(stateText);
            row.Children.Add(progress);
            row.Children.Add(detailText);
            _resourceProgressRows.Add(resource.Id, new ResourceProgressRow(stateText, progress, detailText));
            _resourceProgressPanel.Children.Add(row);
        }
        resourceProgressCard.Children.Add(_resourceProgressPanel);
        panel.Children.Add(CreateCard(resourceProgressCard));

        var referenceSizes = new StackPanel();
        referenceSizes.Children.Add(CreateText(
            T("أحجام مرجعية قبل الموافقة", "Reference sizes before consent"),
            16, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        referenceSizes.Children.Add(CreateText(
            T(
                "Qwen3.5 2B: نحو 2.7–3.1 GB. أوزان VoiceTut: نحو 2.47 GB. PyTorch CPU: نحو 619.4 MB (قد تختلف حزم CUDA). Whisper: نحو 148 MB للنموذج، وقرابة 8 MB للمحرك. Python الاحتياطي: نحو 29.9 MB. يتطلب Ollama نحو 4 GB على الأقل لملفات البرنامج. أحجام اعتماديات pip المؤقتة وبعض الملفات غير ثابتة؛ لذلك هذه قيم مرجعية وليست إجمالي تنزيل أو مساحة نهائيًا مضمونًا.",
                "Qwen3.5 2B: approximately 2.7–3.1 GB. VoiceTut weights: approximately 2.47 GB. PyTorch CPU: approximately 619.4 MB (CUDA packages differ). Whisper: approximately 148 MB for the model and about 8 MB for the runtime. Python fallback: about 29.9 MB. Ollama requires at least 4 GB for program files. Pip dependencies and temporary files vary, so these are references, not a guaranteed aggregate download or final-storage estimate."),
            13, FontWeights.Normal, new Thickness(0)));
        CompleteSetupResourcePlan resourcePlan;
        var resourcePlanWarning = string.Empty;
        try
        {
            resourcePlan = CompleteSetupResourcePlanService.Calculate(
                _resourceManager.GetSnapshotAsync().GetAwaiter().GetResult());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.Security.SecurityException or System.Text.Json.JsonException)
        {
            // Count every required resource as pending if persisted state cannot be read.
            resourcePlan = CompleteSetupResourcePlanService.Calculate(
                Array.Empty<LocalResourceStateEntry>());
            resourcePlanWarning = T(
                "تعذر قراءة حالة الموارد؛ عُرض تقدير محافظ بافتراض أن الموارد المطلوبة غير جاهزة. ",
                "Resource state could not be read; the conservative plan assumes required resources are not ready. ");
        }
        var unknownSizeText = resourcePlan.UnknownSizeResources.Count == 0
            ? T("لا توجد موارد تنزيل مطلوبة بأحجام مجهولة وفق حالة الموارد الحالية.",
                "No pending required downloads have unknown catalog sizes.")
            : string.Join(", ", resourcePlan.UnknownSizeResources);
        referenceSizes.Children.Add(CreateText(
            T(
                "حسب حالة الموارد الحالية، مجموع الأحجام المرجعية المعروفة للموارد المطلوبة غير الجاهزة هو " + resourcePlanWarning +
                CompleteSetupPreflightService.FormatBytes(resourcePlan.KnownReferenceBytes) +
                ". موارد ذات حجم متغير/غير مثبت: " + unknownSizeText +
                ". قد يُعاد استخدام بعض الموارد بدل تنزيلها؛ وهذا ليس إجمالي تنزيل أو مساحة تثبيت نهائية.",
                "Based on current resource state, the sum of known catalog size references for required resources not yet Ready is " + resourcePlanWarning +
                CompleteSetupPreflightService.FormatBytes(resourcePlan.KnownReferenceBytes) +
                ". Some resources may be reused instead of downloaded; this is not a guaranteed download total or final installation size."),
            12, FontWeights.Normal, new Thickness(0, 10, 0, 0)));
        if (resourcePlan.ConditionalResources.Count > 0)
        {
            var conditionalReferences = string.Join(
                ", ",
                resourcePlan.ConditionalResources.Select(resource =>
                    resource.DisplayName + " — " +
                    CompleteSetupPreflightService.FormatBytes(resource.ExpectedSizeBytes!.Value)));
            referenceSizes.Children.Add(CreateText(
                T("موارد احتياطية/اختيارية لا تدخل في المجموع الأساسي (قد تُطلب حسب مسار التثبيت): ",
                  "Optional/fallback resources excluded from the base subtotal (may be needed by the chosen install path): ") +
                conditionalReferences,
                12, FontWeights.Normal, new Thickness(0, 6, 0, 0)));
        }
        panel.Children.Add(CreateCard(referenceSizes));

        _consent.Content = CreateText(
            T(
                "أوافق على تنزيل وتثبيت الموارد المحلية المذكورة من مصادرها المعتمدة، وتشغيل اختبارات الصحة المحلية. لن يُشغّل المعالج الميكروفون ولن يرسل المحادثات إلى خدمة سحابية.",
                "I approve downloading and installing the listed local resources from their approved sources and running local health checks."),
            14, FontWeights.Normal, new Thickness(0));
        _consent.Margin = new Thickness(4, 14, 4, 14);
        _consent.FontSize = 14;
        AutomationProperties.SetName(_consent, T("الموافقة على إعداد الموارد المحلية", "Approve local resource setup"));
        _consent.Checked += async (_, _) =>
        {
            UpdateButtons();
            await RefreshConnectivityAsync();
        };
        _consent.Unchecked += (_, _) =>
        {
            _connectivityCancellation?.Cancel();
            _connectivityText.Text = T(
                "تم إيقاف فحص الاتصال لعدم وجود موافقة حالية.",
                "Connectivity checks are stopped because setup consent is not active.");
            UpdateButtons();
        };
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
        _progressBar.IsIndeterminate = false;
        _progressBar.Visibility = Visibility.Collapsed;
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
        AutomationProperties.SetName(_startButton, T("بدء الإعداد الكامل لموارد ليال", "Start full Layal resource setup"));
        AutomationProperties.SetHelpText(_startButton, T("ينزّل ويجهز الموارد المحلية بعد موافقتك.", "Downloads and prepares local resources after your consent."));
        _startButton.MinWidth = 170;
        _startButton.Padding = new Thickness(16, 10, 16, 10);
        _startButton.Margin = new Thickness(0, 0, 8, 0);
        _startButton.IsDefault = true;
        _startButton.Click += async (_, _) => await StartSetupAsync();

        _limitedModeButton.Content = T("المتابعة بالوضع المحدود", "Continue in limited mode");
        AutomationProperties.SetName(_limitedModeButton, T("المتابعة مؤقتًا بالوضع المحدود", "Continue temporarily in limited mode"));
        AutomationProperties.SetHelpText(_limitedModeButton, T("لا يسجل هذا الاختيار اكتمال الإعداد؛ سيظهر المعالج مرة أخرى.", "This does not mark setup complete; the wizard will appear again."));
        _limitedModeButton.MinWidth = 190;
        _limitedModeButton.Padding = new Thickness(16, 10, 16, 10);
        _limitedModeButton.Margin = new Thickness(0, 0, 8, 0);
        _limitedModeButton.Click += (_, _) => ContinueInLimitedMode();

        _cancelButton.Content = T("إغلاق", "Close");
        AutomationProperties.SetName(_cancelButton, T("إغلاق معالج الإعداد", "Close setup wizard"));
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
        _rootBorder = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush
                : new SolidColorBrush(Color.FromRgb(213, 220, 240)),
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Brushes.White,
            Child = scroll
        };
        return _rootBorder;
    }

    private async Task RefreshConnectivityAsync()
    {
        _connectivityCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _connectivityCancellation = cancellation;
        _connectivityCheckInProgress = true;
        _connectivityText.Text = T(
            "جارٍ فحص اتصال HTTPS بالمصادر الرسمية بعد موافقتك…",
            "Checking HTTPS connectivity to official sources after your approval…");
        UpdateButtons();

        try
        {
            var report = await CompleteSetupPreflightService.InspectConnectivityAsync(cancellation.Token);
            if (!IsLoaded) return;

            var lines = report.Endpoints.Select(endpoint =>
                endpoint.Name + ": " +
                (endpoint.Reachable
                    ? T("متاح (HTTP ", "reachable (HTTP ") + endpoint.Detail + ")"
                    : T("غير متاح — ", "unavailable — ") + endpoint.Detail));
            _connectivityText.Text = string.Join(Environment.NewLine, lines);
            _connectivityReachable = report.HasAnyReachableSource;
            _connectivityText.Foreground = SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush
                : report.HasAnyReachableSource
                    ? new SolidColorBrush(Color.FromRgb(23, 115, 79))
                    : new SolidColorBrush(Color.FromRgb(168, 50, 74));

            if (!report.HasAnyReachableSource)
                AppendLog(T(
                    "لم يمكن الوصول إلى أي مصدر رسمي في فحص الاتصال. يمكنك المتابعة بالوضع المحدود أو إعادة المحاولة بعد استعادة الإنترنت.",
                    "No official source responded during the connectivity check. You can continue in limited mode or retry after restoring internet access."));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Consent was withdrawn or a newer explicit consent started another check.
        }
        catch (Exception exception)
        {
            if (!IsLoaded) return;
            _connectivityText.Text = T(
                "تعذر إكمال فحص الاتصال: ", "Connectivity inspection could not complete: ") +
                (exception.Message.Length > 500 ? exception.Message[..500] + "…" : exception.Message);
            _connectivityReachable = false;
            _connectivityText.Foreground = SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush
                : new SolidColorBrush(Color.FromRgb(168, 50, 74));
        }
        finally
        {
            if (ReferenceEquals(_connectivityCancellation, cancellation))
            {
                _connectivityCheckInProgress = false;
                _connectivityCancellation.Dispose();
                _connectivityCancellation = null;
                UpdateButtons();
            }
            else
            {
                // An older cancelled check must not clear the progress state of a newer consent check.
                cancellation.Dispose();
            }
        }
    }

    private async Task ChooseResourceDirectoryAsync()
    {
        if (_isRunning || _restartRequested) return;
        CancellationTokenSource? migrationCancellation = null;

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = T(
                "اختر مجلدًا فارغًا على قرص محلي لتخزين موارد الذكاء الاصطناعي. ستظل المحادثات والذاكرة والمستندات في مكانها.",
                "Choose an empty folder on a local drive for AI resources. Chats, memory and documents remain where they are."),
            SelectedPath = Path.GetDirectoryName(LocalVoiceService.DataRoot) ?? LocalVoiceService.DataRoot,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return;

        try
        {
            var source = LocalVoiceService.DataRoot;
            var destination = LocalAiResourcePathPolicy.Validate(dialog.SelectedPath, source);
            if (LocalAiResourcePathPolicy.SamePath(source, destination))
                return;

            if (await _chatSetup.IsServerRunningAsync(CancellationToken.None).ConfigureAwait(true))
                throw new InvalidOperationException(T(
                    "خادم Ollama يعمل الآن. أغلق Ollama بالكامل من أيقونة شريط النظام ثم أعد اختيار المجلد. لم يتم نقل أو تعديل أي ملف.",
                    "The Ollama server is currently running. Fully exit Ollama from the system tray, then choose the folder again. No files were moved or changed."));

            static bool IsInside(string parent, string candidate)
            {
                var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
                return !Path.IsPathRooted(relative) &&
                       relative != "." && relative != ".." &&
                       !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            var migrationSources = new List<LocalAiResourceMigrationSource>
            {
                new(source, ".")
            };
            var oldOllamaModels = OllamaLocalModelSetupService.ModelsDirectory;
            var modelsInsideResourceRoot = LocalAiResourcePathPolicy.SamePath(oldOllamaModels, source) ||
                                           IsInside(source, oldOllamaModels);
            var resourceInsideOllamaRoot = IsInside(oldOllamaModels, source);
            if (resourceInsideOllamaRoot && !LocalAiResourcePathPolicy.SamePath(oldOllamaModels, source))
                throw new InvalidOperationException(T(
                    "مجلد موارد Layal يقع داخل مجلد نماذج Ollama الحالي. غيّر OLLAMA_MODELS إلى مجلد مستقل قبل النقل.",
                    "Layal's resource folder is nested inside the current Ollama model folder. Configure OLLAMA_MODELS to a separate folder before migrating."));

            string newOllamaModelsDirectory;
            if (modelsInsideResourceRoot)
            {
                var relativeModelPath = Path.GetRelativePath(source, oldOllamaModels);
                newOllamaModelsDirectory = relativeModelPath == "."
                    ? destination
                    : Path.GetFullPath(Path.Combine(destination, relativeModelPath));
            }
            else
            {
                if (Directory.Exists(oldOllamaModels))
                    migrationSources.Add(new LocalAiResourceMigrationSource(oldOllamaModels, "ollama-models"));
                newOllamaModelsDirectory = Path.Combine(destination, "ollama-models");
            }

            migrationCancellation = new CancellationTokenSource();
            _setupCancellation = migrationCancellation;
            _isRunning = true;
            _chooseResourceDirectoryButton.IsEnabled = false;
            UpdateButtons();
            _statusText.Text = T("جارٍ نسخ موارد ليال ونموذج Ollama والتحقق من كل ملف إلى المجلد الجديد…",
                                 "Copying Layal resources and the Ollama model store, verifying every file…");
            var copyProgress = new Progress<(long CopiedBytes, long TotalBytes, string CurrentFile)>(item =>
            {
                _statusText.Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("نقل الموارد: {0} من {1} — {2}",
                      "Moving resources: {0} of {1} — {2}"),
                    CompleteSetupPreflightService.FormatBytes(item.CopiedBytes),
                    CompleteSetupPreflightService.FormatBytes(item.TotalBytes),
                    item.CurrentFile);
            });
            var result = await LocalAiResourceDataMigrationService.MigrateAsync(
                migrationSources, destination, copyProgress, migrationCancellation.Token).ConfigureAwait(true);

            var previousResourceDirectory = AppRuntime.Settings.LocalAiDataDirectory;
            var previousModelsDirectory = AppRuntime.Settings.OllamaModelsDirectory;
            AppRuntime.Settings.LocalAiDataDirectory = destination;
            AppRuntime.Settings.OllamaModelsDirectory = newOllamaModelsDirectory;
            try
            {
                AppRuntime.SettingsStore.Save(AppRuntime.Settings);
            }
            catch
            {
                AppRuntime.Settings.LocalAiDataDirectory = previousResourceDirectory;
                AppRuntime.Settings.OllamaModelsDirectory = previousModelsDirectory;
                throw;
            }

            _restartRequested = true;
            _isRunning = false;
            if (ReferenceEquals(_setupCancellation, migrationCancellation))
                _setupCancellation = null;
            migrationCancellation.Dispose();
            _consent.IsChecked = false;
            _resourceDirectoryText.Text = destination;
            _statusText.Text = T(
                "تم نقل " + result.CopiedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " ملف والتحقق من سلامته. ستُعاد تشغيل ليال لتشغيل كل الخدمات على المسار الجديد. المجلد الأصلي لم يُحذف.",
                "Copied and verified " + result.CopiedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " files. Layal will restart so all services use the new location. The original folder was retained.");
            AppendLog(_statusText.Text);
            UpdateButtons();
            DialogResult = false;
        }
        catch (OperationCanceledException) when (migrationCancellation?.IsCancellationRequested == true)
        {
            _statusText.Text = T("تم إلغاء نقل الموارد. المجلد الأصلي لم يتغير.",
                                 "Resource migration was cancelled. The original folder was not changed.");
            _chooseResourceDirectoryButton.IsEnabled = true;
            AppendLog(_statusText.Text);
        }
        catch (Exception exception)
        {
            var detail = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            if (detail.Length > 1000) detail = detail[..1000] + "…";
            _statusText.Text = T("تعذر تغيير مجلد الموارد: ", "Could not change the resource folder: ") + detail;
            AppendLog(_statusText.Text);
            _chooseResourceDirectoryButton.IsEnabled = true;
        }
        finally
        {
            if (ReferenceEquals(_setupCancellation, migrationCancellation))
            {
                _setupCancellation?.Dispose();
                _setupCancellation = null;
            }
            if (!_restartRequested)
                _isRunning = false;
            UpdateButtons();
        }
    }

    private string GetLocalizedResourceName(LocalResourceDefinition resource)
    {
        if (!_isArabic) return resource.DisplayName;
        return resource.Id switch
        {
            LocalResourceIds.PythonRuntime => "بيئة Python x64",
            LocalResourceIds.PyTorch => "PyTorch وTorchAudio",
            LocalResourceIds.OmniVoice => "مكتبة OmniVoice",
            LocalResourceIds.VoiceTut => "مكتبة VoiceTut",
            LocalResourceIds.VoiceTutModel => "أوزان نموذج VoiceTut",
            LocalResourceIds.WhisperRuntime => "محرك Whisper",
            LocalResourceIds.WhisperModel => "نموذج Whisper",
            LocalResourceIds.OllamaRuntime => "محرك Ollama",
            LocalResourceIds.OllamaRuntimeInstaller => "مثبّت Ollama الاحتياطي",
            LocalResourceIds.ChatModel => "نموذج المحادثة Qwen3.5 2B",
            LocalResourceIds.OcrArabic => "بيانات OCR العربية",
            LocalResourceIds.OcrEnglish => "بيانات OCR الإنجليزية",
            _ => resource.DisplayName
        };
    }

    private async Task RefreshResourceProgressAsync()
    {
        if (_isRefreshingResourceProgress || !IsLoaded) return;
        _isRefreshingResourceProgress = true;
        try
        {
            var snapshot = await _resourceManager.GetSnapshotAsync();
            var states = snapshot.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
            foreach (var resource in LocalResourceCatalog.All)
            {
                if (!states.TryGetValue(resource.Id, out var state) ||
                    !_resourceProgressRows.ContainsKey(resource.Id))
                    continue;

                _liveResourceProgress.TryGetValue(resource.Id, out var liveProgress);
                UpdateResourceProgressRow(resource, state, liveProgress);
                if (state.State != LocalResourceLifecycle.Downloading)
                    _liveResourceProgress.Remove(resource.Id);
            }

            var required = LocalResourceCatalog.All.Where(item => item.Required).ToArray();
            var ready = required.Count(item => states.TryGetValue(item.Id, out var state) &&
                                                state.State == LocalResourceLifecycle.Ready &&
                                                string.Equals(state.DefinitionVersion, item.Version, StringComparison.Ordinal));
            var active = required.Count(item => states.TryGetValue(item.Id, out var state) &&
                                                 state.State is LocalResourceLifecycle.Downloading or
                                                     LocalResourceLifecycle.Verifying or LocalResourceLifecycle.Installed);
            var failed = required.Count(item => states.TryGetValue(item.Id, out var state) &&
                                                 state.State == LocalResourceLifecycle.Failed);
            _resourceProgressSummary.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("الموارد الجاهزة: {0}/{1} — قيد العمل: {2} — بها أخطاء: {3}",
                  "Ready resources: {0}/{1} — active: {2} — failed: {3}"),
                ready, required.Length, active, failed);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.Security.SecurityException or System.Text.Json.JsonException)
        {
            _resourceProgressSummary.Text = T(
                "تعذرت قراءة حالة الموارد المحفوظة؛ لن يُفترض أن أي مورد جاهز دون تحقق.",
                "Saved resource state could not be read; no resource will be assumed ready without verification.");
        }
        finally
        {
            _isRefreshingResourceProgress = false;
        }
    }

    private void UpdateResourceProgressRow(
        LocalResourceDefinition resource,
        LocalResourceStateEntry state,
        LocalSpeechProgress? liveProgress)
    {
        var row = _resourceProgressRows[resource.Id];
        var progressBar = row.ProgressBar;
        if (!resource.Required && state.State == LocalResourceLifecycle.Pending)
        {
            row.StateText.Text = T("اختياري — لا يحتاجه مسار التثبيت الحالي", "Optional — not required by the selected setup path");
            row.DetailText.Text = T("يُستخدم هذا المورد الاحتياطي فقط عند الحاجة.", "This fallback resource is used only when needed.");
            progressBar.Visibility = Visibility.Collapsed;
            return;
        }

        row.StateText.Text = state.State switch
        {
            LocalResourceLifecycle.Pending => T("بانتظار التنفيذ", "Waiting to run"),
            LocalResourceLifecycle.Downloading => T("جارٍ التنزيل أو التثبيت", "Downloading or installing"),
            LocalResourceLifecycle.Verifying => T("جارٍ التحقق من الهوية والسلامة", "Verifying identity and integrity"),
            LocalResourceLifecycle.Installed => T("تم التثبيت — جارٍ فحص الصحة", "Installed — health check in progress"),
            LocalResourceLifecycle.Ready => T("جاهز وتم التحقق منه", "Ready and verified"),
            LocalResourceLifecycle.Failed => T("فشل المورد", "Resource failed"),
            _ => T("حالة غير معروفة", "Unknown state")
        };

        if (state.State == LocalResourceLifecycle.Downloading)
        {
            var downloaded = liveProgress?.DownloadedBytes ?? state.BytesDownloaded;
            long? total = liveProgress?.TotalBytes is > 0
                ? liveProgress.TotalBytes
                : state.TotalBytes;
            var speed = liveProgress?.BytesPerSecond ?? state.BytesPerSecond ?? 0;
            var remaining = liveProgress?.RemainingSeconds ?? state.RemainingSeconds;
            progressBar.Visibility = Visibility.Visible;
            if (total is > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = Math.Clamp(downloaded * 100d / total.Value, 0d, 100d);
                var speedText = CompleteSetupPreflightService.FormatBytes(
                    (long)Math.Max(0, speed)) + "/s";
                var etaText = remaining.HasValue
                    ? TimeSpan.FromSeconds(Math.Clamp(remaining.Value, 0, 365 * 24 * 60 * 60)).ToString(@"h\:mm\:ss")
                    : T("جارٍ الحساب", "calculating");
                row.DetailText.Text =
                    CompleteSetupPreflightService.FormatBytes(downloaded) + " / " +
                    CompleteSetupPreflightService.FormatBytes(total.Value) + " — " +
                    speedText + " — " + T("المتبقي ", "remaining ") + etaText;
            }
            else
            {
                progressBar.IsIndeterminate = true;
                row.DetailText.Text = T(
                    "المصدر لا يعلن حجمًا موثوقًا لهذه الخطوة؛ تُعرض حالة العمل دون نسبة تقديرية.",
                    "The source does not publish a reliable size for this step; activity is shown without an estimated percentage.");
            }
            return;
        }

        if (state.State is LocalResourceLifecycle.Verifying or LocalResourceLifecycle.Installed)
        {
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            row.DetailText.Text = T(
                "لا يُعلن المورد جاهزًا إلا بعد اكتمال التحقق واختبار الصحة.",
                "The resource is not marked ready until integrity and health checks pass.");
            return;
        }

        progressBar.IsIndeterminate = false;
        if (state.State == LocalResourceLifecycle.Ready)
        {
            progressBar.Visibility = Visibility.Visible;
            progressBar.Value = 100;
            row.DetailText.Text = T(
                "تم تأكيد الإصدار: ", "Verified definition version: ") +
                (state.DefinitionVersion ?? resource.Version);
        }
        else if (state.State == LocalResourceLifecycle.Failed)
        {
            progressBar.Visibility = Visibility.Collapsed;
            var error = state.ErrorMessage ?? T("لا تتوفر تفاصيل إضافية.", "No additional error details are available.");
            row.DetailText.Text = error.Length <= 320 ? error : error[..320] + "…";
        }
        else
        {
            progressBar.Visibility = Visibility.Collapsed;
            row.DetailText.Text = T("لم يبدأ تجهيز هذا المورد بعد.", "This resource has not started yet.");
        }
    }

    private async Task StartSetupAsync()
    {
        if (_isRunning || _consent.IsChecked != true || !_preflight.IsSupported)
            return;

        _hasAttemptedSetup = true;
        _setupCancellation = new CancellationTokenSource();
        var token = _setupCancellation.Token;
        _isRunning = true;
        _resourceProgressTimer.Start();
        await RefreshResourceProgressAsync();
        _progressBar.Visibility = Visibility.Visible;
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
            _liveResourceProgress[progress.ResourceId] = new LocalSpeechProgress(
                T("تنزيل المورد المحلي", "Downloading local resource"),
                progress.TotalBytes > 0 ? progress.DownloadedBytes * 100d / progress.TotalBytes : null,
                progress.DownloadedBytes,
                progress.TotalBytes,
                progress.BytesPerSecond,
                progress.RemainingSeconds,
                progress.ResourceId);
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
                var resourceName = string.Equals(
                    progress.ResourceId,
                    LocalResourceIds.OllamaRuntimeInstaller,
                    StringComparison.Ordinal)
                    ? T("مثبّت Ollama", "Ollama installer")
                    : T("نموذج المحادثة Qwen3.5 2B", "Qwen3.5 2B chat model");
                _statusText.Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("تنزيل {0}: {1} من {2} — {3} — المتبقي {4}",
                      "Downloading {0}: {1} of {2} — {3} — remaining {4}"),
                    resourceName,
                    CompleteSetupPreflightService.FormatBytes(progress.DownloadedBytes),
                    CompleteSetupPreflightService.FormatBytes(progress.TotalBytes),
                    speed,
                    eta);
            }
        });
        var speechProgress = new Progress<LocalSpeechProgress>(progress =>
        {
            if (!string.IsNullOrWhiteSpace(progress.ResourceId))
                _liveResourceProgress[progress.ResourceId] = progress;
            _statusText.Text = progress.Message;
            if (progress.DownloadedBytes.HasValue &&
                progress.TotalBytes is > 0)
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = Math.Clamp(
                    progress.Percent ?? progress.DownloadedBytes.Value * 100d / progress.TotalBytes.Value,
                    0d, 100d);
                var speed = progress.BytesPerSecond is > 0
                    ? CompleteSetupPreflightService.FormatBytes(
                        (long)Math.Max(0, progress.BytesPerSecond.Value)) + "/s"
                    : T("جارٍ الحساب", "calculating");
                var eta = progress.RemainingSeconds.HasValue
                    ? TimeSpan.FromSeconds(Math.Clamp(
                        progress.RemainingSeconds.Value, 0, 365 * 24 * 60 * 60)).ToString(@"h\:mm\:ss")
                    : T("جارٍ الحساب", "calculating");
                _statusText.Text =
                    progress.Message + " — " +
                    CompleteSetupPreflightService.FormatBytes(progress.DownloadedBytes.Value) +
                    " / " + CompleteSetupPreflightService.FormatBytes(progress.TotalBytes.Value) +
                    " — " + speed + " — " + T("المتبقي ", "remaining ") + eta;
            }
            else if (progress.Percent.HasValue)
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
            var beforeSetup = await _resourceManager.GetSnapshotAsync(token).ConfigureAwait(true);
            var storagePlan = CompleteSetupResourcePlanService.Calculate(beforeSetup);
            var knownReferenceBytes = storagePlan.KnownReferenceBytes;
            var temporarySafetyReserve = knownReferenceBytes / 4;
            var reservations = new List<LocalResourceStorageReservation>
            {
                new(
                    LocalVoiceService.DataRoot,
                    checked(knownReferenceBytes + temporarySafetyReserve))
            };

            var chatModelAlreadyReady = beforeSetup.Any(item =>
                string.Equals(item.ResourceId, LocalResourceIds.ChatModel, StringComparison.Ordinal) &&
                item.State == LocalResourceLifecycle.Ready);
            if (!chatModelAlreadyReady && !OllamaLocalModelSetupService.HasDefaultModelManifest())
                reservations.Add(new LocalResourceStorageReservation(
                    OllamaLocalModelSetupService.ModelsDirectory,
                    3_100_000_000 + 512L * 1024 * 1024));

            if (!OllamaLocalModelSetupService.IsRuntimeInstalled)
            {
                reservations.Add(new LocalResourceStorageReservation(
                    OllamaLocalModelSetupService.RuntimeCapacityDirectory, 4_000_000_000));
                if (!OllamaLocalModelSetupService.IsWinGetAvailable &&
                    OllamaLocalModelSetupService.FallbackInstallerExpectedBytes > 0)
                {
                    reservations.Add(new LocalResourceStorageReservation(
                        LocalVoiceService.DataRoot,
                        checked(OllamaLocalModelSetupService.FallbackInstallerExpectedBytes * 2)));
                }
            }

            LocalResourceCombinedCapacityGuard.EnsureAvailable(reservations);

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
            _isRunning = false;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _statusText.Text = T("تم إيقاف الإعداد بأمان. الموارد التي اكتمل التحقق منها محفوظة.", "Setup was safely cancelled. Resources that passed verification remain available.");
            AppendLog(_statusText.Text);
        }
        catch (InsufficientStorageException exception)
        {
            _statusText.Text = T(
                "المساحة غير كافية على " + exception.DirectoryPath + ". المطلوب " +
                CompleteSetupPreflightService.FormatBytes(exception.RequiredBytes) + " والمتاح " +
                CompleteSetupPreflightService.FormatBytes(exception.AvailableBytes) +
                ". لم يتم حذف الموارد السليمة. اختر مجلدًا على قرص آخر ثم أعد المحاولة.",
                "Insufficient free space at " + exception.DirectoryPath + ". Required " +
                CompleteSetupPreflightService.FormatBytes(exception.RequiredBytes) + ", available " +
                CompleteSetupPreflightService.FormatBytes(exception.AvailableBytes) +
                ". Verified resources were not deleted. Choose a folder on another drive and retry.");
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
            _progressBar.IsIndeterminate = false;
            _progressBar.Visibility = Visibility.Collapsed;
            _setupCancellation?.Dispose();
            _setupCancellation = null;
            await RefreshResourceProgressAsync();
            _resourceProgressTimer.Stop();
            UpdateButtons();
        }
    }

    private void ContinueInLimitedMode()
    {
        if (_isRunning) return;
        _connectivityCancellation?.Cancel();
        CanContinueToMainWindow = true;
        SetupCompleted = false;
        DialogResult = true;
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
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _connectivityCancellation?.Cancel();
        if (!_isRunning) return;
        e.Cancel = true;
        _setupCancellation?.Cancel();
        _statusText.Text = T("جارٍ إيقاف الإعداد قبل الإغلاق…", "Cancelling setup before closing…");
    }

    private void UpdateButtons()
    {
        _startButton.Content = _isRunning
            ? T("جارٍ الإعداد…", "Setting up…")
            : _hasAttemptedSetup
                ? T("أعد المحاولة", "Retry setup")
                : T("ابدأ الإعداد الكامل", "Start full setup");
        _startButton.IsEnabled = !_isRunning && !_restartRequested && !_connectivityCheckInProgress &&
                                 _preflight.IsSupported && _consent.IsChecked == true;
        _chooseResourceDirectoryButton.IsEnabled = !_isRunning && !_restartRequested;
        _limitedModeButton.IsEnabled = !_isRunning && !_restartRequested;
        _cancelButton.IsEnabled = !_restartRequested;
        _cancelButton.Content = _isRunning ? T("إيقاف", "Stop") : T("إغلاق", "Close");
        if (_restartRequested) _cancelButton.Content = T("إعادة التشغيل…", "Restarting…");
    }

    private Border CreateCard(UIElement child)
    {
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush
                : new SolidColorBrush(Color.FromRgb(213, 220, 240)),
            Background = SystemParameters.HighContrast
                ? SystemColors.WindowBrush
                : new SolidColorBrush(Color.FromRgb(250, 251, 255)),
            Child = child
        };
        _themeCards.Add(card);
        return card;
    }

    private TextBlock CreateText(string text, double size, FontWeight weight, Thickness margin)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = _isArabic ? TextAlignment.Right : TextAlignment.Left,
            Margin = margin,
            Foreground = SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush
                : new SolidColorBrush(Color.FromRgb(29, 43, 73))
        };
        _themeTextBlocks.Add(block);
        return block;
    }

    private void OnSystemParametersStaticPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!string.IsNullOrEmpty(args.PropertyName) &&
            !string.Equals(args.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
            return;

        Dispatcher.BeginInvoke(new Action(ApplySystemTheme));
    }

    private void ApplySystemTheme() =>
        ApplySystemTheme(SystemParameters.HighContrast);

    private void ApplySystemTheme(bool highContrast)
    {
        Background = highContrast
            ? SystemColors.WindowBrush
            : new SolidColorBrush(Color.FromRgb(243, 246, 255));

        if (_rootBorder is not null)
        {
            _rootBorder.Background = highContrast ? SystemColors.WindowBrush : Brushes.White;
            _rootBorder.BorderBrush = highContrast
                ? SystemColors.WindowTextBrush
                : new SolidColorBrush(Color.FromRgb(213, 220, 240));
        }

        foreach (var card in _themeCards)
        {
            card.Background = highContrast
                ? SystemColors.WindowBrush
                : new SolidColorBrush(Color.FromRgb(250, 251, 255));
            card.BorderBrush = highContrast
                ? SystemColors.WindowTextBrush
                : new SolidColorBrush(Color.FromRgb(213, 220, 240));
        }

        foreach (var block in _themeTextBlocks)
            block.Foreground = highContrast
                ? SystemColors.WindowTextBrush
                : new SolidColorBrush(Color.FromRgb(29, 43, 73));

        if (_connectivityReachable.HasValue)
            _connectivityText.Foreground = highContrast
                ? SystemColors.WindowTextBrush
                : _connectivityReachable.Value
                    ? new SolidColorBrush(Color.FromRgb(23, 115, 79))
                    : new SolidColorBrush(Color.FromRgb(168, 50, 74));
        else
            _connectivityText.Foreground = SystemColors.WindowTextBrush;

        _resourceDirectoryText.Foreground = SystemColors.WindowTextBrush;
        _statusText.Foreground = SystemColors.WindowTextBrush;
        _diagnostics.Foreground = SystemColors.WindowTextBrush;
        _diagnostics.Background = SystemColors.WindowBrush;
    }

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
