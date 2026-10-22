using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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
    private ModelVisual3D? _avatarModelHost;
    private ModelVisual3D? _headRigHost;
    private ModelVisual3D? _bodyRigHost;
    private ModelVisual3D? _leftArmRigHost;
    private ModelVisual3D? _rightArmRigHost;
    private ModelVisual3D? _leftUpperArmJointHost;
    private ModelVisual3D? _rightUpperArmJointHost;
    private ModelVisual3D? _leftForearmJointHost;
    private ModelVisual3D? _rightForearmJointHost;
    private ModelVisual3D? _leftHandJointHost;
    private ModelVisual3D? _rightHandJointHost;
    private ModelVisual3D? _leftLegRigHost;
    private ModelVisual3D? _rightLegRigHost;
    private ModelVisual3D? _leftUpperLegJointHost;
    private ModelVisual3D? _rightUpperLegJointHost;
    private ModelVisual3D? _leftLowerLegJointHost;
    private ModelVisual3D? _rightLowerLegJointHost;
    private ModelVisual3D? _leftFootJointHost;
    private ModelVisual3D? _rightFootJointHost;
    private ModelVisual3D? _left3DIrisHost;
    private ModelVisual3D? _right3DIrisHost;
    private ModelVisual3D? _left3DBrowHost;
    private ModelVisual3D? _right3DBrowHost;
    private ModelVisual3D? _left3DCheekHost;
    private ModelVisual3D? _right3DCheekHost;
    private ModelVisual3D? _mouth3DHost;
    private ScaleTransform3D? _leftEyeBlinkTransform;
    private ScaleTransform3D? _rightEyeBlinkTransform;
    private readonly List<CinematicParticle> _cinematicParticles = new();
    private int _particleSeed;

    private sealed class CinematicParticle
    {
        public FrameworkElement Element { get; init; } = null!;
        public double BaseX { get; init; }
        public double BaseY { get; init; }
        public double Phase { get; init; }
        public double Drift { get; init; }
        public double Speed { get; init; }
    }
    private double _floatPhase;
    private bool _reactionActive;
    private DateTimeOffset _reactionStartedAt;
    private DateTimeOffset _lastReactionShownAt = DateTimeOffset.MinValue;
    private string _lastReactionMood = string.Empty;
    private readonly Dictionary<string, int> _reactionIndexes = new(StringComparer.OrdinalIgnoreCase);
    private int _speechFrame;
    private bool _blinked;
    private bool _isPointerOver;
    private bool _taskActive;
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
        ["playful"] = new[] { "😜", "😉", "🐣", "🎈", "✨", "😎" },
        ["embarrassed"] = new[] { "😳", "🫣", "🙈", "🌸", "💗", "😅", "🫢" },
        ["relieved"] = new[] { "😌", "🌿", "✨", "🫧", "💨", "☀️", "🩵" },
        ["determined"] = new[] { "💪", "🎯", "⚡", "🧭", "🛡️", "🔥", "🫡" },
        ["confident"] = new[] { "😎", "🌟", "✨", "💅", "🪄", "👑", "💫" },
        ["apologetic"] = new[] { "🥺", "🙏", "🫶", "💐", "💗", "😔" },
        ["frustrated"] = new[] { "😤", "💢", "😮‍💨", "🫠", "🛠️", "🤦" },
        ["grateful"] = new[] { "🥰", "💝", "🫶", "🌷", "💐", "💖", "✨" },
        ["celebrating"] = new[] { "🥳", "🎉", "🎊", "💖", "🌈", "🪩", "🚀", "🎈" },
        ["writing"] = new[] { "📝", "✍️", "🖊️", "📄", "💡", "📌", "✨", "🗒️", "🧠" },
        ["validating"] = new[] { "🧪", "🔍", "✅", "📊", "🧩", "🔬", "🎯", "🛠️" }
    };

    public FloatingMascotWindow()
    {
        InitializeComponent();
        InitializeAvatarRig();
        InitializeCinematicEffects();
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

    private void InitializeAvatarRig()
    {
        AvatarViewport.Children.Clear();
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(117, 135, 168)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(255, 252, 255), new Vector3D(-0.35, -0.55, -1)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(110, 218, 241), new Vector3D(0.65, 0.1, -0.55)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(183, 155, 255), new Vector3D(-0.7, 0.1, 0.3)));
        AvatarViewport.Children.Add(new ModelVisual3D { Content = lights });

        // Every anatomical section is a separate scene node with its own transform.
        _bodyRigHost = new ModelVisual3D { Content = CreateBodyModel() };
        _leftArmRigHost = CreateArticulatedArmRig(-1,
            out _leftUpperArmJointHost, out _leftForearmJointHost, out _leftHandJointHost);
        _rightArmRigHost = CreateArticulatedArmRig(1,
            out _rightUpperArmJointHost, out _rightForearmJointHost, out _rightHandJointHost);
        _leftLegRigHost = CreateArticulatedLegRig(-1,
            out _leftUpperLegJointHost, out _leftLowerLegJointHost, out _leftFootJointHost);
        _rightLegRigHost = CreateArticulatedLegRig(1,
            out _rightUpperLegJointHost, out _rightLowerLegJointHost, out _rightFootJointHost);
        _headRigHost = new ModelVisual3D { Content = CreateHeadModel() };
        _avatarModelHost = _headRigHost;

        AvatarViewport.Children.Add(_bodyRigHost);
        // The torso is the skeleton root: shoulders, hips and head inherit its small body motion,
        // while retaining independent local transforms for posing.
        _bodyRigHost.Children.Add(_leftArmRigHost);
        _bodyRigHost.Children.Add(_rightArmRigHost);
        _bodyRigHost.Children.Add(_leftLegRigHost);
        _bodyRigHost.Children.Add(_rightLegRigHost);
        _bodyRigHost.Children.Add(_headRigHost);

        // Real 3D face plate and features replace the former flat Canvas face.
        AddEyeRig(left: true);
        AddEyeRig(left: false);
        _left3DBrowHost = new ModelVisual3D();
        _right3DBrowHost = new ModelVisual3D();
        _left3DCheekHost = new ModelVisual3D();
        _right3DCheekHost = new ModelVisual3D();
        _mouth3DHost = new ModelVisual3D();
        _headRigHost.Children.Add(_left3DCheekHost);
        _headRigHost.Children.Add(_right3DCheekHost);
        _headRigHost.Children.Add(_left3DBrowHost);
        _headRigHost.Children.Add(_right3DBrowHost);
        _headRigHost.Children.Add(_mouth3DHost);
        ApplyThreeDimensionalFaceExpression(_mood);
        ApplyRigPose(0);
    }

    private static Model3DGroup CreateHeadModel()
    {
        var group = new Model3DGroup();
        group.Children.Add(CreateEllipsoid(
            new Point3D(0, 0, 0), 1.08, 0.78, 0.40,
            Color.FromRgb(225, 237, 250), Color.FromRgb(255, 255, 255)));
        group.Children.Add(CreateEllipsoid(
            new Point3D(-0.93, -0.10, 0.015), 0.18, 0.28, 0.23,
            Color.FromRgb(190, 239, 243), Color.FromRgb(236, 255, 255)));
        group.Children.Add(CreateEllipsoid(
            new Point3D(0.93, -0.10, 0.015), 0.18, 0.28, 0.23,
            Color.FromRgb(218, 208, 250), Color.FromRgb(249, 245, 255)));
        group.Children.Add(CreateEllipsoid(
            new Point3D(-0.94, -0.10, 0.22), 0.055, 0.16, 0.055,
            Color.FromRgb(48, 188, 204), Color.FromRgb(170, 250, 255)));
        group.Children.Add(CreateEllipsoid(
            new Point3D(0.94, -0.10, 0.22), 0.055, 0.16, 0.055,
            Color.FromRgb(133, 112, 229), Color.FromRgb(218, 207, 255)));

        // A shallow, rounded dark display is genuine 3D geometry rather than a flat overlay.
        group.Children.Add(CreateEllipsoid(
            new Point3D(0, -0.015, 0.386), 0.73, 0.49, 0.055,
            Color.FromRgb(28, 39, 67), Color.FromRgb(62, 71, 111)));
        return group;
    }

    private static ModelVisual3D CreateArticulatedLegRig(
        int side,
        out ModelVisual3D thigh,
        out ModelVisual3D shin,
        out ModelVisual3D foot)
    {
        // Hip, knee and ankle nodes form a true parent-child transform chain.
        var root = new ModelVisual3D
        {
            Transform = new TranslateTransform3D(side * 0.28, -0.60, 0.24)
        };
        root.Children.Add(new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(0, 0, 0.02), 0.115, 0.12, 0.105,
                Color.FromRgb(205, 221, 245), Color.FromRgb(248, 252, 255))
        });
        thigh = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(side * 0.015, -0.045, 0.025), 0.095, 0.105, 0.10,
                Color.FromRgb(213, 226, 247), Color.FromRgb(255, 255, 255))
        };
        var kneeAnchor = new ModelVisual3D { Transform = new TranslateTransform3D(side * 0.015, -0.10, 0.01) };
        shin = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(0, -0.045, 0.025), 0.08, 0.095, 0.087,
                Color.FromRgb(200, 218, 243), Color.FromRgb(246, 251, 255))
        };
        var ankleAnchor = new ModelVisual3D { Transform = new TranslateTransform3D(side * 0.015, -0.095, 0.02) };
        foot = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(side * 0.03, -0.005, 0.065), 0.135, 0.07, 0.13,
                side < 0 ? Color.FromRgb(150, 133, 230) : Color.FromRgb(78, 191, 202),
                Color.FromRgb(240, 247, 255))
        };
        ankleAnchor.Children.Add(foot);
        shin.Children.Add(ankleAnchor);
        kneeAnchor.Children.Add(shin);
        thigh.Children.Add(kneeAnchor);
        root.Children.Add(thigh);
        return root;
    }

    private static ModelVisual3D CreateArticulatedArmRig(
        int side,
        out ModelVisual3D upperArm,
        out ModelVisual3D forearm,
        out ModelVisual3D hand)
    {
        // Shoulder, elbow and wrist are separate transformable scene nodes.
        var root = new ModelVisual3D
        {
            Transform = new TranslateTransform3D(side * 0.60, -0.30, 0.035)
        };
        root.Children.Add(new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(0, 0, 0.02), 0.14, 0.145, 0.13,
                Color.FromRgb(190, 218, 245), Color.FromRgb(246, 251, 255))
        });
        upperArm = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(side * 0.08, -0.105, 0.025), 0.103, 0.135, 0.104,
                Color.FromRgb(207, 224, 245), Color.FromRgb(246, 251, 255))
        };
        var elbowAnchor = new ModelVisual3D { Transform = new TranslateTransform3D(side * 0.15, -0.17, 0.005) };
        forearm = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(side * 0.07, -0.075, 0.02), 0.087, 0.115, 0.09,
                Color.FromRgb(222, 232, 249), Color.FromRgb(255, 255, 255))
        };
        forearm.Children.Add(new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(side * 0.09, -0.12, 0.083), 0.088, 0.029, 0.024,
                side < 0 ? Color.FromRgb(79, 207, 215) : Color.FromRgb(162, 145, 240),
                Color.FromRgb(242, 252, 255))
        });
        var wristAnchor = new ModelVisual3D { Transform = new TranslateTransform3D(side * 0.08, -0.135, 0.015) };
        hand = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(side * 0.025, -0.004, 0.035), 0.095, 0.088, 0.092,
                side < 0 ? Color.FromRgb(131, 222, 231) : Color.FromRgb(177, 163, 242),
                Color.FromRgb(246, 251, 255))
        };
        wristAnchor.Children.Add(hand);
        forearm.Children.Add(wristAnchor);
        elbowAnchor.Children.Add(forearm);
        upperArm.Children.Add(elbowAnchor);
        root.Children.Add(upperArm);
        return root;
    }

    private static Model3DGroup CreateBodyModel()
    {
        var group = new Model3DGroup();
        group.Children.Add(CreateEllipsoid(
            new Point3D(0, -0.70, 0.015), 0.49, 0.34, 0.29,
            Color.FromRgb(206, 220, 244), Color.FromRgb(248, 252, 255)));
        group.Children.Add(CreateEllipsoid(
            new Point3D(0, -0.47, 0.03), 0.22, 0.17, 0.20,
            Color.FromRgb(226, 237, 250), Color.FromRgb(255, 255, 255)));
        group.Children.Add(CreateEllipsoid(
            new Point3D(0, -0.69, 0.292), 0.19, 0.048, 0.028,
            Color.FromRgb(67, 199, 209), Color.FromRgb(187, 250, 250)));
        return group;
    }

    private static void SetJointRotation(ModelVisual3D? joint, double angle)
    {
        if (joint is not null)
            joint.Transform = new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 0, 1), angle));
    }

    private void AddEyeRig(bool left)
    {
        var x = left ? -0.265 : 0.265;
        const double y = 0.185;
        const double z = 0.428;
        var eye = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(x, y, z), 0.132, 0.165, 0.047,
                Color.FromRgb(248, 251, 255), Color.FromRgb(255, 255, 255))
        };
        var blink = new ScaleTransform3D(1, 1, 1, x, y, z);
        eye.Transform = blink;
        var iris = new ModelVisual3D
        {
            Content = CreateEllipsoid(new Point3D(x, y - 0.002, 0.471), 0.064, 0.082, 0.024,
                Color.FromRgb(91, 162, 231), Color.FromRgb(170, 237, 255))
        };
        var pupil = new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                {
                    CreateEllipsoid(new Point3D(x, y - 0.004, 0.49), 0.034, 0.049, 0.015,
                        Color.FromRgb(16, 25, 47), Color.FromRgb(49, 73, 115)),
                    CreateEllipsoid(new Point3D(x - 0.014, y + 0.025, 0.506), 0.012, 0.015, 0.006,
                        Color.FromRgb(255, 255, 255), Color.FromRgb(255, 255, 255))
                }
            }
        };
        iris.Children.Add(pupil);
        eye.Children.Add(iris);
        _headRigHost!.Children.Add(eye);
        // The iris is already parented by the eye node; keep references for mouse gaze.
        if (left)
        {
            _left3DIrisHost = iris;
            _leftEyeBlinkTransform = blink;
        }
        else
        {
            _right3DIrisHost = iris;
            _rightEyeBlinkTransform = blink;
        }
    }

    private void InitializeCinematicEffects()
    {
        CinematicEffectsLayer.Children.Clear();
        _cinematicParticles.Clear();
    }

    private void ConfigureCinematicEffects(string mood)
    {
        CinematicEffectsLayer.Children.Clear();
        _cinematicParticles.Clear();
        string[] symbols = mood switch
        {
            "love" or "shy" or "embarrassed" or "grateful" or "apologetic" =>
                new[] { "💗", "💕", "🌸", "💖", "✧", "🌷", "🫧", "♥" },
            "success" or "happy" or "excited" or "celebrating" or "proud" =>
                new[] { "✨", "✦", "🎉", "💫", "✧", "🌟", "✴", "💖" },
            "planning" or "researching" or "reading" or "thinking" =>
                new[] { "✧", "◇", "✦", "🫧", "⌁", "◆", "✺", "·" },
            "coding" or "using_tool" or "validating" or "writing" =>
                new[] { "⌘", "⚙", "✦", "◇", "⌁", "✧", "▣", "✴" },
            "warning" or "error" or "frustrated" =>
                new[] { "!", "◆", "✦", "◇", "·", "⚡", "⌁", "✧" },
            _ => new[] { "🌸", "✧", "🫧", "✦", "·", "💫", "◇", "✴" }
        };
        var anchors = new (double X, double Y)[]
        {
            (16, 48), (39, 78), (216, 38), (230, 75),
            (13, 117), (43, 133), (205, 115), (231, 132)
        };
        var count = mood is "sleepy" or "offline" or "privacy" ? 0 : (IsTaskMood(mood) || _taskActive ? 8 : 5);
        for (var i = 0; i < count; i++)
        {
            var anchor = anchors[i];
            var text = new TextBlock
            {
                Text = symbols[(i + _particleSeed) % symbols.Length],
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = i % 3 == 0 ? 17 : 13 + (i % 2) * 2,
                FontWeight = FontWeights.SemiBold,
                Foreground = i % 3 == 0 ? Brushes.LightCyan : (i % 2 == 0 ? Brushes.Plum : Brushes.LightPink),
                Opacity = 0.7,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            Canvas.SetLeft(text, anchor.X);
            Canvas.SetTop(text, anchor.Y);
            CinematicEffectsLayer.Children.Add(text);
            _cinematicParticles.Add(new CinematicParticle
            {
                Element = text,
                BaseX = anchor.X,
                BaseY = anchor.Y,
                Phase = i * 0.91 + _particleSeed * 0.23,
                Drift = 4.5 + (i % 4) * 2.2,
                Speed = 0.44 + (i % 4) * 0.11
            });
        }
        _particleSeed++;
        CinematicEffectsLayer.Visibility =
            AppRuntime.Settings.MascotAnimationsEnabled && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnimateCinematicEffects()
    {
        if (!AppRuntime.Settings.MascotAnimationsEnabled)
        {
            CinematicEffectsLayer.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var particle in _cinematicParticles)
        {
            // Each glyph follows a staggered rise/fade cycle; different speeds keep the
            // burst from looking like a synchronized row of labels.
            var cycle = (_floatPhase * particle.Speed + particle.Phase) % (Math.PI * 2);
            if (cycle < 0) cycle += Math.PI * 2;
            var progress = cycle / (Math.PI * 2);
            var rise = progress * (IsTaskMood(_mood) || _taskActive ? 28 : 20);
            var wave = cycle * 1.7;
            var outward = particle.BaseX < 124 ? -1.0 : 1.0;
            var burst = _mood is "success" or "happy" or "excited" or "celebrating" ? Math.Sin(progress * Math.PI) * 5.5 : 0;
            Canvas.SetLeft(particle.Element, particle.BaseX + Math.Sin(wave) * particle.Drift + outward * burst);
            Canvas.SetTop(particle.Element, particle.BaseY - rise + Math.Cos(wave * 0.68) * 3.5);
            var fade = Math.Sin(progress * Math.PI);
            particle.Element.Opacity = 0.12 + fade * 0.68;
            var scale = 0.60 + fade * 0.45 + Math.Sin(wave * 1.7) * 0.06;
            var rotation = Math.Sin(wave * 0.75) * (IsTaskMood(_mood) ? 22 : 34);
            particle.Element.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new RotateTransform(rotation),
                    new ScaleTransform(Math.Max(0.4, scale), Math.Max(0.4, scale))
                }
            };
        }
    }

    private void ApplyThreeDimensionalFaceExpression(string mood)
    {
        if (_headRigHost is null || _mouth3DHost is null ||
            _left3DBrowHost is null || _right3DBrowHost is null ||
            _left3DCheekHost is null || _right3DCheekHost is null)
            return;

        if (_leftEyeBlinkTransform is not null) _leftEyeBlinkTransform.ScaleY = 1;
        if (_rightEyeBlinkTransform is not null) _rightEyeBlinkTransform.ScaleY = 1;

        var leftBrowControlY = 0.445;
        var rightBrowControlY = 0.445;
        var leftBrowStartY = 0.385;
        var rightBrowStartY = 0.385;
        if (mood is "thinking" or "planning" or "researching" or "curious")
        {
            leftBrowControlY = 0.475;
            rightBrowControlY = 0.435;
        }
        else if (mood is "sad" or "worried" or "apologetic" or "relieved")
        {
            leftBrowControlY = rightBrowControlY = 0.34;
            leftBrowStartY = rightBrowStartY = 0.405;
        }
        else if (mood is "frustrated" or "annoyed" or "determined" or "error" or "warning")
        {
            leftBrowControlY = rightBrowControlY = 0.425;
            leftBrowStartY = 0.355;
            rightBrowStartY = 0.355;
        }
        else if (mood is "surprised" or "excited" or "celebrating")
        {
            leftBrowControlY = rightBrowControlY = 0.51;
            leftBrowStartY = rightBrowStartY = 0.415;
        }

        _left3DBrowHost.Content = CreateCurveStrip(
            SampleQuadraticCurve(new Point3D(-0.44, leftBrowStartY, 0.468),
                new Point3D(-0.27, leftBrowControlY, 0.468), new Point3D(-0.105, 0.39, 0.468), 12),
            0.018, Color.FromRgb(176, 203, 248));
        _right3DBrowHost.Content = CreateCurveStrip(
            SampleQuadraticCurve(new Point3D(0.105, 0.39, 0.468),
                new Point3D(0.27, rightBrowControlY, 0.468), new Point3D(0.44, rightBrowStartY, 0.468), 12),
            0.018, Color.FromRgb(176, 203, 248));

        var cheekColor = mood is "love" or "shy" or "embarrassed" or "grateful" or "happy" or "celebrating"
            ? Color.FromRgb(255, 129, 183)
            : Color.FromRgb(224, 118, 168);
        var cheekRadius = mood is "sad" or "offline" ? 0.045 : 0.073;
        _left3DCheekHost.Content = CreateEllipsoid(
            new Point3D(-0.50, -0.105, 0.437), cheekRadius, 0.035, 0.018, cheekColor, cheekColor);
        _right3DCheekHost.Content = CreateEllipsoid(
            new Point3D(0.50, -0.105, 0.437), cheekRadius, 0.035, 0.018, cheekColor, cheekColor);

        if (mood is "wink" or "playful") {
            if (_leftEyeBlinkTransform is not null) _leftEyeBlinkTransform.ScaleY = 0.13;
            if (_rightEyeBlinkTransform is not null) _rightEyeBlinkTransform.ScaleY = 1.06;
        }
        else if (mood is "sleepy" or "bored") {
            if (_leftEyeBlinkTransform is not null) _leftEyeBlinkTransform.ScaleY = 0.48;
            if (_rightEyeBlinkTransform is not null) _rightEyeBlinkTransform.ScaleY = 0.48;
        }
        else if (mood is "laughing" or "celebrating") {
            if (_leftEyeBlinkTransform is not null) _leftEyeBlinkTransform.ScaleY = 0.12;
            if (_rightEyeBlinkTransform is not null) _rightEyeBlinkTransform.ScaleY = 0.12;
        }
        else if (mood is "surprised" or "excited") {
            if (_leftEyeBlinkTransform is not null) _leftEyeBlinkTransform.ScaleY = 1.12;
            if (_rightEyeBlinkTransform is not null) _rightEyeBlinkTransform.ScaleY = 1.12;
        }

        if (mood is "surprised" or "confused") {
            _mouth3DHost.Content = CreateEllipsoid(
                new Point3D(0, -0.205, 0.474), 0.070, 0.092, 0.026,
                Color.FromRgb(96, 37, 81), Color.FromRgb(184, 75, 133));
            return;
        }
        if (mood is "speaking" or "responding" or "laughing" or "excited" or "celebrating") {
            _mouth3DHost.Content = CreateEllipsoid(
                new Point3D(0, -0.225, 0.474), 0.112, 0.064, 0.025,
                Color.FromRgb(112, 40, 84), Color.FromRgb(195, 90, 144));
            return;
        }

        var left = new Point3D(-0.285, -0.205, 0.472);
        var right = new Point3D(0.285, -0.205, 0.472);
        var controlY = mood switch
        {
            "sad" or "worried" or "apologetic" or "error" => -0.105,
            "frustrated" or "annoyed" or "warning" => -0.18,
            "coding" or "determined" or "validating" => -0.235,
            "idle" or "offline" or "privacy" or "paused" or "waiting" => -0.215,
            _ => -0.34
        };
        if (mood is "frustrated" or "annoyed" or "error") {
            var points = new List<Point3D>();
            points.AddRange(SampleQuadraticCurve(left, new Point3D(-0.14, controlY - 0.025, 0.472),
                new Point3D(-0.02, controlY + 0.015, 0.472), 8));
            points.AddRange(SampleQuadraticCurve(new Point3D(-0.02, controlY + 0.015, 0.472),
                new Point3D(0.12, controlY - 0.02, 0.472), right, 8).Skip(1));
            _mouth3DHost.Content = CreateCurveStrip(points.ToArray(), 0.022, Color.FromRgb(250, 227, 244));
        }
        else
        {
            _mouth3DHost.Content = CreateCurveStrip(
                SampleQuadraticCurve(left, new Point3D(0, controlY, 0.472), right, 18),
                0.022, Color.FromRgb(250, 227, 244));
        }
    }

    private void ApplyRigPose(double phase)
    {
        if (_headRigHost is null || _bodyRigHost is null ||
            _leftArmRigHost is null || _rightArmRigHost is null ||
            _leftLegRigHost is null || _rightLegRigHost is null ||
            _leftUpperArmJointHost is null || _rightUpperArmJointHost is null ||
            _leftForearmJointHost is null || _rightForearmJointHost is null ||
            _leftHandJointHost is null || _rightHandJointHost is null ||
            _leftUpperLegJointHost is null || _rightUpperLegJointHost is null ||
            _leftLowerLegJointHost is null || _rightLowerLegJointHost is null ||
            _leftFootJointHost is null || _rightFootJointHost is null)
            return;

        _headRigHost.Transform = new Transform3DGroup
        {
            Children =
            {
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), Math.Sin(phase * 0.22) * (_mood == "curious" ? 7.5 : 2.8))),
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), Math.Sin(phase * 0.31) * ((_mood is "thinking" or "reading") ? 3.4 : 1.6))),
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), Math.Sin(phase * 0.31) * ((_mood is "embarrassed" or "shy") ? 3.2 : 1.1))),
                new TranslateTransform3D(0, Math.Sin(phase) * 0.012, 0)
            }
        };
        var bodyRoll = _mood is "celebrating" or "excited" ? Math.Sin(phase * 1.2) * 3.3 :
            _mood is "thinking" or "planning" or "researching" or "coding" ? Math.Sin(phase * 0.55) * 1.2 :
            Math.Sin(phase * 0.36) * 0.7;
        _bodyRigHost.Transform = new Transform3DGroup
        {
            Children =
            {
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), bodyRoll)),
                new TranslateTransform3D(0, Math.Sin(phase * 1.4) * 0.012, 0)
            }
        };

        var leftArmAngle = Math.Sin(phase * 0.86) * 5.5;
        var rightArmAngle = Math.Sin(phase * 0.91 + 1.1) * 5.5;
        if (_mood is "writing" or "coding" or "using_tool")
        {
            leftArmAngle += Math.Sin(phase * 2.2) * 5;
            rightArmAngle += Math.Cos(phase * 2.6) * 7;
        }
        else if (_mood is "thinking" or "planning" or "researching" or "reading")
        {
            leftArmAngle -= 4.5;
            rightArmAngle += 11 + Math.Sin(phase * 0.9) * 3.0;
        }
        else if (_mood == "waiting")
        {
            leftArmAngle += 3.0;
            rightArmAngle -= 3.0;
        }
        if (_mood is "success" or "celebrating" or "excited")
        {
            leftArmAngle -= 25 + Math.Sin(phase * 1.8) * 6;
            rightArmAngle += 25 + Math.Sin(phase * 1.7) * 6;
        }
        if (_mood is "apologetic" or "sad" or "error" or "frustrated")
        {
            leftArmAngle += 8;
            rightArmAngle -= 8;
        }
        SetJointRotation(_leftUpperArmJointHost, leftArmAngle);
        SetJointRotation(_rightUpperArmJointHost, rightArmAngle);

        var leftForearmAngle = Math.Sin(phase * 0.78 + 0.3) * 9.0;
        var rightForearmAngle = Math.Sin(phase * 0.81 + 1.0) * 9.0;
        var leftHandAngle = Math.Sin(phase * 1.7 + 0.2) * 7.0;
        var rightHandAngle = Math.Sin(phase * 1.9 + 0.7) * 7.0;
        if (_mood is "writing" or "coding" or "using_tool")
        {
            leftForearmAngle += Math.Sin(phase * 2.1) * 12.0;
            rightForearmAngle += Math.Cos(phase * 2.5) * 14.0;
            leftHandAngle += Math.Sin(phase * 3.1) * 13.0;
            rightHandAngle += Math.Cos(phase * 3.4) * 15.0;
            if (_mood == "writing")
            {
                rightForearmAngle += 18;
                rightHandAngle -= 12;
                leftForearmAngle -= 7;
            }
            else if (_mood == "using_tool")
            {
                rightForearmAngle += 16;
                rightHandAngle -= 9;
            }
        }
        else if (_mood is "thinking" or "planning" or "researching")
        {
            rightForearmAngle += 12;
            rightHandAngle += 8;
        }
        else if (_mood == "reading")
        {
            leftForearmAngle -= 11;
            rightForearmAngle += 20;
            rightHandAngle += 10;
        }
        if (_mood is "success" or "celebrating" or "excited")
        {
            leftForearmAngle -= 17;
            rightForearmAngle += 17;
            leftHandAngle -= 11;
            rightHandAngle += 11;
        }
        SetJointRotation(_leftForearmJointHost, leftForearmAngle);
        SetJointRotation(_rightForearmJointHost, rightForearmAngle);
        SetJointRotation(_leftHandJointHost, leftHandAngle);
        SetJointRotation(_rightHandJointHost, rightHandAngle);

        var legSwing = Math.Sin(phase * 0.72) * (_mood is "celebrating" or "excited" ? 6.0 : 2.0);
        var leftKneeFlex = _mood is "celebrating" or "excited" ? 12 + Math.Abs(Math.Sin(phase)) * 9 : 2 + Math.Abs(Math.Sin(phase * 0.7)) * 2;
        var rightKneeFlex = _mood is "celebrating" or "excited" ? 12 + Math.Abs(Math.Cos(phase)) * 9 : 2 + Math.Abs(Math.Cos(phase * 0.7)) * 2;
        SetJointRotation(_leftUpperLegJointHost, legSwing);
        SetJointRotation(_rightUpperLegJointHost, -legSwing);
        SetJointRotation(_leftLowerLegJointHost, leftKneeFlex);
        SetJointRotation(_rightLowerLegJointHost, rightKneeFlex);
        SetJointRotation(_leftFootJointHost, -leftKneeFlex * 0.45);
        SetJointRotation(_rightFootJointHost, -rightKneeFlex * 0.45);
    }

    private static Point3D[] SampleQuadraticCurve(Point3D start, Point3D control, Point3D end, int segments)
    {
        var points = new Point3D[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var t = (double)i / segments;
            var inverse = 1 - t;
            points[i] = new Point3D(
                inverse * inverse * start.X + 2 * inverse * t * control.X + t * t * end.X,
                inverse * inverse * start.Y + 2 * inverse * t * control.Y + t * t * end.Y,
                inverse * inverse * start.Z + 2 * inverse * t * control.Z + t * t * end.Z);
        }
        return points;
    }

    private static GeometryModel3D CreateCurveStrip(Point3D[] points, double thickness, Color color)
    {
        var mesh = new MeshGeometry3D();
        if (points.Length < 2)
            return new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color)));

        for (var i = 0; i < points.Length; i++)
        {
            var previous = points[Math.Max(0, i - 1)];
            var next = points[Math.Min(points.Length - 1, i + 1)];
            var tangent = new Vector3D(next.X - previous.X, next.Y - previous.Y, 0);
            if (tangent.Length < 0.0001) tangent = new Vector3D(1, 0, 0);
            tangent.Normalize();
            var normal = new Vector3D(-tangent.Y, tangent.X, 0);
            normal.Normalize();
            var offset = normal * thickness * 0.5;
            mesh.Positions.Add(new Point3D(points[i].X + offset.X, points[i].Y + offset.Y, points[i].Z));
            mesh.Positions.Add(new Point3D(points[i].X - offset.X, points[i].Y - offset.Y, points[i].Z));
            mesh.Normals.Add(new Vector3D(0, 0, 1));
            mesh.Normals.Add(new Vector3D(0, 0, 1));
        }
        for (var i = 0; i < points.Length - 1; i++)
        {
            var index = i * 2;
            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add(index + 2);
            mesh.TriangleIndices.Add(index + 2);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add(index + 3);
        }
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)), 35));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static GeometryModel3D CreateEllipsoid(
        Point3D center, double radiusX, double radiusY, double radiusZ,
        Color baseColor, Color highlightColor)
    {
        const int slices = 32;
        const int stacks = 20;
        var mesh = new MeshGeometry3D();

        for (var stack = 0; stack <= stacks; stack++)
        {
            var theta = Math.PI * stack / stacks;
            var sinTheta = Math.Sin(theta);
            var cosTheta = Math.Cos(theta);

            for (var slice = 0; slice <= slices; slice++)
            {
                var phi = 2 * Math.PI * slice / slices;
                var nx = sinTheta * Math.Cos(phi);
                var nz = sinTheta * Math.Sin(phi);
                var ny = cosTheta;

                mesh.Positions.Add(new Point3D(
                    center.X + radiusX * nx,
                    center.Y + radiusY * ny,
                    center.Z + radiusZ * nz));

                var normal = new Vector3D(nx / radiusX, ny / radiusY, nz / radiusZ);
                normal.Normalize();
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point((double)slice / slices, (double)stack / stacks));
            }
        }

        for (var stack = 0; stack < stacks; stack++)
        {
            for (var slice = 0; slice < slices; slice++)
            {
                var first = stack * (slices + 1) + slice;
                var nextRow = first + slices + 1;

                // Counter-clockwise winding keeps the outward-facing normals lit correctly.
                mesh.TriangleIndices.Add(first);
                mesh.TriangleIndices.Add(first + 1);
                mesh.TriangleIndices.Add(nextRow);

                mesh.TriangleIndices.Add(first + 1);
                mesh.TriangleIndices.Add(nextRow + 1);
                mesh.TriangleIndices.Add(nextRow);
            }
        }

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(baseColor)));
        material.Children.Add(new SpecularMaterial(
            new SolidColorBrush(Color.FromArgb(172, highlightColor.R, highlightColor.G, highlightColor.B)), 52));

        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    public string CurrentMood => _mood;
    public bool IsTaskActive => _taskActive;

    public void SetTaskActive(bool active)
    {
        _taskActive = active;
        if (active)
        {
            _lastInteractionAt = DateTimeOffset.UtcNow;
            if (_mood is "idle" or "curious" or "sleepy")
                SetMood("thinking", showReaction: false);
        }
        UpdateTaskProps();
    }

    public void ShowAgain()
    {
        if (!IsVisible) Show();
        Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
        if (!AppRuntime.Settings.MascotAnimationsEnabled) Root.RenderTransform = Transform.Identity;
    }

    public void UpdateLanguage()
    {
        var ar = AppRuntime.Settings.Language == "ar";
        ToolTip = ar ? "ليال | انقر مرتين لفتح الواجهة" : "Layal | Double-click to open the full interface";
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
                LeftBrow.Data = Geometry.Parse("M 26,18 Q 34,15 42,17");
                RightBrow.Data = Geometry.Parse("M 61,14 Q 69,10 77,14");
                _leftPupilMoodX = 1.5; _leftPupilMoodY = -2.0;
                _rightPupilMoodX = 2.0; _rightPupilMoodY = -2.0;
                Mouth.Data = Geometry.Parse("M 40,56 Q 51,51 64,56");
                status = AccentAmber;
                break;
            case "planning":
                LeftBrow.Data = Geometry.Parse("M 25,15 Q 34,9 43,15");
                RightBrow.Data = Geometry.Parse("M 61,17 Q 69,14 77,17");
                _leftPupilMoodX = -1.2; _leftPupilMoodY = -2.1;
                _rightPupilMoodX = -0.5; _rightPupilMoodY = -1.8;
                Mouth.Data = Geometry.Parse("M 42,56 Q 51,52 60,56");
                status = AccentPurple;
                break;
            case "researching":
                LeftBrow.Data = Geometry.Parse("M 25,14 Q 34,9 43,15");
                RightBrow.Data = Geometry.Parse("M 60,14 Q 69,9 78,15");
                LeftEye.Height = RightEye.Height = 25;
                _leftPupilMoodX = -2.1; _leftPupilMoodY = -0.2;
                _rightPupilMoodX = -1.8; _rightPupilMoodY = -0.2;
                Mouth.Data = Geometry.Parse("M 43,55 Q 51,58 59,55");
                status = AccentCyan;
                break;
            case "reading":
                LeftEye.Height = RightEye.Height = 19;
                _leftPupilMoodX = 0.3; _leftPupilMoodY = 2.5;
                _rightPupilMoodX = 0.3; _rightPupilMoodY = 2.5;
                LeftBrow.Data = Geometry.Parse("M 26,18 Q 34,15 42,18");
                RightBrow.Data = Geometry.Parse("M 61,18 Q 69,15 77,18");
                Mouth.Data = Geometry.Parse("M 39,55 Q 51,63 64,55");
                status = AccentCyan;
                break;
            case "coding":
                LeftEye.Height = RightEye.Height = 18;
                LeftBrow.Data = Geometry.Parse("M 25,19 Q 34,13 43,18");
                RightBrow.Data = Geometry.Parse("M 60,18 Q 69,13 78,19");
                Mouth.Data = Geometry.Parse("M 40,57 L 63,57");
                status = AccentPurple;
                break;
            case "writing":
                LeftEye.Height = RightEye.Height = 19;
                _leftPupilMoodX = -2.2; _leftPupilMoodY = 2.0;
                _rightPupilMoodX = -1.8; _rightPupilMoodY = 2.0;
                LeftBrow.Data = Geometry.Parse("M 26,17 Q 34,13 42,18");
                RightBrow.Data = Geometry.Parse("M 61,17 Q 69,13 77,18");
                Mouth.Data = Geometry.Parse("M 39,55 Q 51,61 64,55");
                status = AccentPurple;
                break;
            case "using_tool":
                _leftPupilMoodX = 2.2; _leftPupilMoodY = 0.4;
                _rightPupilMoodX = 2.2; _rightPupilMoodY = 0.4;
                LeftBrow.Data = Geometry.Parse("M 26,15 Q 34,11 42,15");
                RightBrow.Data = Geometry.Parse("M 61,15 Q 69,11 77,15");
                Mouth.Data = Geometry.Parse("M 38,54 Q 51,63 65,54");
                status = AccentCyan;
                break;
            case "waiting":
                LeftEye.Height = RightEye.Height = 20;
                Mouth.Data = Geometry.Parse("M 41,57 Q 51,60 61,57");
                status = AccentAmber;
                break;
            case "validating":
                LeftEye.Height = RightEye.Height = 24;
                LeftBrow.Data = Geometry.Parse("M 25,15 Q 34,9 43,15");
                RightBrow.Data = Geometry.Parse("M 60,15 Q 69,9 78,15");
                Mouth.Data = Geometry.Parse("M 42,55 Q 51,61 60,55");
                GlowHalo.Opacity = 0.74;
                status = AccentGreen;
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
            case "embarrassed":
                LeftEye.Height = RightEye.Height = 16;
                _leftPupilMoodY = _rightPupilMoodY = 2.2;
                LeftBrow.Data = Geometry.Parse("M 26,17 Q 34,14 42,19");
                RightBrow.Data = Geometry.Parse("M 61,19 Q 69,14 77,17");
                Mouth.Data = Geometry.Parse("M 43,56 Q 51,60 59,56");
                CheekLeft.Opacity = CheekRight.Opacity = 1;
                GlowHalo.Opacity = 0.72;
                status = AccentRed;
                break;
            case "relieved":
                LeftEye.Height = RightEye.Height = 16;
                _leftPupilMoodY = _rightPupilMoodY = 1.1;
                Mouth.Data = Geometry.Parse("M 29,49 Q 51,76 79,49");
                CheekLeft.Opacity = CheekRight.Opacity = 0.86;
                GlowHalo.Opacity = 0.72;
                status = AccentGreen;
                break;
            case "determined":
                LeftEye.Height = RightEye.Height = 20;
                LeftBrow.Data = Geometry.Parse("M 26,17 Q 34,22 42,18");
                RightBrow.Data = Geometry.Parse("M 61,18 Q 69,22 77,17");
                Mouth.Data = Geometry.Parse("M 39,57 Q 51,59 64,57");
                GlowHalo.Opacity = 0.68;
                status = AccentCyan;
                break;
            case "confident":
                LeftBrow.Data = Geometry.Parse("M 25,17 Q 34,12 43,16");
                RightBrow.Data = Geometry.Parse("M 61,14 Q 69,10 77,14");
                Mouth.Data = Geometry.Parse("M 36,54 Q 54,64 70,52");
                GlowHalo.Opacity = 0.72;
                status = AccentPurple;
                break;
            case "apologetic":
                LeftEye.Height = RightEye.Height = 18;
                _leftPupilMoodY = _rightPupilMoodY = 1.5;
                LeftBrow.Data = Geometry.Parse("M 26,18 Q 34,23 42,20");
                RightBrow.Data = Geometry.Parse("M 61,20 Q 69,23 77,18");
                Mouth.Data = Geometry.Parse("M 35,62 Q 51,48 69,62");
                CheekLeft.Opacity = CheekRight.Opacity = 0.45;
                GlowHalo.Opacity = 0.38;
                status = AccentMuted;
                break;
            case "frustrated":
                LeftEye.Height = RightEye.Height = 18;
                LeftBrow.Data = Geometry.Parse("M 25,22 Q 34,13 43,18");
                RightBrow.Data = Geometry.Parse("M 60,18 Q 69,13 78,22");
                Mouth.Data = Geometry.Parse("M 38,58 Q 44,53 50,58 Q 57,63 65,57");
                CheekLeft.Opacity = CheekRight.Opacity = 0.35;
                GlowHalo.Opacity = 0.38;
                status = AccentRed;
                break;
            case "grateful":
                LeftEye.Height = RightEye.Height = 21;
                Mouth.Data = Geometry.Parse("M 31,48 Q 51,75 77,48");
                CheekLeft.Opacity = CheekRight.Opacity = 0.96;
                GlowHalo.Opacity = 0.76;
                status = AccentGreen;
                break;
            case "celebrating":
                LeftEye.Height = RightEye.Height = 6;
                LeftPupil.Visibility = RightPupil.Visibility = Visibility.Collapsed;
                LeftEyeShine.Visibility = RightEyeShine.Visibility = Visibility.Collapsed;
                Mouth.Data = Geometry.Parse("M 30,46 Q 51,84 80,46 Z");
                Mouth.Fill = new SolidColorBrush(Color.FromRgb(119, 48, 90));
                CheekLeft.Opacity = CheekRight.Opacity = 1;
                GlowHalo.Opacity = 0.92;
                status = AccentGreen;
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
        ApplyThreeDimensionalFaceExpression(_mood);
        ConfigureCinematicEffects(_mood);
        UpdateTaskProps();
        if (showReaction && (previousMood != _mood || _mood != "idle"))
            ShowReactionForMood(_mood);
        _blinkTimer.Interval = _mood switch
        {
            "listening" or "transcribing" => TimeSpan.FromSeconds(5.2),
            "thinking" or "planning" or "researching" or "reading" or "writing" or "coding" or "validating" => TimeSpan.FromSeconds(4.4),
            "privacy" or "offline" => TimeSpan.FromSeconds(6.5),
            _ => TimeSpan.FromSeconds(3.65)
        };

        _moodResetTimer.Stop();
        if (!(_taskActive && IsTaskMood(_mood)) &&
            (_mood is "success" or "error" or "warning" or "paused" or "interrupted" or
            "happy" or "greeting" or "love" or "laughing" or "wink" or "surprised" or "confused" or
            "sad" or "worried" or "proud" or "shy" or "annoyed" or "bored" or "playful" or "excited" or
            "embarrassed" or "relieved" or "determined" or "confident" or "apologetic" or "frustrated" or
            "grateful" or "celebrating"))
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
        if (_left3DIrisHost is not null)
            _left3DIrisHost.Transform = new TranslateTransform3D(
                (_leftPupilMoodX + _pointerOffsetX) * 0.014,
                -(_leftPupilMoodY + _pointerOffsetY) * 0.014, 0);
        if (_right3DIrisHost is not null)
            _right3DIrisHost.Transform = new TranslateTransform3D(
                (_rightPupilMoodX + _pointerOffsetX) * 0.014,
                -(_rightPupilMoodY + _pointerOffsetY) * 0.014, 0);
    }

    private void ResetTerminalMood()
    {
        _moodResetTimer.Stop();
        if (_mood is not ("success" or "error" or "warning" or "paused" or "interrupted" or
            "happy" or "greeting" or "love" or "laughing" or "wink" or "surprised" or "confused" or
            "sad" or "worried" or "proud" or "shy" or "annoyed" or "bored" or "playful" or "excited" or
            "embarrassed" or "relieved" or "determined" or "confident" or "apologetic" or "frustrated" or
            "grateful" or "celebrating"))
            return;

        // A short reaction may finish while a task is still running. Return to the real work
        // state instead of falling back to idle and leaving the face disconnected from progress.
        SetMood(_taskActive ? "thinking" : (_isPointerOver ? "curious" : "idle"), showReaction: false);
    }

    private void ShowReactionForMood(string mood)
    {
        if (!AppRuntime.Settings.MascotAnimationsEnabled || !ReactionSets.TryGetValue(mood, out var choices)) return;
        var now = DateTimeOffset.UtcNow;
        if (_lastReactionMood.Equals(mood, StringComparison.OrdinalIgnoreCase) && now - _lastReactionShownAt < TimeSpan.FromMilliseconds(900)) return;
        _lastReactionMood = mood;
        _lastReactionShownAt = now;
        var nextIndex = _reactionIndexes.TryGetValue(mood, out var savedIndex)
            ? savedIndex % choices.Length
            : Random.Shared.Next(choices.Length);
        for (var attempt = 0; attempt < choices.Length && choices[nextIndex] == _lastReactionEmoji; attempt++)
            nextIndex = (nextIndex + 1) % choices.Length;
        ReactionEmojiText.Text = choices[nextIndex];
        _lastReactionEmoji = choices[nextIndex];
        // Walk each mood's emoji pool in shuffled-start order so it does not keep picking
        // the same reaction at random before the rest of that pool has been shown.
        _reactionIndexes[mood] = (nextIndex + 1) % choices.Length;
        Canvas.SetLeft(ReactionBubble, 185);
        Canvas.SetTop(ReactionBubble, 29);
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
        Canvas.SetLeft(ReactionBubble, 185 + Math.Sin(progress * Math.PI) * 5);
        Canvas.SetTop(ReactionBubble, 29 - (25 * progress));
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

    private static bool IsTaskMood(string mood) =>
        mood is "thinking" or "planning" or "researching" or "reading" or "writing" or
            "coding" or "validating" or "using_tool" or "waiting" or "warning" or
            "transcribing" or "responding" or "speaking" or "camera_processing";

    private void UpdateTaskProps()
    {
        if (!_taskActive && !IsTaskMood(_mood))
        {
            TaskPropsLayer.Visibility = Visibility.Collapsed;
            PaperProp.Opacity = PencilProp.Opacity = HoloComputerProp.Opacity = 0;
            PaperProp.RenderTransform = PencilProp.RenderTransform = HoloComputerProp.RenderTransform = Transform.Identity;
            return;
        }

        TaskPropsLayer.Visibility = Visibility.Visible;
        // Prop emphasis follows the actual stage instead of showing every prop equally.
        var propOpacity = _mood switch
        {
            "planning" or "researching" or "reading" => (Paper: 0.96, Pencil: 0.42, Computer: 0.70),
            "writing" => (Paper: 0.92, Pencil: 1.0, Computer: 0.44),
            "coding" or "using_tool" or "validating" => (Paper: 0.35, Pencil: 0.35, Computer: 1.0),
            "waiting" or "warning" => (Paper: 0.52, Pencil: 0.18, Computer: 0.84),
            _ => (Paper: 0.82, Pencil: 0.72, Computer: 0.88)
        };
        PaperProp.Opacity = propOpacity.Paper;
        PencilProp.Opacity = propOpacity.Pencil;
        HoloComputerProp.Opacity = propOpacity.Computer;
    }

    public void ApplyScale(double scale)
    {
        scale = Math.Clamp(scale, 0.65, 1.8);
        Width = 248 * scale;
        Height = 175 * scale;
        // Viewbox scales the fixed 178x145 design exactly once; scaling both the Window and
        // its child LayoutTransform previously caused the mascot to clip at larger sizes.
    }

    private void AnimateMoodTick()
    {
        if (!AppRuntime.Settings.MascotAnimationsEnabled || !IsVisible)
        {
            Root.RenderTransform = Transform.Identity;
            AntennaDot.RenderTransform = Transform.Identity;
            if (_avatarModelHost is not null) _avatarModelHost.Transform = Transform3D.Identity;
            if (_headRigHost is not null) _headRigHost.Transform = Transform3D.Identity;
            if (_bodyRigHost is not null) _bodyRigHost.Transform = Transform3D.Identity;
            SetJointRotation(_leftUpperArmJointHost, 0);
            SetJointRotation(_rightUpperArmJointHost, 0);
            SetJointRotation(_leftForearmJointHost, 0);
            SetJointRotation(_rightForearmJointHost, 0);
            SetJointRotation(_leftHandJointHost, 0);
            SetJointRotation(_rightHandJointHost, 0);
            SetJointRotation(_leftUpperLegJointHost, 0);
            SetJointRotation(_rightUpperLegJointHost, 0);
            SetJointRotation(_leftLowerLegJointHost, 0);
            SetJointRotation(_rightLowerLegJointHost, 0);
            SetJointRotation(_leftFootJointHost, 0);
            SetJointRotation(_rightFootJointHost, 0);
            GlowHalo.Opacity = _mood is "offline" or "privacy" ? 0.24 : 0.5;
            PaperProp.RenderTransform = PencilProp.RenderTransform = HoloComputerProp.RenderTransform = Transform.Identity;
            Canvas.SetTop(PaperProp, 94);
            Canvas.SetTop(PencilProp, 89);
            UpdateTaskProps();
            HoloComputerProp.Opacity = TaskPropsLayer.Visibility == Visibility.Visible ? HoloComputerProp.Opacity : 0;
            CinematicEffectsLayer.Visibility = Visibility.Collapsed;
            HideReaction();
            return;
        }

        _floatPhase += _mood switch
        {
            "thinking" or "planning" or "researching" or "coding" or "writing" or "validating" or "celebrating" => 0.16,
            "speaking" => 0.22,
            "listening" or "transcribing" => 0.10,
            _ => 0.075
        };

        var amplitude = _mood switch
        {
            "idle" => 1.8,
            "success" or "celebrating" => 2.4,
            "thinking" or "planning" or "researching" or "coding" or "writing" or "validating" => 0.8,
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

        // Independently pose head, torso and shoulders; each is a separate 3D rig node.
        ApplyRigPose(_floatPhase);

        if (TaskPropsLayer.Visibility == Visibility.Visible)
        {
            var propWave = Math.Sin(_floatPhase);
            Canvas.SetTop(PaperProp, 94 + propWave * 2.7);
            PaperProp.RenderTransform = new RotateTransform(Math.Sin(_floatPhase * (_mood == "writing" ? 1.25 : 0.72)) * 4.2);
            Canvas.SetTop(PencilProp, 89 - Math.Cos(_floatPhase * (_mood == "writing" ? 2.35 : 1.05)) * 1.7);
            PencilProp.RenderTransform = new RotateTransform(-10 + Math.Sin(_floatPhase * (_mood == "writing" ? 2.9 : 1.25)) * (_mood == "writing" ? 8.0 : 4.5));
            HoloComputerProp.RenderTransform = new TranslateTransform(0, Math.Sin(_floatPhase * 1.15) * (_mood is "coding" or "using_tool" or "validating" ? 4.7 : 3.2));
            var computerBase = _mood switch
            {
                "writing" => 0.42,
                "planning" or "researching" or "reading" => 0.66,
                "coding" or "using_tool" or "validating" => 0.92,
                "waiting" or "warning" => 0.78,
                _ => 0.82
            };
            HoloComputerProp.Opacity = computerBase * (0.88 + ((Math.Sin(_floatPhase * 1.45) + 1) * 0.06));
        }

        if (_mood is "speaking" or "responding")
        {
            _speechFrame++;
            if (_speechFrame % 4 == 0)
            {
                var open = (_speechFrame / 4) % 2 == 0;
                Mouth.Data = open
                    ? Geometry.Parse("M 39,50 Q 51,73 65,50 Q 52,66 39,50 Z")
                    : Geometry.Parse("M 37,52 Q 51,62 67,52");
                if (_mouth3DHost is not null)
                    _mouth3DHost.Content = open
                        ? CreateEllipsoid(new Point3D(0, -0.225, 0.474), 0.112, 0.064, 0.025,
                            Color.FromRgb(112, 40, 84), Color.FromRgb(195, 90, 144))
                        : CreateCurveStrip(
                            SampleQuadraticCurve(new Point3D(-0.285, -0.205, 0.472),
                                new Point3D(0, -0.34, 0.472), new Point3D(0.285, -0.205, 0.472), 16),
                            0.022, Color.FromRgb(250, 227, 244));
            }
        }

        AnimateCinematicEffects();
        AnimateReactionTick();
    }

    private void Blink()
    {
        if (!AppRuntime.Settings.MascotAnimationsEnabled || _blinked || !IsVisible || _mood is "listening" or "transcribing" or "privacy" or "offline") return;
        _blinked = true;
        LeftEye.Height = RightEye.Height = 3;
        LeftPupil.Visibility = RightPupil.Visibility = Visibility.Collapsed;
        LeftEyeShine.Visibility = RightEyeShine.Visibility = Visibility.Collapsed;
        if (_leftEyeBlinkTransform is not null) _leftEyeBlinkTransform.ScaleY = 0.08;
        if (_rightEyeBlinkTransform is not null) _rightEyeBlinkTransform.ScaleY = 0.08;
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
                or "thinking" or "planning" or "researching" or "reading" or "writing" or "coding" or "validating"
                or "using_tool" or "waiting" or "speaking" or "responding" or "happy" or "greeting" or "love" or "laughing" or "wink" or "surprised"
                or "confused" or "sad" or "worried" or "proud" or "shy" or "annoyed" or "bored" or "playful"
                or "success" or "warning" or "error" or "paused" or "recovering" or "reconnecting"
                or "camera_processing" or "interrupted" or "sleepy" or "excited" or "embarrassed" or
                "relieved" or "determined" or "confident" or "apologetic" or "frustrated" or "grateful" or
                "celebrating" => normalized,
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

    private bool IsPointOnMascot(Point point)
    {
        // The larger transparent window leaves room for floating props. Normalize back to
        // the 248x175 design coordinates so scaling never turns empty margins into click targets.
        var scale = Math.Max(0.1, Width / 248.0);
        point = new Point(point.X / scale, point.Y / scale);

        var onCore = point.X >= 48 && point.X <= 201 && point.Y >= 12 && point.Y <= 139;
        var onLeftSensor = point.X >= 5 && point.X <= 35 && point.Y >= 42 && point.Y <= 94;
        var onRightSensor = point.X >= 213 && point.X <= 243 && point.Y >= 42 && point.Y <= 94;
        var propsVisible = TaskPropsLayer.Visibility == Visibility.Visible;
        var onPaper = propsVisible && point.X >= 3 && point.X <= 43 && point.Y >= 90 && point.Y <= 142;
        var onPencil = propsVisible && point.X >= 28 && point.X <= 48 && point.Y >= 84 && point.Y <= 134;
        var onComputer = propsVisible && point.X >= 190 && point.X <= 246 && point.Y >= 91 && point.Y <= 143;

        return onCore || onLeftSensor || onRightSensor || onPaper || onPencil || onComputer;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsPointOnMascot(e.GetPosition(this))) return;

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

        // Work/approval states take priority over a cosmetic tap; respond with a reaction
        // bubble but keep the real task face and task props intact.
        if (_taskActive || IsTaskMood(_mood))
        {
            if (AppRuntime.Settings.MascotAnimationsEnabled) ShowReactionForMood(_mood);
            return;
        }

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
            "confused", "proud", "shy", "playful", "excited", "embarrassed", "relieved",
            "determined", "confident", "apologetic", "frustrated", "grateful", "celebrating"
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

        if (!_taskActive && (_mood is "idle" or "curious") &&
            DateTimeOffset.UtcNow - _lastInteractionAt >= TimeSpan.FromMinutes(2))
        {
            SetMood("sleepy", showReaction: false);
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!IsPointOnMascot(e.GetPosition(this))) return;

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

        var windowPoint = e.GetPosition(this);
        if (!_pointerDown && !IsPointOnMascot(windowPoint))
        {
            if (_isPointerOver)
            {
                _isPointerOver = false;
                _pointerOffsetX = _pointerOffsetY = 0;
                if (_mood == "curious") SetMood("idle", showReaction: false);
                else ApplyPupilTransforms();
            }
            return;
        }

        if (!_isPointerOver)
        {
            _isPointerOver = true;
            _lastInteractionAt = DateTimeOffset.UtcNow;
            if (_mood is "idle" or "sleepy")
                SetMood("curious", showReaction: AppRuntime.Settings.MascotAnimationsEnabled);
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
            CinematicEffectsLayer.Children.Clear();
            _cinematicParticles.Clear();
        }
        base.OnClosing(e);
    }
}
