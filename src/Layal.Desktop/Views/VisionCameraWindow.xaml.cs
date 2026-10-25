using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;

namespace Layal.Views;

/// <summary>Captures a local webcam preview and emits still JPEG frames only on request.</summary>
public partial class VisionCameraWindow : System.Windows.Window
{
    private readonly bool _isArabic;
    private VideoCapture? _capture;
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
        CameraPreview.Source = null;
        _latestJpeg = null;
    }
}
