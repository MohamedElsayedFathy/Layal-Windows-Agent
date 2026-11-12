using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using Layal.Services;

namespace Layal.Views;

/// <summary>Captures a local webcam preview and emits still JPEG frames only on request.</summary>
public partial class VisionCameraWindow : System.Windows.Window
{
    private readonly bool _isArabic;
    // OpenCV CAP_PROP_READ_TIMEOUT_MSEC identifier (the pinned wrapper lacks the enum name).
    private const int CaptureReadTimeoutProperty = 54;
    private readonly OnvifCameraDiscoveryService _onvifDiscovery = new();
    private readonly ObservableCollection<OnvifCameraDevice> _discoveredCameras = new();
    private VideoCapture? _capture;
    private bool _isNetworkCamera;
    private bool _switchingSource;
    // Model-free image understanding is not implemented yet. Keep all frame-dispatch paths closed.
    private static readonly bool NativeVisionAnalysisAvailable = false;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _observationTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private byte[]? _latestJpeg;
    private bool _captureReady;
    private bool _frameReadInProgress;
    private Task<byte[]?>? _frameReadTask;
    private bool _isClosing;

    public bool CameraIsRunning => _captureReady && IsVisible;
    public bool IsNetworkCamera => _isNetworkCamera;
    public bool SpeakAutomaticObservations => SpeakCameraObservationsCheckBox.IsChecked == true;

    public event Action<bool>? CameraStatusChanged;
    public event Action<byte[]>? AnalyzeRequested;
    public event Action<byte[]>? ScanSurroundingsRequested;
    public event Action<byte[]>? ObserveRequested;
    public event Action<bool>? AutomaticObservationChanged;

    public VisionCameraWindow(bool isArabic)
    {
        _isArabic = isArabic;
        _lifetimeToken = _lifetimeCancellation.Token;
        InitializeComponent();
        LayalWindowChrome.ApplyGlass(this, isArabic);
        ApplyLanguage();
        DiscoveredCamerasSelector.ItemsSource = _discoveredCameras;
        _previewTimer.Tick += PreviewTimer_Tick;
        _observationTimer.Tick += ObservationTimer_Tick;
        Loaded += VisionCameraWindow_Loaded;
        Closed += VisionCameraWindow_Closed;
    }

    private void ApplyLanguage()
    {
        Title = _isArabic ? "الرؤية بالكاميرا" : "Camera vision";
        VisionPrivacyText.Text = _isArabic
            ? "هذه نافذة معاينة محلية فقط. محرك الرؤية المستقل لم يُنفّذ بعد؛ لذلك أزرار التحليل والمسح والملاحظات الصوتية والتحليل الدوري متوقفة. لا تُرسل أي صورة أو لقطة إلى نموذج أو جهة خارجية. أغلق الكاميرا لإيقاف الالتقاط."
            : "This window provides a local preview only. Layal's independent vision engine is not implemented yet, so analysis, scene scanning, spoken observations, and periodic analysis are disabled. No image or camera frame is sent to a model or external service. Close the camera to stop capture.";
        ContinuousObservationCheckBox.Content = _isArabic ? "التحليل التلقائي غير متاح حاليًا" : "Automatic analysis unavailable";
        AnalyzeCurrentFrameButton.Content = _isArabic ? "تحليل اللقطة غير متاح حاليًا" : "Frame analysis unavailable";
        SpeakCameraObservationsCheckBox.Content = _isArabic ? "النطق التلقائي للملاحظات غير متاح" : "Automatic spoken observations unavailable";
        ScanSurroundingsButton.Content = _isArabic ? "مسح المكان غير متاح حاليًا" : "Scene scan unavailable";
        AutomationProperties.SetName(SpeakCameraObservationsCheckBox, _isArabic
            ? "النطق التلقائي للملاحظات البصرية غير متاح"
            : "Automatic spoken observations unavailable");
        AutomationProperties.SetHelpText(SpeakCameraObservationsCheckBox, _isArabic
            ? "غير متاح حتى تنفيذ محرك الرؤية المستقل؛ لا يتم توليد ملاحظات بصرية."
            : "Unavailable until independent vision is implemented; no visual observations are generated.");
        AutomationProperties.SetName(ScanSurroundingsButton, _isArabic ? "مسح المكان غير متاح" : "Scene scanning unavailable");
        AutomationProperties.SetHelpText(ScanSurroundingsButton, _isArabic
            ? "غير متاح حتى تنفيذ تحليل المشهد المستقل؛ لا يُلتقط إطار لهذا الغرض ولا يُرسل شيء إلى نموذج."
            : "Unavailable until independent scene analysis is implemented; no frame is captured for this purpose or sent to a model.");
        CloseCameraButton.Content = _isArabic ? "إيقاف الكاميرا" : "Stop camera";
        ScanNetworkCamerasButton.Content = _isArabic ? "فحص كاميرات الشبكة المحلية" : "Scan local network cameras";
        UseLocalCameraButton.Content = _isArabic ? "استخدام كاميرا الكمبيوتر" : "Use computer camera";
        ConnectNetworkCameraButton.Content = _isArabic ? "الاتصال بالكاميرا" : "Connect to camera";
        ManualRtspAddressInput.ToolTip = _isArabic
            ? "مثال: rtsp://192.168.1.20:554/path — من دون اسم مستخدم أو كلمة مرور"
            : "Example: rtsp://192.168.1.20:554/path — no username or password in the URL";
        AutomationProperties.SetName(ManualRtspAddressInput, _isArabic ? "عنوان RTSP المحلي لكاميرا مصرح بها" : "Local RTSP address for an authorized camera");
        AutomationProperties.SetHelpText(ManualRtspAddressInput, _isArabic
            ? "استخدم عنوانًا على شبكة خاصة محلية فقط؛ أدخل بيانات الدخول في الحقول المنفصلة."
            : "Use an address on a private local network only; enter credentials in separate fields.");
        NetworkCameraHelpText.Text = _isArabic
            ? "الفحص يبحث عن أجهزة تعلن دعم ONVIF على الشبكة المحلية فقط. يمكنك بدلًا من ذلك إدخال عنوان RTSP محلي من مسؤول الكاميرا. لا تُفتح أي كاميرا حتى تؤكد الاتصال ببيانات دخول صحيحة. لا يُحفظ اسم المستخدم أو كلمة المرور. قد لا يكون RTSP/HTTP مشفرًا."
            : "Discovery searches the local network only for devices advertising ONVIF. Alternatively, enter a private-network RTSP address supplied by the camera administrator. A stream opens only after you confirm valid credentials. Credentials are not saved. RTSP/HTTP may be unencrypted.";
        AutomationProperties.SetName(ScanNetworkCamerasButton, _isArabic ? "فحص كاميرات ONVIF على الشبكة المحلية" : "Scan local network for ONVIF cameras");
        AutomationProperties.SetHelpText(ScanNetworkCamerasButton, _isArabic
            ? "يرسل طلب اكتشاف ONVIF على الشبكة المحلية فقط؛ لا يحاول تخمين كلمات المرور أو فتح بث الكاميرا."
            : "Sends a local ONVIF discovery probe only; it does not guess passwords or open a camera stream.");
        AutomationProperties.SetName(DiscoveredCamerasSelector, _isArabic ? "الكاميرات المكتشفة على الشبكة المحلية" : "Discovered local-network cameras");
        AutomationProperties.SetName(CameraUsernameInput, _isArabic ? "اسم مستخدم كاميرا الشبكة" : "Network camera username");
        AutomationProperties.SetName(CameraPasswordInput, _isArabic ? "كلمة مرور كاميرا الشبكة" : "Network camera password");
        AutomationProperties.SetHelpText(CameraPasswordInput, _isArabic
            ? "تُستخدم مؤقتًا للاتصال بالكاميرا المختارة ولا تُحفظ في الإعدادات."
            : "Used temporarily to connect to the selected camera and never saved in settings.");
        AutomationProperties.SetName(ConnectNetworkCameraButton, _isArabic ? "الاتصال بكاميرا الشبكة المختارة" : "Connect to selected network camera");
        AutomationProperties.SetHelpText(ConnectNetworkCameraButton, _isArabic
            ? "يتطلب بيانات دخول صالحة وصلاحية ONVIF؛ لا توجد محاولة لتجاوز المصادقة."
            : "Requires valid credentials and ONVIF permission; authentication is never bypassed.");
        CameraStatusText.Text = _isArabic ? "جارٍ تهيئة الكاميرا..." : "Preparing camera...";
        AutomationProperties.SetName(CameraPreview, _isArabic ? "المعاينة المباشرة للكاميرا" : "Live camera preview");
        AutomationProperties.SetName(ContinuousObservationCheckBox, _isArabic ? "التحليل البصري التلقائي غير متاح" : "Automatic visual analysis unavailable");
        AutomationProperties.SetHelpText(ContinuousObservationCheckBox, _isArabic
            ? "متوقف حاليًا؛ لا يوجد محرك رؤية مستقل، ولن تُلتقط لقطات للتحليل أو تُرسل إلى أي نموذج."
            : "Currently disabled; no independent vision engine is available, and no frames are captured for analysis or sent to a model.");
        AutomationProperties.SetName(AnalyzeCurrentFrameButton, _isArabic ? "تحليل اللقطة غير متاح" : "Frame analysis unavailable");
        AutomationProperties.SetHelpText(AnalyzeCurrentFrameButton, _isArabic
            ? "لا يوجد محرك رؤية مستقل؛ لا يتم تحليل اللقطة أو إرسالها إلى أي نموذج."
            : "No independent vision engine is available; the frame is not analyzed or sent to any model.");
        AutomationProperties.SetName(CameraStatusText, _isArabic ? "حالة الكاميرا" : "Camera status");
    }

    private async void VisionCameraWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
        {
            CameraStatusText.Text = _isArabic
                ? "درع الخصوصية مفعّل؛ لم تُفتح الكاميرا."
                : "The privacy shield is active; the camera was not opened.";
            StopForPrivacyShield();
            return;
        }
        if (_switchingSource || _captureReady) return;

        _switchingSource = true;
        UseLocalCameraButton.IsEnabled = false;
        ScanNetworkCamerasButton.IsEnabled = false;
        ConnectNetworkCameraButton.IsEnabled = false;
        CameraStatusText.Text = _isArabic ? "جارٍ فتح كاميرا الكمبيوتر..." : "Opening the computer camera...";
        VideoCapture? candidate = null;
        try
        {
            candidate = new VideoCapture();
            var opened = await OpenLocalCaptureAsync(candidate, () => candidate = null, _lifetimeToken);
            _lifetimeToken.ThrowIfCancellationRequested();
            if (_isClosing || !PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
                return;
            if (!opened)
                throw new InvalidOperationException(_isArabic
                    ? "تعذر فتح الكاميرا. تحقق من توصيلها وإذن Windows وعدم استخدامها من تطبيق آخر."
                    : "Could not open the camera. Check its connection, Windows privacy permission, and whether another app is using it.");

            _capture = candidate;
            candidate = null;
            _captureReady = true;
            _isNetworkCamera = false;
            ClearLatestFrame();
            _previewTimer.Start();
            CameraStatusText.Text = _isArabic
                ? "الكاميرا نشطة محليًا — لم تُرسل أي صورة بعد."
                : "Camera is previewing locally — no image has been sent yet.";
            CameraStatusChanged?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                CameraStatusText.Text = _isArabic ? "تم إلغاء فتح الكاميرا." : "Camera startup was cancelled.";
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                _captureReady = false;
                CameraStatusText.Text = (_isArabic ? "تعذر تشغيل الكاميرا: " : "Could not start camera: ") + SafeCameraError(ex);
                AnalyzeCurrentFrameButton.IsEnabled = false;
                CameraStatusChanged?.Invoke(false);
            }
        }
        finally
        {
            candidate?.Dispose();
            if (!_isClosing)
            {
                _switchingSource = false;
                UseLocalCameraButton.IsEnabled = true;
                ScanNetworkCamerasButton.IsEnabled = true;
                ConnectNetworkCameraButton.IsEnabled = true;
            }
        }
    }

        private async void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (!PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
        {
            StopForPrivacyShield();
            return;
        }
        if (!_captureReady || _isClosing || _frameReadInProgress) return;
        var source = _capture;
        if (source is null) return;

        _frameReadInProgress = true;
        var readTask = Task.Run<byte[]?>(() =>
        {
            using var frame = new Mat();
            using var displayFrame = new Mat();
            if (!source.Read(frame) || frame.Empty()) return null;

            var scale = Math.Min(1d, Math.Min(1280d / frame.Width, 900d / frame.Height));
            if (scale < 1d)
            {
                Cv2.Resize(frame, displayFrame,
                    new OpenCvSharp.Size(Math.Max(1, (int)(frame.Width * scale)), Math.Max(1, (int)(frame.Height * scale))),
                    0, 0, InterpolationFlags.Area);
            }
            else
            {
                frame.CopyTo(displayFrame);
            }

            Cv2.ImEncode(".jpg", displayFrame, out var bytes,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 84));
            return bytes.Length == 0 ? null : bytes;
        });
        _frameReadTask = readTask;

        try
        {
            var bytes = await readTask;
            if (bytes is null) return;
            if (_isClosing || !_captureReady || !ReferenceEquals(source, _capture) ||
                !PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
            {
                PrivacyShieldPolicy.ClearCapturedBuffer(bytes);
                if (!_isClosing) StopForPrivacyShield();
                return;
            }

            ClearLatestFrame();
            _latestJpeg = bytes;
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            CameraPreview.Source = bitmap;
            AnalyzeCurrentFrameButton.IsEnabled = true;
            ScanSurroundingsButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (!_isClosing && ReferenceEquals(source, _capture))
                CameraStatusText.Text = (_isArabic ? "تعذر قراءة إطار الكاميرا: " : "Could not read a camera frame: ") + SafeCameraError(ex);
        }
        finally
        {
            _frameReadInProgress = false;
            if (ReferenceEquals(_frameReadTask, readTask))
                _frameReadTask = null;
        }
    }

    private async Task PausePreviewAndWaitForFrameAsync()
    {
        _previewTimer.Stop();
        var currentRead = _frameReadTask;
        if (currentRead is null) return;
        try { await currentRead; }
        catch { /* the preview handler reports recoverable frame errors */ }
    }

    private async void ScanNetworkCamerasButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingSource || !PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
            return;

        var consent = LayalDialogService.Show(this,
            _isArabic
                ? "سيُرسل التطبيق طلب اكتشاف ONVIF متعدد الإرسال إلى الشبكة المحلية لاكتشاف الأجهزة التي تعلن أنها كاميرات. لن يُجرّب كلمات مرور أو يفتح البث أثناء الفحص. لا تتابع إلا إذا كنت مخولًا بفحص هذه الشبكة. هل تريد المتابعة؟"
                : "The app will send an ONVIF multicast discovery probe on the local network to find devices advertising camera services. It will not try passwords or open streams during discovery. Continue only if you are authorized to scan this network. Continue?",
            _isArabic ? "تأكيد فحص الشبكة المحلية" : "Confirm local network scan",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;
        if (_switchingSource || !PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
            return;

        _switchingSource = true;
        ScanNetworkCamerasButton.IsEnabled = false;
        ConnectNetworkCameraButton.IsEnabled = false;
        UseLocalCameraButton.IsEnabled = false;
        CameraStatusText.Text = _isArabic ? "جارٍ البحث عن كاميرات ONVIF محلية لمدة تصل إلى 4 ثوانٍ..." : "Searching for local ONVIF cameras for up to 4 seconds...";
        try
        {
            var cameras = await _onvifDiscovery.DiscoverAsync(TimeSpan.FromSeconds(4), _lifetimeToken);
            _lifetimeToken.ThrowIfCancellationRequested();
            if (_isClosing) return;
            _discoveredCameras.Clear();
            foreach (var camera in cameras)
                _discoveredCameras.Add(camera);
            if (_discoveredCameras.Count > 0)
            {
                DiscoveredCamerasSelector.SelectedIndex = 0;
                CameraStatusText.Text = _isArabic
                    ? $"عُثر على {_discoveredCameras.Count} جهاز ONVIF على الشبكة المحلية. اختر جهازًا واتصل به ببيانات دخول مصرح بها."
                    : $"Found {_discoveredCameras.Count} local ONVIF device(s). Select one and connect with authorized credentials.";
            }
            else
            {
                CameraStatusText.Text = _isArabic
                    ? "لم يُعثر على كاميرات ONVIF. قد تكون الشبكة تمنع multicast أو قد لا تدعم الكاميرا ONVIF. يمكنك استخدام عنوان RTSP الذي يوفره مسؤول الكاميرا لاحقًا."
                    : "No ONVIF cameras were discovered. The network may block multicast, or the camera may not support ONVIF.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                CameraStatusText.Text = _isArabic ? "تم إيقاف فحص الشبكة." : "Network scan was cancelled.";
        }
        catch (Exception ex)
        {
            if (!_isClosing)
                CameraStatusText.Text = (_isArabic ? "تعذر فحص الشبكة المحلية: " : "Local network scan failed: ") + ex.Message;
        }
        finally
        {
            if (!_isClosing)
            {
                _switchingSource = false;
                ScanNetworkCamerasButton.IsEnabled = true;
                ConnectNetworkCameraButton.IsEnabled = true;
                UseLocalCameraButton.IsEnabled = true;
            }
        }
    }

    private async void ConnectNetworkCameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingSource || !PrivacyShieldPolicy.CanStartCapture(
                AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting)) return;

        var manualAddress = ManualRtspAddressInput.Text.Trim();
        var camera = string.IsNullOrWhiteSpace(manualAddress)
            ? DiscoveredCamerasSelector.SelectedItem as OnvifCameraDevice
            : null;
        Uri? manualStreamUri = null;
        var cameraName = camera?.DisplayName ?? string.Empty;
        if (camera is null)
        {
            if (!Uri.TryCreate(manualAddress, UriKind.Absolute, out var parsedStream) ||
                parsedStream.Scheme is not ("rtsp" or "rtsps") ||
                !string.IsNullOrEmpty(parsedStream.UserInfo) ||
                !OnvifCameraDiscoveryService.IsPrivateCameraEndpointHost(parsedStream.Host) ||
                !string.IsNullOrEmpty(parsedStream.Fragment))
            {
                CameraStatusText.Text = _isArabic
                    ? "اختر كاميرا ONVIF مكتشفة أو أدخل عنوان RTSP/RTSPS بعنوان IP خاص محلي ومن دون بيانات دخول داخل الرابط."
                    : "Select a discovered ONVIF camera or enter an RTSP/RTSPS URL with a private local IP and no embedded credentials.";
                return;
            }
            manualStreamUri = parsedStream;
            cameraName = parsedStream.Host;
        }

        var username = CameraUsernameInput.Text.Trim();
        var password = CameraPasswordInput.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            CameraStatusText.Text = _isArabic ? "أدخل اسم المستخدم وكلمة المرور المصرح بهما لهذه الكاميرا." : "Enter the authorized username and password for this camera.";
            return;
        }

        var isHttp = camera is not null &&
                     Uri.TryCreate(camera.DeviceServiceAddress, UriKind.Absolute, out var deviceUri) &&
                     deviceUri.Scheme == Uri.UriSchemeHttp;
        var usesRtspWithoutTls = manualStreamUri?.Scheme == "rtsp" || camera is not null;
        var transportWarning = isHttp || usesRtspWithoutTls
            ? (_isArabic
                ? "تحذير: جزء من الاتصال يستخدم HTTP/RTSP غير المشفر وقد تكون بيانات الدخول أو الفيديو معرضة على الشبكة. "
                : "Warning: HTTP/RTSP transport is unencrypted and credentials or video may be exposed on the network. ")
            : string.Empty;
        var consent = LayalDialogService.Show(this,
            _isArabic
                ? $"سيتم الاتصال بالكاميرا المحلية {cameraName} باستخدام بيانات الدخول التي أدخلتها" +
                  (camera is not null ? " وطلب عنوان بث ONVIF." : " وفتح عنوان RTSP الذي أدخلته.") +
                  " ستظل المعاينة على هذا الكمبيوتر؛ لا تتوفر ميزة تحليل اللقطات ولا تُرسل إلى أي نموذج. لا تُحفظ بيانات الدخول. " +
                  transportWarning + "هل لديك صلاحية مشاهدة هذا البث وتريد المتابعة؟"
                : $"The app will connect to local camera {cameraName} using the credentials you entered" +
                  (camera is not null ? " and request its ONVIF stream URI." : " and open the RTSP address you supplied.") +
                  " Preview stays on this computer; frame analysis is unavailable and no frame is sent to any model. Credentials are not saved. " +
                  transportWarning + "Are you authorized to view this stream and do you want to continue?",
            _isArabic ? "تأكيد اتصال بكاميرا الشبكة" : "Confirm network camera connection",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes)
        {
            CameraPasswordInput.Clear();
            return;
        }
        if (!PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
        {
            CameraPasswordInput.Clear();
            return;
        }

        _switchingSource = true;
        ConnectNetworkCameraButton.IsEnabled = false;
        ScanNetworkCamerasButton.IsEnabled = false;
        UseLocalCameraButton.IsEnabled = false;
        CameraStatusText.Text = _isArabic ? "جارٍ التحقق من صلاحية الكاميرا وفتح البث..." : "Authenticating to the camera and opening the stream...";
        VideoCapture? candidate = null;
        try
        {
            Uri streamUri;
            if (camera is not null)
                streamUri = await _onvifDiscovery.GetAuthenticatedStreamUriAsync(camera, username, password, _lifetimeToken);
            else
                streamUri = manualStreamUri!;

            _lifetimeToken.ThrowIfCancellationRequested();
            if (_isClosing) return;
            var authenticatedAddress = OnvifCameraDiscoveryService.BuildAuthenticatedStreamAddress(streamUri, username, password);
            candidate = new VideoCapture();
            var openingCapture = candidate;
            var opened = await OpenNetworkCaptureAsync(
                openingCapture, authenticatedAddress, () => candidate = null, _lifetimeToken);
            _lifetimeToken.ThrowIfCancellationRequested();
            if (_isClosing) return;
            if (!opened)
                throw new InvalidOperationException(_isArabic
                    ? "تعذر فتح بث RTSP. تحقق من مسار البث وإعدادات الترميز وصلاحيات المشاهدة."
                    : "The RTSP stream could not be opened. Check the stream path, codec, and viewing permissions.");

            StopAutomaticObservation();
            await PausePreviewAndWaitForFrameAsync();
            var previous = _capture;
            _capture = candidate;
            candidate = null;
            _captureReady = true;
            _isNetworkCamera = true;
            ClearLatestFrame();
            try { previous?.Release(); } catch { }
            previous?.Dispose();
            CameraStatusText.Text = _isArabic
                ? "بث كاميرا الشبكة متصل للمعاينة المحلية فقط؛ الرؤية المستقلة غير متاحة ولن تُرسل أي لقطة إلى نموذج."
                : "Network camera stream is connected for local preview only; independent vision is unavailable and no frame will be sent to a model.";
            _previewTimer.Start();
            CameraStatusChanged?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                CameraStatusText.Text = _isArabic ? "تم إلغاء الاتصال بكاميرا الشبكة." : "Network camera connection was cancelled.";
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                // Never show the credential-bearing RTSP URL or exception text from the native media stack.
                CameraStatusText.Text = (_isArabic
                ? "تعذر الاتصال بكاميرا الشبكة. تحقق من بيانات الدخول ودعم ONVIF/RTSP وصلاحيات المشاهدة. التفاصيل الآمنة: "
                : "Could not connect to the network camera. Verify credentials, ONVIF/RTSP support, and viewing permissions. Safe detail: ") +
                SafeCameraError(ex);
            }
        }
        finally
        {
            candidate?.Dispose();
            if (!_isClosing)
            {
                CameraPasswordInput.Clear();
                _switchingSource = false;
                ScanNetworkCamerasButton.IsEnabled = true;
                ConnectNetworkCameraButton.IsEnabled = true;
                UseLocalCameraButton.IsEnabled = true;
            }
        }
    }

    private static async Task<bool> OpenLocalCaptureAsync(
        VideoCapture capture, Action transferOwnership, CancellationToken cancellationToken)
    {
        // Native camera opens cannot be interrupted safely. Run them off the dispatcher and
        // dispose only after Open returns if cancellation or the timeout wins.
        cancellationToken.ThrowIfCancellationRequested();
        var opening = Task.Run(() =>
        {
            if (capture.Open(0, VideoCaptureAPIs.DSHOW)) return true;
            return capture.Open(0, VideoCaptureAPIs.MSMF);
        });
        var timeout = Task.Delay(TimeSpan.FromSeconds(15));
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(opening, timeout, cancelled);
        if (!ReferenceEquals(completed, opening))
        {
            transferOwnership();
            _ = opening.ContinueWith(task =>
            {
                _ = task.Exception;
                capture.Dispose();
            }, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Opening the local camera exceeded 15 seconds.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await opening;
    }
    private static async Task<bool> OpenNetworkCaptureAsync(
        VideoCapture capture, string address, Action transferOwnership, CancellationToken cancellationToken)
    {
        // Native OpenCV open calls cannot be interrupted reliably. On cancellation, transfer
        // ownership to a continuation that disposes the capture only after Open actually returns.
        cancellationToken.ThrowIfCancellationRequested();
        var opening = Task.Run(() => capture.Open(address, VideoCaptureAPIs.FFMPEG));
        var timeout = Task.Delay(TimeSpan.FromSeconds(15));
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(opening, timeout, cancelled);
        if (!ReferenceEquals(completed, opening))
        {
            transferOwnership();
            _ = opening.ContinueWith(task =>
            {
                _ = task.Exception;
                capture.Dispose();
            }, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Opening the local camera stream exceeded 15 seconds.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var opened = await opening;
        if (opened)
            capture.Set((VideoCaptureProperties)CaptureReadTimeoutProperty, 3000);
        return opened;
    }

    private async void UseLocalCameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingSource || !PrivacyShieldPolicy.CanStartCapture(
                AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting)) return;
        _switchingSource = true;
        UseLocalCameraButton.IsEnabled = false;
        ConnectNetworkCameraButton.IsEnabled = false;
        ScanNetworkCamerasButton.IsEnabled = false;
        CameraStatusText.Text = _isArabic ? "جارٍ فتح كاميرا الكمبيوتر..." : "Opening the computer camera...";
        VideoCapture? candidate = null;
        try
        {
            candidate = new VideoCapture();
            var opened = await OpenLocalCaptureAsync(candidate, () => candidate = null, _lifetimeToken);
            _lifetimeToken.ThrowIfCancellationRequested();
            if (_isClosing || !PrivacyShieldPolicy.CanStartCapture(
                    AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
                return;
            if (!opened)
                throw new InvalidOperationException(_isArabic
                    ? "تعذر فتح كاميرا الكمبيوتر. تحقق من إذن Windows ومن عدم استخدامها في تطبيق آخر."
                    : "Could not open the computer camera. Check Windows privacy permission and whether another app is using it.");

            StopAutomaticObservation();
            await PausePreviewAndWaitForFrameAsync();
            var previous = _capture;
            _capture = candidate;
            candidate = null;
            _captureReady = true;
            _isNetworkCamera = false;
            ClearLatestFrame();
            try { previous?.Release(); } catch { }
            previous?.Dispose();
            CameraStatusText.Text = _isArabic
                ? "كاميرا الكمبيوتر تعمل للمعاينة المحلية فقط؛ الرؤية المستقلة غير متاحة ولن تُرسل أي لقطة إلى نموذج."
                : "Computer camera is active for local preview only; independent vision is unavailable and no frame will be sent to a model.";
            _previewTimer.Start();
            CameraStatusChanged?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                CameraStatusText.Text = _isArabic ? "تم إلغاء فتح الكاميرا المحلية." : "Local camera startup was cancelled.";
        }
        catch (Exception ex)
        {
            if (!_isClosing)
                CameraStatusText.Text = SafeCameraError(ex);
        }
        finally
        {
            candidate?.Dispose();
            if (!_isClosing)
            {
                CameraPasswordInput.Clear();
                UseLocalCameraButton.IsEnabled = true;
                ConnectNetworkCameraButton.IsEnabled = true;
                ScanNetworkCamerasButton.IsEnabled = true;
                _switchingSource = false;
            }
        }
    }

        private static string SafeCameraError(Exception ex)
    {
        var message = ex.Message;
        // Native OpenCV/FFmpeg messages sometimes include input URLs. Do not surface credentials.
        foreach (var scheme in new[] { "rtsp://", "rtsps://" })
        {
            var start = message.IndexOf(scheme, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                var end = message.IndexOfAny(new[] { ' ', '\r', '\n', ')', ']' }, start);
                if (end < 0) end = message.Length;
                message = message.Remove(start, end - start).Insert(start, "[stream address redacted]");
            }
        }
        return message;
    }

    /// <summary>Stops the camera immediately and discards its latest in-memory frame.</summary>
    public void StopForPrivacyShield()
    {
        _isClosing = true;
        _captureReady = false;
        _lifetimeCancellation.Cancel();
        _previewTimer.Stop();
        _observationTimer.Stop();
        if (ContinuousObservationCheckBox.IsChecked == true)
            ContinuousObservationCheckBox.IsChecked = false;
        ClearLatestFrame();
        CameraPasswordInput.Clear();
        CameraStatusChanged?.Invoke(false);
        // Owned windows can be hidden with the main window; close even when not visible
        // so the native capture device is released and Closed cleanup always runs.
        try { Close(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Privacy shield could not close the camera window: {0}", ex.Message); }
    }

    private void ClearLatestFrame()
    {
        PrivacyShieldPolicy.ClearCapturedBuffer(_latestJpeg);
        _latestJpeg = null;
        if (CameraPreview is not null)
            CameraPreview.Source = null;
        if (AnalyzeCurrentFrameButton is not null)
            AnalyzeCurrentFrameButton.IsEnabled = false;
        if (ScanSurroundingsButton is not null)
            ScanSurroundingsButton.IsEnabled = false;
    }

    public void StopAutomaticObservation()
    {
        _observationTimer.Stop();
        if (ContinuousObservationCheckBox.IsChecked != true) return;

        ContinuousObservationCheckBox.IsChecked = false;
        CameraStatusText.Text = _isArabic
            ? "تم إيقاف التحليل التلقائي."
            : "Automatic observation has stopped.";
        AutomaticObservationChanged?.Invoke(false);
    }

    private void ObservationTimer_Tick(object? sender, EventArgs e)
    {
        if (!NativeVisionAnalysisAvailable)
        {
            _observationTimer.Stop();
            if (ContinuousObservationCheckBox.IsChecked == true)
            {
                ContinuousObservationCheckBox.IsChecked = false;
                AutomaticObservationChanged?.Invoke(false);
            }
            return;
        }

        if (!PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
        {
            StopForPrivacyShield();
            return;
        }
        if (!_captureReady || _isClosing || ContinuousObservationCheckBox.IsChecked != true || _latestJpeg is null)
            return;

        ObserveRequested?.Invoke(_latestJpeg.ToArray());
    }

    private void ContinuousObservationCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!NativeVisionAnalysisAvailable)
        {
            ContinuousObservationCheckBox.IsChecked = false;
            _observationTimer.Stop();
            CameraStatusText.Text = _isArabic
                ? "التحليل البصري التلقائي غير متاح؛ تظل معاينة الكاميرا محلية فقط."
                : "Automatic visual analysis is unavailable; the camera preview remains local only.";
            AutomaticObservationChanged?.Invoke(false);
            return;
        }

        if (!PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
        {
            ContinuousObservationCheckBox.IsChecked = false;
            _observationTimer.Stop();
            return;
        }
        if (ContinuousObservationCheckBox.IsChecked == true)
        {
            var answer = LayalDialogService.Show(this,
                _isArabic
                    ? "التحليل البصري التلقائي غير متاح في النواة المستقلة الحالية؛ لا يمكن تشغيل هذا الخيار ولن تُرسل أي لقطة إلى نموذج."
                    : "Automatic visual analysis is unavailable in the current native core; this option cannot start and no frame will be sent to a model.",
                _isArabic ? "موافقة على التحليل البصري التلقائي" : "Confirm automatic visual analysis",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                ContinuousObservationCheckBox.IsChecked = false;
                _observationTimer.Stop();
                AutomaticObservationChanged?.Invoke(false);
                return;
            }

            if (!_captureReady || _isClosing ||
                !PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
            {
                ContinuousObservationCheckBox.IsChecked = false;
                _observationTimer.Stop();
                return;
            }

            _observationTimer.Start();
            CameraStatusText.Text = _isArabic
                ? "المعاينة نشطة والتحليل التلقائي مفعّل؛ تُرسل لقطة كل 10 ثوانٍ."
                : "Preview active and automatic analysis enabled; one still is sent every 10 seconds.";
            AutomaticObservationChanged?.Invoke(true);
        }
        else
        {
            _observationTimer.Stop();
            CameraStatusText.Text = _isArabic
                ? "المعاينة محلية والتحليل التلقائي متوقف."
                : "Preview stays local; automatic analysis is off.";
            AutomaticObservationChanged?.Invoke(false);
        }
    }

    private void AnalyzeCurrentFrameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NativeVisionAnalysisAvailable)
        {
            CameraStatusText.Text = _isArabic
                ? "تحليل الصور غير متاح في النواة المستقلة الحالية؛ لم تُرسل أي لقطة."
                : "Image analysis is unavailable in the current native core; no frame was sent.";
            return;
        }
        if (!PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
        {
            StopForPrivacyShield();
            return;
        }
        if (!_captureReady || _latestJpeg is null)
        {
            CameraStatusText.Text = _isArabic ? "لا توجد لقطة متاحة للتحليل." : "No frame is available to analyze.";
            return;
        }

        AnalyzeRequested?.Invoke(_latestJpeg.ToArray());
    }

    private void ScanSurroundingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NativeVisionAnalysisAvailable)
        {
            CameraStatusText.Text = _isArabic
                ? "تحليل المشهد غير متاح في النواة المستقلة الحالية؛ لم تُرسل أي لقطة."
                : "Scene analysis is unavailable in the current native core; no frame was sent.";
            return;
        }
        if (!PrivacyShieldPolicy.CanStartCapture(AppRuntime.Settings.PrivacyShieldEnabled, AppRuntime.IsExiting))
        {
            StopForPrivacyShield();
            return;
        }
        if (!_captureReady || _latestJpeg is null)
        {
            CameraStatusText.Text = _isArabic ? "لا توجد لقطة متاحة لمسح المكان." : "No frame is available to scan the surroundings.";
            return;
        }

        ScanSurroundingsRequested?.Invoke(_latestJpeg.ToArray());
    }

    private void CloseCameraButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void VisionCameraWindow_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _captureReady = false;
        _lifetimeCancellation.Cancel();
        CameraStatusChanged?.Invoke(false);
        _previewTimer.Stop();
        _observationTimer.Stop();
        _previewTimer.Tick -= PreviewTimer_Tick;
        _observationTimer.Tick -= ObservationTimer_Tick;
        if (ContinuousObservationCheckBox.IsChecked == true)
            AutomaticObservationChanged?.Invoke(false);
        var currentRead = _frameReadTask;
        if (currentRead is not null)
        {
            try { await currentRead; } catch { }
        }
        try { _capture?.Release(); } catch { }
        _capture?.Dispose();
        _capture = null;
        CameraPasswordInput.Clear();
        ClearLatestFrame();
        try { _lifetimeCancellation.Dispose(); } catch { }
    }
}
