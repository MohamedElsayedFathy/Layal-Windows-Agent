using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private readonly OnvifCameraDiscoveryService _onvifDiscovery = new();
    private readonly ObservableCollection<OnvifCameraDevice> _discoveredCameras = new();
    private VideoCapture? _capture;
    private bool _isNetworkCamera;
    private bool _switchingSource;
    private readonly Mat _frame = new();
    private readonly Mat _displayFrame = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _observationTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private byte[]? _latestJpeg;
    private bool _captureReady;
    private bool _isClosing;

    public bool CameraIsRunning => _captureReady && IsVisible;

    public event Action<bool>? CameraStatusChanged;
    public event Action<byte[]>? AnalyzeRequested;
    public event Action<byte[]>? ObserveRequested;
    public event Action<bool>? AutomaticObservationChanged;

    public VisionCameraWindow(bool isArabic)
    {
        _isArabic = isArabic;
        InitializeComponent();
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
            ? "المعاينة محلية. لا تُرسل اللقطات إلى مزود الذكاء الاصطناعي إلا عند ضغط «حلّل اللقطة الحالية» أو تشغيل التحليل التلقائي. التحليل التلقائي يرسل صورة ثابتة كل 10 ثوانٍ، وليس بث فيديو. أوقفه أو أغلق الكاميرا في أي وقت. تعابير الوجه مؤشرات مرئية وليست دليلًا قاطعًا على المشاعر."
            : "The preview stays local. Frames are sent to your AI provider only when you analyze one or enable automatic observation. Automatic mode sends one still image every 10 seconds, not a video stream. Turn it off or close the camera at any time. Facial expressions are visible cues, not proof of someone's feelings.";
        ContinuousObservationCheckBox.Content = _isArabic ? "تحليل تلقائي كل 10 ثوانٍ" : "Analyze automatically every 10 seconds";
        AnalyzeCurrentFrameButton.Content = _isArabic ? "حلّل اللقطة الحالية" : "Analyze current frame";
        CloseCameraButton.Content = _isArabic ? "إيقاف الكاميرا" : "Stop camera";
        ScanNetworkCamerasButton.Content = _isArabic ? "فحص كاميرات الشبكة المحلية" : "Scan local network cameras";
        UseLocalCameraButton.Content = _isArabic ? "استخدام كاميرا الكمبيوتر" : "Use computer camera";
        ConnectNetworkCameraButton.Content = _isArabic ? "الاتصال ببث الكاميرا المختارة" : "Connect to selected camera stream";
        NetworkCameraHelpText.Text = _isArabic
            ? "الفحص يبحث عن أجهزة تعلن دعم ONVIF على الشبكة المحلية فقط. لا تُفتح أي كاميرا حتى تختار جهازًا وتؤكد الاتصال ببيانات دخوله الصحيحة. لا يُحفظ اسم المستخدم أو كلمة المرور. قد لا يكون اتصال HTTP مشفرًا."
            : "Discovery searches only the local network for devices advertising ONVIF. A camera stream opens only after you select a device and confirm valid credentials. Credentials are not saved. HTTP connections may not be encrypted.";
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
        AutomationProperties.SetName(ContinuousObservationCheckBox, _isArabic ? "تفعيل التحليل البصري التلقائي" : "Enable automatic visual observation");
        AutomationProperties.SetHelpText(ContinuousObservationCheckBox, _isArabic
            ? "يرسل لقطة ثابتة إلى مزود الذكاء الاصطناعي كل 10 ثوانٍ ما دامت الكاميرا والتحليل التلقائي نشطين."
            : "Sends one still frame to the AI provider every 10 seconds while the camera and automatic observation remain active.");
        AutomationProperties.SetName(CameraStatusText, _isArabic ? "حالة الكاميرا" : "Camera status");
    }

    private void VisionCameraWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _capture = new VideoCapture();
            _captureReady = _capture.Open(0, VideoCaptureAPIs.DSHOW);
            if (!_captureReady)
                _captureReady = _capture.Open(0, VideoCaptureAPIs.MSMF);
            if (!_captureReady)
            {
                CameraStatusText.Text = _isArabic
                    ? "تعذر فتح الكاميرا. تحقق من توصيلها وإذن Windows وعدم استخدامها من تطبيق آخر."
                    : "Could not open the camera. Check its connection, Windows privacy permission, and whether another app is using it.";
                AnalyzeCurrentFrameButton.IsEnabled = false;
                CameraStatusChanged?.Invoke(false);
                return;
            }

            _previewTimer.Start();
            CameraStatusText.Text = _isArabic ? "الكاميرا نشطة محليًا — لم تُرسل أي صورة بعد." : "Camera is previewing locally — no image has been sent yet.";
            CameraStatusChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            _captureReady = false;
            CameraStatusText.Text = (_isArabic ? "تعذر تشغيل الكاميرا: " : "Could not start camera: ") + ex.Message;
            AnalyzeCurrentFrameButton.IsEnabled = false;
            CameraStatusChanged?.Invoke(false);
        }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (!_captureReady || _isClosing) return;
        try
        {
            if (_capture is null || !_capture.Read(_frame) || _frame.Empty()) return;

            var scale = Math.Min(1d, Math.Min(1280d / _frame.Width, 900d / _frame.Height));
            if (scale < 1d)
            {
                Cv2.Resize(_frame, _displayFrame,
                    new OpenCvSharp.Size(Math.Max(1, (int)(_frame.Width * scale)), Math.Max(1, (int)(_frame.Height * scale))),
                    0, 0, InterpolationFlags.Area);
            }
            else
            {
                _frame.CopyTo(_displayFrame);
            }

            Cv2.ImEncode(".jpg", _displayFrame, out var bytes,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 84));
            if (bytes.Length == 0) return;

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
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = (_isArabic ? "تعذر قراءة إطار الكاميرا: " : "Could not read a camera frame: ") + ex.Message;
        }
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

    private async void ScanNetworkCamerasButton_Click(object sender, RoutedEventArgs e)
    {
        var consent = MessageBox.Show(this,
            _isArabic
                ? "سيُرسل التطبيق طلب اكتشاف ONVIF متعدد الإرسال إلى الشبكة المحلية لاكتشاف الأجهزة التي تعلن أنها كاميرات. لن يُجرّب كلمات مرور أو يفتح البث أثناء الفحص. لا تتابع إلا إذا كنت مخولًا بفحص هذه الشبكة. هل تريد المتابعة؟"
                : "The app will send an ONVIF multicast discovery probe on the local network to find devices advertising camera services. It will not try passwords or open streams during discovery. Continue only if you are authorized to scan this network. Continue?",
            _isArabic ? "تأكيد فحص الشبكة المحلية" : "Confirm local network scan",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        ScanNetworkCamerasButton.IsEnabled = false;
        ConnectNetworkCameraButton.IsEnabled = false;
        CameraStatusText.Text = _isArabic ? "جارٍ البحث عن كاميرات ONVIF محلية لمدة تصل إلى 4 ثوانٍ..." : "Searching for local ONVIF cameras for up to 4 seconds...";
        try
        {
            var cameras = await _onvifDiscovery.DiscoverAsync(TimeSpan.FromSeconds(4));
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
            CameraStatusText.Text = _isArabic ? "تم إيقاف فحص الشبكة." : "Network scan was cancelled.";
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = (_isArabic ? "تعذر فحص الشبكة المحلية: " : "Local network scan failed: ") + ex.Message;
        }
        finally
        {
            ScanNetworkCamerasButton.IsEnabled = true;
            ConnectNetworkCameraButton.IsEnabled = _discoveredCameras.Count > 0;
        }
    }

    private async void ConnectNetworkCameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingSource) return;
        if (DiscoveredCamerasSelector.SelectedItem is not OnvifCameraDevice camera)
        {
            CameraStatusText.Text = _isArabic ? "افحص الشبكة ثم اختر كاميرا ONVIF أولًا." : "Scan the network and select an ONVIF camera first.";
            return;
        }

        var username = CameraUsernameInput.Text.Trim();
        var password = CameraPasswordInput.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            CameraStatusText.Text = _isArabic ? "أدخل اسم المستخدم وكلمة المرور المصرح بهما لهذه الكاميرا." : "Enter the authorized username and password for this camera.";
            return;
        }

        var isHttp = Uri.TryCreate(camera.DeviceServiceAddress, UriKind.Absolute, out var deviceUri) &&
                     deviceUri.Scheme == Uri.UriSchemeHttp;
        var consent = MessageBox.Show(this,
            _isArabic
                ? $"سيتم الاتصال بالجهاز {camera.DisplayName} باستخدام بيانات الدخول التي أدخلتها وطلب عنوان بث ONVIF. ستظل المعاينة على هذا الكمبيوتر؛ لا تُرسل اللقطات إلى نموذج AI إلا عند تحليلها. لا تُحفظ بيانات الدخول. " +
                  (isHttp ? "تحذير: عنوان الجهاز يستخدم HTTP، وقد لا تكون حماية النقل مشفرة بالكامل. " : string.Empty) +
                  "هل لديك صلاحية مشاهدة هذا البث وتريد المتابعة؟"
                : $"The app will authenticate to {camera.DisplayName} and request its ONVIF stream address. Preview stays on this computer; frames are sent to AI only when analyzed. Credentials are not saved. " +
                  (isHttp ? "Warning: the device endpoint uses HTTP, so transport may not be fully encrypted. " : string.Empty) +
                  "Are you authorized to view this stream and do you want to continue?",
            _isArabic ? "تأكيد اتصال بكاميرا الشبكة" : "Confirm network camera connection",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes)
        {
            CameraPasswordInput.Clear();
            return;
        }

        _switchingSource = true;
        ConnectNetworkCameraButton.IsEnabled = false;
        ScanNetworkCamerasButton.IsEnabled = false;
        CameraStatusText.Text = _isArabic ? "جارٍ التحقق من صلاحية ONVIF وفتح البث..." : "Authenticating to ONVIF and opening the stream...";
        VideoCapture? candidate = null;
        try
        {
            var streamUri = await _onvifDiscovery.GetAuthenticatedStreamUriAsync(camera, username, password);
            var authenticatedAddress = OnvifCameraDiscoveryService.BuildAuthenticatedStreamAddress(streamUri, username, password);
            candidate = new VideoCapture();
            var opened = await Task.Run(() => candidate.Open(authenticatedAddress, VideoCaptureAPIs.FFMPEG));
            if (!opened)
                throw new InvalidOperationException(_isArabic
                    ? "تم التحقق من ONVIF، لكن تعذر فتح بث RTSP. تحقق من مسار البث وإعدادات الترميز وصلاحيات المشاهدة."
                    : "ONVIF was reached, but the RTSP stream could not be opened. Check the stream path, codec, and viewing permissions.");

            StopAutomaticObservation();
            _previewTimer.Stop();
            var previous = _capture;
            _capture = candidate;
            candidate = null;
            _captureReady = true;
            _isNetworkCamera = true;
            _latestJpeg = null;
            try { previous?.Release(); } catch { }
            previous?.Dispose();
            CameraStatusText.Text = _isArabic
                ? "بث كاميرا الشبكة متصل محليًا؛ لم تُرسل أي لقطة إلى نموذج AI بعد."
                : "Network camera stream connected locally; no frame has been sent to AI yet.";
            _previewTimer.Start();
            CameraStatusChanged?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            CameraStatusText.Text = _isArabic ? "تم إلغاء الاتصال بكاميرا الشبكة." : "Network camera connection was cancelled.";
        }
        catch (Exception ex)
        {
            // Never show the credential-bearing RTSP URL or exception text from the native media stack.
            CameraStatusText.Text = (_isArabic
                ? "تعذر الاتصال بكاميرا الشبكة. تحقق من بيانات الدخول ودعم ONVIF/RTSP وصلاحيات المشاهدة. التفاصيل الآمنة: "
                : "Could not connect to the network camera. Verify credentials, ONVIF/RTSP support, and viewing permissions. Safe detail: ") +
                SafeCameraError(ex);
        }
        finally
        {
            candidate?.Dispose();
            CameraPasswordInput.Clear();
            _switchingSource = false;
            ScanNetworkCamerasButton.IsEnabled = true;
            ConnectNetworkCameraButton.IsEnabled = _discoveredCameras.Count > 0;
        }
    }

    private async void UseLocalCameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingSource) return;
        _switchingSource = true;
        UseLocalCameraButton.IsEnabled = false;
        CameraStatusText.Text = _isArabic ? "جارٍ فتح كاميرا الكمبيوتر..." : "Opening the computer camera...";
        VideoCapture? candidate = null;
        try
        {
            candidate = new VideoCapture();
            var opened = await Task.Run(() =>
            {
                if (candidate.Open(0, VideoCaptureAPIs.DSHOW)) return true;
                return candidate.Open(0, VideoCaptureAPIs.MSMF);
            });
            if (!opened)
                throw new InvalidOperationException(_isArabic
                    ? "تعذر فتح كاميرا الكمبيوتر. تحقق من إذن Windows ومن عدم استخدامها في تطبيق آخر."
                    : "Could not open the computer camera. Check Windows privacy permission and whether another app is using it.");

            StopAutomaticObservation();
            _previewTimer.Stop();
            var previous = _capture;
            _capture = candidate;
            candidate = null;
            _captureReady = true;
            _isNetworkCamera = false;
            _latestJpeg = null;
            try { previous?.Release(); } catch { }
            previous?.Dispose();
            CameraStatusText.Text = _isArabic
                ? "كاميرا الكمبيوتر نشطة محليًا؛ لم تُرسل أي لقطة إلى نموذج AI بعد."
                : "Computer camera is previewing locally; no frame has been sent to AI yet.";
            _previewTimer.Start();
            CameraStatusChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = SafeCameraError(ex);
        }
        finally
        {
            candidate?.Dispose();
            CameraPasswordInput.Clear();
            UseLocalCameraButton.IsEnabled = true;
            _switchingSource = false;
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
                var end = message.IndexOfAny(new[] { ' ', '\\r', '\\n', ')', ']' }, start);
                if (end < 0) end = message.Length;
                message = message.Remove(start, end - start).Insert(start, "[stream address redacted]");
            }
        }
        return message;
    }

    private void ObservationTimer_Tick(object? sender, EventArgs e)
    {
        if (!_captureReady || _isClosing || ContinuousObservationCheckBox.IsChecked != true || _latestJpeg is null)
            return;

        ObserveRequested?.Invoke(_latestJpeg.ToArray());
    }

    private void ContinuousObservationCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (ContinuousObservationCheckBox.IsChecked == true)
        {
            var answer = MessageBox.Show(this,
                _isArabic
                    ? "سيُرسل إطار صورة ثابت إلى مزود الذكاء الاصطناعي كل 10 ثوانٍ أثناء تفعيل هذا الخيار. لن يُرسل بث الفيديو الخام، ويمكنك إيقاف التحليل في أي وقت. هل تريد المتابعة؟"
                    : "A still image will be sent to your AI provider every 10 seconds while this option is enabled. Raw video is not streamed, and you can stop this at any time. Continue?",
                _isArabic ? "موافقة على التحليل البصري التلقائي" : "Confirm automatic visual analysis",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                ContinuousObservationCheckBox.IsChecked = false;
                _observationTimer.Stop();
                AutomaticObservationChanged?.Invoke(false);
                return;
            }

            if (!_captureReady)
            {
                ContinuousObservationCheckBox.IsChecked = false;
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
        if (!_captureReady || _latestJpeg is null)
        {
            CameraStatusText.Text = _isArabic ? "لا توجد لقطة متاحة للتحليل." : "No frame is available to analyze.";
            return;
        }

        AnalyzeRequested?.Invoke(_latestJpeg.ToArray());
    }

    private void CloseCameraButton_Click(object sender, RoutedEventArgs e) => Close();

    private void VisionCameraWindow_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        CameraStatusChanged?.Invoke(false);
        _previewTimer.Stop();
        _observationTimer.Stop();
        _previewTimer.Tick -= PreviewTimer_Tick;
        _observationTimer.Tick -= ObservationTimer_Tick;
        if (ContinuousObservationCheckBox.IsChecked == true)
            AutomaticObservationChanged?.Invoke(false);
        try { _capture?.Release(); } catch { }
        _capture?.Dispose();
        _frame.Dispose();
        _displayFrame.Dispose();
        CameraPasswordInput.Clear();
        CameraPreview.Source = null;
        _latestJpeg = null;
    }
}
