using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Layal.Services;

namespace Layal.Views;

/// <summary>Native, transparent Layal mascot window. Facial states are driven by actual agent events.</summary>
public partial class FloatingMascotWindow : Window
{
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _restoreEyesTimer;
    private readonly DispatcherTimer _floatTimer;
    private readonly DispatcherTimer _moodResetTimer;
    private readonly DispatcherTimer _idleTimer;
    private double _floatPhase;
    private bool _reactionActive;
    private DateTimeOffset _reactionStartedAt;
    private DateTimeOffset _lastReactionShownAt = DateTimeOffset.MinValue;
    private string _lastReactionMood = string.Empty;
    private readonly Dictionary<string, int> _reactionIndexes = new(StringComparer.OrdinalIgnoreCase);
    private int _speechFrame;
    private bool _blinked;
    private bool _isPointerOver;
    private bool _pointerDown;
    private bool _dragStarted;
    private Point _pointerDownPosition;
    private DateTimeOffset _lastInteractionAt = DateTimeOffset.UtcNow;
    private string _lastTapMood = string.Empty;
    private string _lastReactionEmoji = string.Empty;
    private double _pointerOffsetX;
    private double _pointerOffsetY;
    private double _leftPupilMoodX;
    private double _leftPupilMoodY;
    private double _rightPupilMoodX;
    private double _rightPupilMoodY;
    private string _mood = "idle";
    private readonly ContextMenu _contextMenu = new();
    private readonly MenuItem _openMenuItem = new();
    private readonly MenuItem _hideMenuItem = new();
    private readonly MenuItem _exitMenuItem = new();

    private static readonly Brush EyeWhite = new SolidColorBrush(Color.FromRgb(247, 249, 255));
    private static readonly Brush BrowNeutral = new SolidColorBrush(Color.FromRgb(171, 189, 249));
    private static readonly Brush AccentCyan = new SolidColorBrush(Color.FromRgb(89, 225, 230));
    private static readonly Brush AccentPurple = new SolidColorBrush(Color.FromRgb(167, 139, 250));
    private static readonly Brush AccentGreen = new SolidColorBrush(Color.FromRgb(77, 225, 161));
    private static readonly Brush AccentAmber = new SolidColorBrush(Color.FromRgb(245, 201, 111));
    private static readonly Brush AccentRed = new SolidColorBrush(Color.FromRgb(255, 113, 142));
    private static readonly Brush AccentMuted = new SolidColorBrush(Color.FromRgb(157, 171, 199));

    private static readonly Dictionary<string, string[]> ReactionSets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = new[] { "😊", "🌸", "✨", "🫧", "💫", "🌈", "🐣", "💗", "🌷", "⭐", "🩷", "☀️" },
        ["curious"] = new[] { "👀", "🤔", "🧐", "❓", "💡", "🪄", "✨", "🔍", "🫣", "😮", "🦉", "🙋", "🐾", "❔" },
        ["thinking"] = new[] { "💭", "🤔", "🧠", "💡", "🔮", "🧐", "📝", "☁️", "💫", "🧩", "🙃", "🤓", "🌀" },
        ["planning"] = new[] { "🗺️", "💡", "📝", "📋", "🧭", "🗓️", "🪄", "🧠", "🧩", "📌", "🗂️", "🎯", "🪜", "🚦" },
        ["researching"] = new[] { "🔎", "📚", "🧐", "🗞️", "🔬", "📖", "🌐", "🧭", "💡", "📝", "🗃️", "🦉", "🪄", "📊" },
        ["reading"] = new[] { "📖", "👓", "💡", "📚", "🤓", "🔖", "📘", "🧐", "✍️", "💭", "📑", "🦉", "📗", "🧾" },
        ["coding"] = new[] { "💻", "🧩", "⚙️", "⌨️", "👩‍💻", "🛠️", "🤓", "🔧", "🐞", "🚀", "🧠", "✨", "🖥️", "🧪" },
        ["using_tool"] = new[] { "🛠️", "⚙️", "🔧", "🧰", "🔩", "🪛", "🤖", "🚀", "✨", "🧪", "🔋", "🔌", "🪄", "📦" },
        ["listening"] = new[] { "👂", "🎧", "👀", "🫶", "🙂", "💗", "🙋", "🗣️", "💭", "🐰", "💬", "✨" },
        ["transcribing"] = new[] { "📝", "✍️", "🎧", "🗒️", "⌨️", "📋", "🔤", "✅", "✨", "🗣️", "🦻", "📄" },
        ["speaking"] = new[] { "💬", "🗨️", "👋", "🗣️", "😊", "💖", "💌", "📣", "✨", "🎤", "🫶", "😄", "🗯️", "💞" },
        ["excited"] = new[] { "🤩", "🥳", "💖", "🎉", "🌟", "🩷", "💃", "🪩", "🚀", "🎊", "😆", "🐣", "💫", "🎈", "😍", "🌈" },
        ["success"] = new[] { "🎉", "✅", "✨", "💖", "🏆", "🌟", "🥳", "👏", "🙌", "💯", "🎊", "💐", "🚀", "🤩", "🪄", "🩷" },
        ["warning"] = new[] { "⚠️", "❗", "🫣", "🛑", "🙈", "😬", "👀", "🔔", "🚧", "🟠", "🧯", "🚦", "🤨", "🫢" },
        ["error"] = new[] { "😵‍💫", "🙈", "🛑", "😓", "🫠", "💔", "😣", "🧯", "🚨", "🆘", "😵", "🤦", "🥴", "🫥" },
        ["paused"] = new[] { "⏸️", "😌", "💤", "☕", "🫖", "🧘", "🌙", "⏳", "🍵", "💭", "🧸", "🌿" },
        ["interrupted"] = new[] { "✋", "🛑", "💭", "🙋", "🔔", "🖐️", "🤚", "⏸️", "🚦", "👀", "🫷", "🫸" },
        ["recovering"] = new[] { "🔄", "🤞", "🛠️", "💪", "🩹", "🧩", "🌈", "🚀", "🧰", "🧪", "💡", "✨", "🫡", "🪄" },
        ["reconnecting"] = new[] { "🔄", "📡", "🤞", "📶", "🔌", "🛰️", "📲", "🔋", "🌐", "🛜", "🔗", "✨", "🧭", "💫" },
        ["offline"] = new[] { "🔌", "📴", "💤", "🌙", "🔋", "🪫", "📡", "😴", "☁️", "🧸", "🛑", "🌧️" },
        ["privacy"] = new[] { "🔒", "🤫", "🛡️", "🔐", "🗝️", "👀", "🧘", "🕵️‍♀️", "🔏", "🫶", "🤐", "🪪" },
        ["camera_processing"] = new[] { "📷", "👁️", "🔍", "🎥", "📸", "🧐", "🔬", "🧿", "🎯", "👀", "📹", "💡", "🖼️", "🌟" },
        ["sleepy"] = new[] { "🥱", "🌙", "💤", "🛌", "😴", "☕", "🧸", "🌃", "⭐", "😪", "🫠", "🦉" },
        ["responding"] = new[] { "💬", "🗨️", "🗣️", "✨", "😊", "🫶" },
        ["happy"] = new[] { "😊", "🌸", "💖", "✨", "🌈", "🩷" },
        ["greeting"] = new[] { "👋", "🌞", "😊", "💐", "✨", "🌷" },
        ["love"] = new[] { "💖", "💗", "🩷", "🥰", "💕", "💞" },
        ["laughing"] = new[] { "😂", "🤣", "😆", "😹", "🎉", "💫" },
        ["wink"] = new[] { "😉", "😜", "✨", "😏", "🫣" },
        ["surprised"] = new[] { "😲", "😮", "🤯", "✨", "‼️", "🫢" },
        ["confused"] = new[] { "🤔", "😵‍💫", "❓", "🫤", "🧐" },
        ["sad"] = new[] { "🥺", "😢", "🌧️", "💧", "🫂" },
        ["worried"] = new[] { "😟", "🥺", "🫣", "💭", "🫂" },
        ["proud"] = new[] { "🏆", "👏", "🌟", "💯", "🎉", "🙌" },
        ["shy"] = new[] { "☺️", "🫣", "🌸", "💗", "🙈" },
        ["annoyed"] = new[] { "😒", "🙄", "😤", "💢", "🫠" },
        ["bored"] = new[] { "🥱", "😑", "💤", "☕", "🌙" },
        ["playful"] = new[] { "😜", "😉", "🐣", "🎈", "✨", "😎" }
    };

    public FloatingMascotWindow()
    {
        InitializeComponent();
        Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
        Opacity = AppRuntime.Settings.MascotOpacity;
        ApplyScale(AppRuntime.Settings.MascotScale);

        var hasSavedPosition = AppRuntime.Settings.MascotLeft != -1 || AppRuntime.Settings.MascotTop != -1;
        var desiredLeft = hasSavedPosition
            ? AppRuntime.Settings.MascotLeft
            : SystemParameters.WorkArea.Right - Width - 28;
        var desiredTop = hasSavedPosition
            ? AppRuntime.Settings.MascotTop
            : SystemParameters.WorkArea.Top + 70;
        (Left, Top) = ClampToVirtualScreen(desiredLeft, desiredTop);

        _openMenuItem.Click += OpenMenuItem_Click;
        _hideMenuItem.Click += HideMenuItem_Click;
        _exitMenuItem.Click += ExitMenuItem_Click;
        _contextMenu.Items.Add(_openMenuItem);
        _contextMenu.Items.Add(_hideMenuItem);
        _contextMenu.Items.Add(new Separator());
        _contextMenu.Items.Add(_exitMenuItem);
        ContextMenu = _contextMenu;

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3650) };
        _blinkTimer.Tick += (_, _) => Blink();
        _restoreEyesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(135) };
        _restoreEyesTimer.Tick += (_, _) => RestoreEyes();
        _floatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _floatTimer.Tick += (_, _) => AnimateMoodTick();
        _moodResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _moodResetTimer.Tick += (_, _) => ResetTerminalMood();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleTimer.Tick += (_, _) => UpdateIdleState();
        _blinkTimer.Start();
        _idleTimer.Start();
        _floatTimer.Start();
        UpdateLanguage();
        SetMood("idle");
    }

    public string CurrentMood => _mood;

    public void ShowAgain()
    {
        if (!IsVisible) Show();
        Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
        if (!AppRuntime.Settings.MascotAnimationsEnabled) Root.RenderTransform = Transform.Identity;
    }

    public void UpdateLanguage()
    {
        var ar = AppRuntime.Settings.Language == "ar";
        Title = ar ? "ليال" : "Layal";
        var localizedToolTip = ar ? "ليال | انقر مرتين لفتح الواجهة" : "Layal | Double-click to open the full interface";
        ToolTip = localizedToolTip;
        Root.ToolTip = localizedToolTip;
        _openMenuItem.Header = ar ? "فتح ليال" : "Open Layal";
        _hideMenuItem.Header = ar ? "إخفاء الشخصية" : "Hide mascot";
        _exitMenuItem.Header = ar ? "خروج" : "Exit";
    }

    public void SetMood(string mood, bool showReaction = true)
    {
        var normalizedMood = NormalizeMood(mood);
        if (!string.Equals(_mood, normalizedMood, StringComparison.OrdinalIgnoreCase))
            _lastInteractionAt = DateTimeOffset.UtcNow;

        var previousMood = _mood;
        _mood = normalizedMood;
        _speechFrame = 0;
        _leftPupilMoodX = _leftPupilMoodY = _rightPupilMoodX = _rightPupilMoodY = 0;
        LeftPupil.RenderTransform = Transform.Identity;
        RightPupil.RenderTransform = Transform.Identity;
        LeftEyeShine.RenderTransform = RightEyeShine.RenderTransform = Transform.Identity;
        LeftEye.Fill = RightEye.Fill = EyeWhite;
        LeftEye.Height = RightEye.Height = 22;
        LeftPupil.Visibility = RightPupil.Visibility = Visibility.Visible;
        LeftEyeShine.Visibility = RightEyeShine.Visibility = Visibility.Visible;
        LeftBrow.Stroke = RightBrow.Stroke = BrowNeutral;
        LeftBrow.Data = Geometry.Parse("M 26,18 Q 34,14 42,18");
        RightBrow.Data = Geometry.Parse("M 61,18 Q 69,14 77,18");
        CheekLeft.Opacity = CheekRight.Opacity = 0.62;
        Mouth.Stroke = EyeWhite;
        Mouth.Fill = Brushes.Transparent;
        Mouth.StrokeThickness = 3.4;
        GlowHalo.Opacity = _mood is "offline" or "privacy" ? 0.24 : 0.58;
        var status = AccentGreen;

        switch (_mood)
        {
            case "listening":
                LeftEye.Height = RightEye.Height = 25;
                LeftBrow.Data = Geometry.Parse("M 26,16 Q 34,11 42,16");
                RightBrow.Data = Geometry.Parse("M 61,16 Q 69,11 77,16");
                Mouth.Data = Geometry.Parse("M 38,54 Q 51,62 65,54");
                status = AccentCyan;
                break;
            case "transcribing":
                LeftEye.Height = RightEye.Height = 21;
                _leftPupilMoodY = 1;
                _rightPupilMoodY = 1;
                Mouth.Data = Geometry.Parse("M 38,56 Q 51,53 65,56");
                status = AccentPurple;
                break;
            case "excited":
                LeftEye.Height = RightEye.Height = 25;
                LeftBrow.Data = Geometry.Parse("M 25,15 Q 34,9 43,15");
                RightBrow.Data = Geometry.Parse("M 60,15 Q 69,9 78,15");
                Mouth.Data = Geometry.Parse("M 26,46 Q 51,82 80,46");
                CheekLeft.Opacity = CheekRight.Opacity = 1;
                GlowHalo.Opacity = 0.84;
                status = AccentGreen;
                break;
            case "sleepy":
                LeftEye.Height = RightEye.Height = 13;
                LeftBrow.Data = Geometry.Parse("M 26,22 Q 34,19 42,22");
                RightBrow.Data = Geometry.Parse("M 61,22 Q 69,19 77,22");
                Mouth.Data = Geometry.Parse("M 42,58 Q 51,60 60,58");
                CheekLeft.Opacity = CheekRight.Opacity = 0.36;
                status = AccentMuted;
                break;
            case "understanding":
            case "curious":
                LeftBrow.Data = Geometry.Parse("M 25,15 Q 34,10 43,15");
                RightBrow.Data = Geometry.Parse("M 61,18 Q 69,14 77,18");
                _leftPupilMoodX = 1.3; _leftPupilMoodY = -1.5;
                _rightPupilMoodX = 1.3; _rightPupilMoodY = -1.5;
                Mouth.Data = Geometry.Parse("M 43,54 Q 51,59 59,54");
                status = AccentCyan;
                break;
            case "thinking":
            case "planning":
            case "researching":
            case "reading":
            case "coding":
            case "using_tool":
            case "waiting":
                LeftBrow.Data = Geometry.Parse("M 26,18 Q 34,15 42,17");
                RightBrow.Data = Geometry.Parse("M 61,14 Q 69,10 77,14");
                _leftPupilMoodX = 1.5; _leftPupilMoodY = -2.0;
                _rightPupilMoodX = 2.0; _rightPupilMoodY = -2.0;
                Mouth.Data = Geometry.Parse("M 40,56 Q 51,51 64,56");
                status = AccentAmber;
                break;
            case "responding":
            case "speaking":
                LeftEye.Height = RightEye.Height = 23;
                Mouth.Data = Geometry.Parse("M 39,50 Q 51,73 65,50 Q 52,66 39,50 Z");
                Mouth.Fill = new SolidColorBrush(Color.FromRgb(103, 44, 88));
                Mouth.StrokeThickness = 2.6;
                status = AccentCyan;
                break;
            case "greeting":
            case "happy":
                LeftEye.Height = RightEye.Height = 24;
                LeftBrow.Data = Geometry.Parse("M 25,16 Q 34,10 43,16");
                RightBrow.Data = Geometry.Parse("M 60,16 Q 69,10 78,16");
                Mouth.Data = Geometry.Parse("M 27,47 Q 51,80 79,47");
                CheekLeft.Opacity = CheekRight.Opacity = 0.96;
                GlowHalo.Opacity = 0.70;
                status = AccentGreen;
                break;
            case "love":
            case "shy":
                LeftEye.Fill = RightEye.Fill = new SolidColorBrush(Color.FromRgb(255, 209, 231));
                LeftBrow.Data = Geometry.Parse("M 25,17 Q 34,12 43,18");
                RightBrow.Data = Geometry.Parse("M 60,18 Q 69,12 78,17");
                _leftPupilMoodX = 0.5; _leftPupilMoodY = 1.8;
                _rightPupilMoodX = 0.5; _rightPupilMoodY = 1.8;
                Mouth.Data = Geometry.Parse("M 39,54 Q 51,63 64,54");
                CheekLeft.Opacity = CheekRight.Opacity = 1;
                GlowHalo.Opacity = 0.78;
                status = AccentRed;
                break;
            case "laughing":
                LeftEye.Height = RightEye.Height = 6;
                LeftPupil.Visibility = RightPupil.Visibility = Visibility.Collapsed;
                LeftEyeShine.Visibility = RightEyeShine.Visibility = Visibility.Collapsed;
                LeftBrow.Data = Geometry.Parse("M 25,17 Q 34,10 43,16");
                RightBrow.Data = Geometry.Parse("M 60,16 Q 69,10 78,17");
                Mouth.Data = Geometry.Parse("M 30,46 Q 51,88 80,46 Z");
                Mouth.Fill = new SolidColorBrush(Color.FromRgb(119, 48, 90));
                CheekLeft.Opacity = CheekRight.Opacity = 1;
                GlowHalo.Opacity = 0.78;
                status = AccentPurple;
                break;
            case "wink":
            case "playful":
                LeftEye.Height = 3;
                LeftPupil.Visibility = LeftEyeShine.Visibility = Visibility.Collapsed;
                RightEye.Height = 25;
                LeftBrow.Data = Geometry.Parse("M 25,20 Q 34,25 43,20");
                RightBrow.Data = Geometry.Parse("M 60,14 Q 69,9 78,15");
                Mouth.Data = Geometry.Parse("M 28,48 Q 51,79 79,48");
                CheekLeft.Opacity = CheekRight.Opacity = 0.94;
                status = AccentPurple;
                break;
            case "surprised":
                LeftEye.Height = RightEye.Height = 27;
                LeftBrow.Data = Geometry.Parse("M 25,12 Q 34,6 43,12");
                RightBrow.Data = Geometry.Parse("M 60,12 Q 69,6 78,12");
                Mouth.Data = Geometry.Parse("M 44,51 Q 51,46 58,51 Q 61,61 51,66 Q 41,61 44,51 Z");
                Mouth.Fill = new SolidColorBrush(Color.FromRgb(103, 44, 88));
                CheekLeft.Opacity = CheekRight.Opacity = 0.50;
                status = AccentCyan;
                break;
            case "confused":
                LeftBrow.Data = Geometry.Parse("M 25,13 Q 34,7 43,14");
                RightBrow.Data = Geometry.Parse("M 60,22 Q 69,18 78,22");
                _leftPupilMoodX = -2; _leftPupilMoodY = -1;
                _rightPupilMoodX = 2; _rightPupilMoodY = 1;
                Mouth.Data = Geometry.Parse("M 39,58 Q 46,52 53,58 Q 60,64 66,57");
                status = AccentAmber;
                break;
            case "sad":
                LeftBrow.Data = Geometry.Parse("M 25,17 Q 34,24 43,21");
                RightBrow.Data = Geometry.Parse("M 60,21 Q 69,24 78,17");
                Mouth.Data = Geometry.Parse("M 32,63 Q 51,44 71,63");
                CheekLeft.Opacity = CheekRight.Opacity = 0.28;
                GlowHalo.Opacity = 0.34;
                status = AccentMuted;
                break;
            case "worried":
                LeftBrow.Data = Geometry.Parse("M 25,16 Q 34,11 43,20");
                RightBrow.Data = Geometry.Parse("M 60,20 Q 69,11 78,16");
                LeftEye.Height = RightEye.Height = 20;
                Mouth.Data = Geometry.Parse("M 38,62 Q 51,52 64,62");
                GlowHalo.Opacity = 0.40;
                status = AccentAmber;
                break;
            case "proud":
                LeftEye.Height = RightEye.Height = 24;
                LeftBrow.Data = Geometry.Parse("M 25,15 Q 34,10 43,15");
                RightBrow.Data = Geometry.Parse("M 60,15 Q 69,10 78,15");
                Mouth.Data = Geometry.Parse("M 27,47 Q 51,77 79,47");
                CheekLeft.Opacity = CheekRight.Opacity = 0.90;
                GlowHalo.Opacity = 0.80;
                status = AccentGreen;
                break;
            case "annoyed":
                LeftEye.Height = RightEye.Height = 15;
                LeftBrow.Data = Geometry.Parse("M 25,15 L 43,22");
                RightBrow.Data = Geometry.Parse("M 60,22 L 78,15");
                Mouth.Data = Geometry.Parse("M 38,58 L 64,58");
                CheekLeft.Opacity = CheekRight.Opacity = 0.24;
                status = AccentRed;
                break;
            case "bored":
                LeftEye.Height = RightEye.Height = 12;
                _leftPupilMoodY = 3; _rightPupilMoodY = 3;
                Mouth.Data = Geometry.Parse("M 40,59 L 64,59");
                CheekLeft.Opacity = CheekRight.Opacity = 0.22;
                status = AccentMuted;
                break;
            case "success":
                LeftBrow.Data = Geometry.Parse("M 26,17 Q 34,13 42,17");
                RightBrow.Data = Geometry.Parse("M 61,17 Q 69,13 77,17");
                Mouth.Data = Geometry.Parse("M 27,47 Q 51,77 79,47");
                CheekLeft.Opacity = CheekRight.Opacity = 0.98;
                GlowHalo.Opacity = 0.78;
                status = AccentGreen;
                break;
            case "warning":
                LeftBrow.Data = Geometry.Parse("M 26,17 L 42,22");
                RightBrow.Data = Geometry.Parse("M 61,22 L 77,17");
                Mouth.Data = Geometry.Parse("M 35,63 Q 51,49 70,63");
                status = AccentAmber;
                break;
            case "error":
                LeftBrow.Data = Geometry.Parse("M 26,21 L 42,15");
                RightBrow.Data = Geometry.Parse("M 61,15 L 77,21");
                _leftPupilMoodX = 1;
                _rightPupilMoodX = -1;
                Mouth.Data = Geometry.Parse("M 34,63 Q 51,48 70,63");
                CheekLeft.Opacity = CheekRight.Opacity = 0.28;
                status = AccentRed;
                break;
            case "paused":
            case "interrupted":
                Mouth.Data = Geometry.Parse("M 40,57 L 64,57");
                status = AccentMuted;
                break;
            case "recovering":
            case "reconnecting":
                LeftBrow.Data = Geometry.Parse("M 26,15 Q 34,11 42,15");
                RightBrow.Data = Geometry.Parse("M 61,15 Q 69,11 77,15");
                Mouth.Data = Geometry.Parse("M 43,54 Q 51,63 59,54");
                status = AccentAmber;
                break;
            case "offline":
                LeftEye.Height = RightEye.Height = 15;
                Mouth.Data = Geometry.Parse("M 40,61 Q 51,54 64,61");
                LeftBrow.Data = Geometry.Parse("M 26,22 Q 34,19 42,22");
                RightBrow.Data = Geometry.Parse("M 61,22 Q 69,19 77,22");
                status = AccentMuted;
                break;
            case "privacy":
                LeftEye.Height = RightEye.Height = 4;
                LeftPupil.Visibility = RightPupil.Visibility = Visibility.Collapsed;
                LeftEyeShine.Visibility = RightEyeShine.Visibility = Visibility.Collapsed;
                LeftBrow.Data = Geometry.Parse("M 26,20 Q 34,24 42,20");
                RightBrow.Data = Geometry.Parse("M 61,20 Q 69,24 77,20");
                Mouth.Data = Geometry.Parse("M 40,57 Q 51,60 64,57");
                status = AccentGreen;
                break;
            case "camera_processing":
                _leftPupilMoodX = -1; _leftPupilMoodY = 1.5;
                _rightPupilMoodX = -1; _rightPupilMoodY = 1.5;
                Mouth.Data = Geometry.Parse("M 39,56 Q 51,52 64,56");
                status = AccentPurple;
                break;
            default:
                Mouth.Data = Geometry.Parse("M 28,48 Q 51,72 78,48");
                status = AccentGreen;
                break;
        }

        ApplyPupilTransforms();
        Canvas.SetTop(LeftPupil, 23 + (LeftEye.Height - 10) / 2);
        Canvas.SetTop(RightPupil, 23 + (RightEye.Height - 10) / 2);
        Canvas.SetTop(LeftEyeShine, 23 + (LeftEye.Height - 10) / 2 - 1.5);
        Canvas.SetTop(RightEyeShine, 23 + (RightEye.Height - 10) / 2 - 1.5);
        StatusDot.Fill = status;
        if (showReaction && (previousMood != _mood || _mood != "idle"))
            ShowReactionForMood(_mood);
        _blinkTimer.Interval = _mood switch
        {
            "listening" or "transcribing" => TimeSpan.FromSeconds(5.2),
            "thinking" or "planning" or "coding" => TimeSpan.FromSeconds(4.4),
            "privacy" or "offline" => TimeSpan.FromSeconds(6.5),
            _ => TimeSpan.FromSeconds(3.65)
        };

        _moodResetTimer.Stop();
        if (_mood is "success" or "error" or "warning" or "paused" or "interrupted" or
            "happy" or "greeting" or "love" or "laughing" or "wink" or "surprised" or "confused" or
            "sad" or "worried" or "proud" or "shy" or "annoyed" or "bored" or "playful" or "excited")
        {
            _moodResetTimer.Interval = _mood is "success" or "happy" or "greeting" or "love" or "laughing" or "proud"
                ? TimeSpan.FromMilliseconds(2800)
                : TimeSpan.FromMilliseconds(3200);
            _moodResetTimer.Start();
        }
    }

    private void ApplyPupilTransforms()
    {
        LeftPupil.RenderTransform = new TranslateTransform(_leftPupilMoodX + _pointerOffsetX, _leftPupilMoodY + _pointerOffsetY);
        RightPupil.RenderTransform = new TranslateTransform(_rightPupilMoodX + _pointerOffsetX, _rightPupilMoodY + _pointerOffsetY);
        LeftEyeShine.RenderTransform = new TranslateTransform(_pointerOffsetX, _pointerOffsetY);
        RightEyeShine.RenderTransform = new TranslateTransform(_pointerOffsetX, _pointerOffsetY);
    }

    private void ResetTerminalMood()
    {
        _moodResetTimer.Stop();
        if (_mood is "success" or "error" or "warning" or "paused" or "interrupted" or
            "happy" or "greeting" or "love" or "laughing" or "wink" or "surprised" or "confused" or
            "sad" or "worried" or "proud" or "shy" or "annoyed" or "bored" or "playful" or "excited")
            SetMood(_isPointerOver ? "curious" : "idle", showReaction: false);
    }

    private void ShowReactionForMood(string mood)
    {
        if (!AppRuntime.Settings.MascotAnimationsEnabled || !ReactionSets.TryGetValue(mood, out var choices)) return;
        var now = DateTimeOffset.UtcNow;
        if (_lastReactionMood.Equals(mood, StringComparison.OrdinalIgnoreCase) && now - _lastReactionShownAt < TimeSpan.FromMilliseconds(900)) return;
        _lastReactionMood = mood;
        _lastReactionShownAt = now;
        _reactionIndexes.TryGetValue(mood, out var index);
        var nextIndex = Random.Shared.Next(choices.Length);
        for (var attempt = 0; attempt < choices.Length && choices[nextIndex] == _lastReactionEmoji; attempt++)
            nextIndex = (nextIndex + 1) % choices.Length;
        ReactionEmojiText.Text = choices[nextIndex];
        _lastReactionEmoji = choices[nextIndex];
        _reactionIndexes[mood] = (nextIndex + 1 + index) % choices.Length;
        Canvas.SetLeft(ReactionBubble, 115);
        Canvas.SetTop(ReactionBubble, 54);
        ReactionBubble.RenderTransform = Transform.Identity;
        ReactionBubble.Visibility = Visibility.Visible;
        ReactionBubble.Opacity = 0;
        _reactionStartedAt = DateTimeOffset.UtcNow;
        _reactionActive = true;
    }

    private void AnimateReactionTick()
    {
        if (!_reactionActive) return;
        var elapsed = (DateTimeOffset.UtcNow - _reactionStartedAt).TotalSeconds;
        const double duration = 2.2;
        if (elapsed >= duration)
        {
            HideReaction();
            return;
        }

        var progress = Math.Clamp(elapsed / duration, 0, 1);
        var fadeIn = Math.Clamp(elapsed / 0.16, 0, 1);
        var fadeOut = elapsed <= 1.62 ? 1 : Math.Clamp((duration - elapsed) / (duration - 1.62), 0, 1);
        ReactionBubble.Opacity = Math.Min(fadeIn, fadeOut);
        Canvas.SetLeft(ReactionBubble, 115 + Math.Sin(progress * Math.PI) * 5);
        Canvas.SetTop(ReactionBubble, 54 - (38 * progress));
        var scale = 0.78 + Math.Sin(progress * Math.PI) * 0.20;
        ReactionBubble.RenderTransform = new ScaleTransform(scale, scale);
    }

    private void HideReaction()
    {
        _reactionActive = false;
        ReactionBubble.Opacity = 0;
        ReactionBubble.Visibility = Visibility.Collapsed;
        ReactionBubble.RenderTransform = Transform.Identity;
    }

    public void ApplyScale(double scale)
    {
        scale = Math.Clamp(scale, 0.65, 1.8);
        Width = 178 * scale;
        Height = 145 * scale;
        // Viewbox scales the fixed 178x145 design exactly once; scaling both the Window and
        // its child LayoutTransform previously caused the mascot to clip at larger sizes.
    }

    private void AnimateMoodTick()
    {
        if (!AppRuntime.Settings.MascotAnimationsEnabled || !IsVisible)
        {
            Root.RenderTransform = Transform.Identity;
            AntennaDot.RenderTransform = Transform.Identity;
            GlowHalo.Opacity = _mood is "offline" or "privacy" ? 0.24 : 0.5;
            HideReaction();
            return;
        }

        _floatPhase += _mood switch
        {
            "thinking" or "planning" or "researching" or "coding" => 0.16,
            "speaking" => 0.22,
            "listening" or "transcribing" => 0.10,
            _ => 0.075
        };

        var amplitude = _mood switch
        {
            "idle" => 1.8,
            "success" => 2.4,
            "thinking" or "planning" or "researching" or "coding" => 0.8,
            "speaking" => 0.65,
            "listening" => 1.0,
            "offline" or "privacy" or "paused" => 0.2,
            _ => 0.55
        };
        var x = _mood == "curious" ? Math.Sin(_floatPhase * 0.5) * 1.1 : 0;
        Root.RenderTransform = new TranslateTransform(x, Math.Sin(_floatPhase) * amplitude);

        var pulse = 0.92 + (Math.Sin(_floatPhase * 1.6) + 1) * 0.07;
        AntennaDot.RenderTransformOrigin = new Point(0.5, 0.5);
        AntennaDot.RenderTransform = new ScaleTransform(pulse, pulse);
        GlowHalo.Opacity = (_mood is "offline" or "privacy" ? 0.20 : 0.48) + ((Math.Sin(_floatPhase) + 1) * 0.06);

        if (_mood is "speaking" or "responding")
        {
            _speechFrame++;
            if (_speechFrame % 4 == 0)
            {
                var open = (_speechFrame / 4) % 2 == 0;
                Mouth.Data = open
                    ? Geometry.Parse("M 39,50 Q 51,73 65,50 Q 52,66 39,50 Z")
                    : Geometry.Parse("M 37,52 Q 51,62 67,52");
            }
        }

        AnimateReactionTick();
    }

    private void Blink()
    {
        if (!AppRuntime.Settings.MascotAnimationsEnabled || _blinked || !IsVisible || _mood is "listening" or "transcribing" or "privacy" or "offline") return;
        _blinked = true;
        LeftEye.Height = RightEye.Height = 3;
        LeftPupil.Visibility = RightPupil.Visibility = Visibility.Collapsed;
        LeftEyeShine.Visibility = RightEyeShine.Visibility = Visibility.Collapsed;
        _restoreEyesTimer.Start();
    }

    private void RestoreEyes()
    {
        _restoreEyesTimer.Stop();
        _blinked = false;
        SetMood(_mood, showReaction: false);
    }

    private static string NormalizeMood(string? mood)
    {
        var normalized = (mood ?? "idle").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return normalized switch
        {
            "processing" => "thinking",
            "tool" or "tool_call" or "executing" => "using_tool",
            "approval" or "approval_needed" or "approval_required" => "warning",
            "waiting_for_result" => "waiting",
            "done" or "completed" or "celebrating" => "success",
            "failed" => "error",
            "camera" => "camera_processing",
            "offline" or "privacy" or "idle" or "listening" or "transcribing" or "understanding" or "curious"
                or "thinking" or "planning" or "researching" or "reading" or "coding" or "using_tool" or "waiting"
                or "speaking" or "responding" or "happy" or "greeting" or "love" or "laughing" or "wink" or "surprised"
                or "confused" or "sad" or "worried" or "proud" or "shy" or "annoyed" or "bored" or "playful"
                or "success" or "warning" or "error" or "paused" or "recovering" or "reconnecting"
                or "camera_processing" or "interrupted" or "sleepy" or "excited" => normalized,
            _ => "idle"
        };
    }

    private (double Left, double Top) ClampToVirtualScreen(double left, double top)
    {
        var minLeft = SystemParameters.VirtualScreenLeft;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxLeft = minLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - Width);
        var maxTop = minTop + Math.Max(0, SystemParameters.VirtualScreenHeight - Height);
        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _lastInteractionAt = DateTimeOffset.UtcNow;
        if (e.ClickCount >= 2)
        {
            _pointerDown = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            SetMood("excited");
            AppRuntime.MainWindow?.ShowFromTray();
            e.Handled = true;
            return;
        }

        // A single tap is a personality interaction; dragging is detected only after
        // the pointer crosses the normal Windows drag threshold.
        _pointerDownPosition = e.GetPosition(this);
        _pointerDown = true;
        _dragStarted = false;
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown) return;

        _pointerDown = false;
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (!_dragStarted)
            ReactToTap();

        _dragStarted = false;
        e.Handled = true;
    }

    private void ReactToTap()
    {
        _lastInteractionAt = DateTimeOffset.UtcNow;
        if (!AppRuntime.Settings.MascotAnimationsEnabled)
        {
            // With decorative animation disabled, still provide a static facial response
            // rather than unexpectedly opening the full application on a single tap.
            SetMood("happy", showReaction: false);
            return;
        }

        // Pick from expressive, short-lived moods. Avoid repeating the previous tap mood.
        string[] tapMoods =
        {
            "happy", "greeting", "love", "laughing", "wink", "surprised",
            "confused", "proud", "shy", "playful", "excited"
        };
        var nextMood = tapMoods[Random.Shared.Next(tapMoods.Length)];
        for (var attempt = 0; attempt < tapMoods.Length && nextMood == _lastTapMood; attempt++)
            nextMood = tapMoods[(Array.IndexOf(tapMoods, nextMood) + 1) % tapMoods.Length];

        _lastTapMood = nextMood;
        SetMood(nextMood);
    }

    private void SaveMascotPosition()
    {
        var position = ClampToVirtualScreen(Left, Top);
        Left = position.Left;
        Top = position.Top;
        AppRuntime.Settings.MascotLeft = Left;
        AppRuntime.Settings.MascotTop = Top;
        try { AppRuntime.SettingsStore.Save(AppRuntime.Settings); }
        catch { /* Keep the mascot usable even if settings storage is unavailable. */ }
    }

    private void UpdateIdleState()
    {
        if (!IsVisible || _isPointerOver || _pointerDown)
            return;

        if ((_mood is "idle" or "curious") &&
            DateTimeOffset.UtcNow - _lastInteractionAt >= TimeSpan.FromMinutes(2))
        {
            SetMood("sleepy", showReaction: false);
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _isPointerOver = true;
        _lastInteractionAt = DateTimeOffset.UtcNow;
        if (_mood is "idle" or "sleepy")
            SetMood("curious", showReaction: AppRuntime.Settings.MascotAnimationsEnabled);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!IsVisible) return;

        if (_pointerDown && e.LeftButton == MouseButtonState.Pressed && !_dragStarted)
        {
            var currentPoint = e.GetPosition(this);
            var dx = Math.Abs(currentPoint.X - _pointerDownPosition.X);
            var dy = Math.Abs(currentPoint.Y - _pointerDownPosition.Y);
            if (dx >= SystemParameters.MinimumHorizontalDragDistance ||
                dy >= SystemParameters.MinimumVerticalDragDistance)
            {
                _pointerDown = false;
                _dragStarted = true;
                if (IsMouseCaptured) ReleaseMouseCapture();
                try { DragMove(); }
                catch (InvalidOperationException) { }
                SaveMascotPosition();
                _dragStarted = false;
                return;
            }
        }

        var size = FaceCanvas.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        // Let the pupils follow the pointer while preserving the task expression.
        var point = e.GetPosition(FaceCanvas);
        var normalizedX = Math.Clamp((point.X / size.Width - 0.5) * 2, -1, 1);
        var normalizedY = Math.Clamp((point.Y / size.Height - 0.5) * 2, -1, 1);
        _pointerOffsetX = normalizedX * 2.6;
        _pointerOffsetY = normalizedY * 1.8;
        ApplyPupilTransforms();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _isPointerOver = false;
        _pointerOffsetX = _pointerOffsetY = 0;
        if (_mood == "curious") SetMood("idle");
        else ApplyPupilTransforms();
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        UpdateLanguage();
        _contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e) => AppRuntime.MainWindow?.ShowFromTray();
    private void HideMenuItem_Click(object sender, RoutedEventArgs e) => Hide();
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => AppRuntime.ExitAction?.Invoke();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AppRuntime.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _blinkTimer.Stop();
            _restoreEyesTimer.Stop();
            _floatTimer.Stop();
            _moodResetTimer.Stop();
            _idleTimer.Stop();
        }
        base.OnClosing(e);
    }
}
