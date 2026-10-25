namespace Layal.Services;

/// <summary>Layal portrait variants that exist as real artwork in Assets/Characters.</summary>
public enum LayalSprite
{
    Idle,
    Happy,
    Curious,
    Playful,
    Focused,
    Surprised,
    Calm
}

/// <summary>Accent used for the robot's screen. Never the only signal: every tone also has a distinct face shape or glyph.</summary>
public enum RobotTone
{
    Cyan,
    Amber,
    Coral,
    Muted
}

/// <summary>
/// Vector description of the robot's face, drawn over its blank glass screen in a 100x76 design box.
/// Plain strings keep this mapping free of WPF types so it can be unit-tested anywhere.
/// </summary>
public sealed record RobotFaceSpec(
    string LeftEye,
    string RightEye,
    bool EyesFilled,
    string Mouth,
    bool MouthFilled,
    string Glyph,
    RobotTone Tone);

public sealed record CharacterExpression(string Mood, LayalSprite Layal, RobotFaceSpec Robot)
{
    public string LayalAssetName => "layal-" + Layal.ToString().ToLowerInvariant() + ".png";
}

/// <summary>
/// Maps the mascot's real runtime mood (the same names produced by TaskMoodResolver and the UI)
/// to Layal's portrait and the robot's face. The concept sheet provides six expressions plus the
/// default pose, so moods without dedicated art (for example sad or worried) are expressed through
/// the closest available portrait while the robot's face carries the specific state.
/// </summary>
public static class CharacterExpressionMap
{
    // Eye shapes in the 100x76 face box. Left eye is centred near x=30, right eye near x=70.
    private const string RoundL = "M 24,28 A 6,8 0 1 1 36,28 A 6,8 0 1 1 24,28 Z";
    private const string RoundR = "M 64,28 A 6,8 0 1 1 76,28 A 6,8 0 1 1 64,28 Z";
    private const string WideL = "M 22.5,28 A 7.5,10 0 1 1 37.5,28 A 7.5,10 0 1 1 22.5,28 Z";
    private const string WideR = "M 62.5,28 A 7.5,10 0 1 1 77.5,28 A 7.5,10 0 1 1 62.5,28 Z";
    private const string UpL = "M 27,22 A 6,8 0 1 1 39,22 A 6,8 0 1 1 27,22 Z";
    private const string UpR = "M 67,22 A 6,8 0 1 1 79,22 A 6,8 0 1 1 67,22 Z";
    private const string JoyL = "M 23,32 Q 30,20 37,32";
    private const string JoyR = "M 63,32 Q 70,20 77,32";
    private const string RestL = "M 23,28 Q 30,35 37,28";
    private const string RestR = "M 63,28 Q 70,35 77,28";
    private const string LineL = "M 23,28 L 37,28";
    private const string LineR = "M 63,28 L 77,28";
    private const string CrossL = "M 24,22 L 36,34 M 36,22 L 24,34";
    private const string CrossR = "M 64,22 L 76,34 M 76,22 L 64,34";
    private const string WinkR = "M 63,29 Q 70,35 77,29";

    // Mouth shapes.
    private const string Smile = "M 40,48 Q 50,60 60,48";
    private const string BigSmile = "M 38,46 Q 50,66 62,46 Z";
    private const string Flat = "M 41,53 L 59,53";
    private const string Tiny = "M 46,52 A 4,5 0 1 1 54,52 A 4,5 0 1 1 46,52 Z";
    private const string Frown = "M 40,57 Q 50,47 60,57";
    private const string Wavy = "M 38,53 Q 44,48 50,53 Q 56,58 62,53";
    private const string Dots = "M 42,53 L 42.2,53 M 50,53 L 50.2,53 M 58,53 L 58.2,53";

    private static readonly RobotFaceSpec Ready = new(RoundL, RoundR, true, Smile, false, "", RobotTone.Cyan);
    private static readonly RobotFaceSpec Happy = new(JoyL, JoyR, false, Smile, false, "", RobotTone.Cyan);
    private static readonly RobotFaceSpec Delighted = new(JoyL, JoyR, false, BigSmile, true, "", RobotTone.Cyan);
    private static readonly RobotFaceSpec Listening = new(WideL, WideR, true, Tiny, true, "", RobotTone.Cyan);
    private static readonly RobotFaceSpec Thinking = new(UpL, UpR, true, Dots, false, "", RobotTone.Cyan);
    private static readonly RobotFaceSpec Working = new(LineL, LineR, false, Flat, false, "", RobotTone.Cyan);
    private static readonly RobotFaceSpec Success = new(JoyL, JoyR, false, BigSmile, true, "\u2713", RobotTone.Cyan);
    private static readonly RobotFaceSpec Asking = new(RoundL, WideR, true, Wavy, false, "?", RobotTone.Cyan);
    private static readonly RobotFaceSpec Waiting = new(WideL, WideR, true, Flat, false, "!", RobotTone.Amber);
    private static readonly RobotFaceSpec Warning = new(RoundL, RoundR, true, Flat, false, "!", RobotTone.Amber);
    private static readonly RobotFaceSpec Worried = new(RoundL, RoundR, true, Frown, false, "!", RobotTone.Amber);
    private static readonly RobotFaceSpec Failed = new(CrossL, CrossR, false, Frown, false, "\u00D7", RobotTone.Coral);
    private static readonly RobotFaceSpec Sleepy = new(RestL, RestR, false, Flat, false, "z", RobotTone.Muted);
    private static readonly RobotFaceSpec Offline = new(LineL, LineR, false, Flat, false, "", RobotTone.Muted);
    private static readonly RobotFaceSpec Winking = new(RoundL, WinkR, true, Smile, false, "", RobotTone.Cyan);
    private static readonly RobotFaceSpec Surprise = new(WideL, WideR, true, Tiny, true, "!", RobotTone.Cyan);
    private static readonly RobotFaceSpec Sad = new(RestL, RestR, false, Frown, false, "", RobotTone.Muted);

    public static CharacterExpression Resolve(string? mood)
    {
        var key = (mood ?? "idle").Trim().ToLowerInvariant();
        return key switch
        {
            // Attentive / receiving input.
            "listening" or "transcribing" or "understanding" => New(key, LayalSprite.Idle, Listening),
            // Reasoning: Layal rests her chin on her hand and glances aside while the robot's dots pulse.
            "thinking" or "planning" => New(key, LayalSprite.Curious, Thinking),
            // Tool/IO work: Layal looks down at the laptop; the robot narrows its eyes.
            "researching" or "reading" or "writing" or "coding" or "using_tool" or "validating" or
                "camera_processing" or "determined" => New(key, LayalSprite.Focused, Working),
            "responding" or "speaking" => New(key, LayalSprite.Happy, Ready),
            "waiting" => New(key, LayalSprite.Idle, Waiting),
            // Positive outcomes.
            "success" or "celebrating" or "proud" => New(key, LayalSprite.Happy, Success),
            "happy" or "greeting" or "grateful" or "relieved" or "laughing" or "love" => New(key, LayalSprite.Happy, Delighted),
            "excited" => New(key, LayalSprite.Surprised, Surprise),
            "wink" or "playful" or "confident" => New(key, LayalSprite.Playful, Winking),
            "surprised" => New(key, LayalSprite.Surprised, Surprise),
            "shy" or "embarrassed" => New(key, LayalSprite.Calm, Happy),
            // Needs the user: approval, uncertainty, trouble. The robot carries the specific signal.
            "warning" => New(key, LayalSprite.Curious, Warning),
            "confused" or "curious" => New(key, LayalSprite.Curious, Asking),
            "worried" or "annoyed" or "frustrated" or "interrupted" or "recovering" or "reconnecting" =>
                New(key, LayalSprite.Curious, Worried),
            "error" => New(key, LayalSprite.Curious, Failed),
            "sad" or "apologetic" => New(key, LayalSprite.Calm, Sad),
            // Quiet states.
            "sleepy" or "bored" => New(key, LayalSprite.Calm, Sleepy),
            "paused" or "offline" or "privacy" => New(key, LayalSprite.Calm, Offline),
            _ => New("idle", LayalSprite.Idle, Ready)
        };
    }

    /// <summary>Short spoken description of the current state for assistive technology (never relies on colour).</summary>
    public static string Describe(CharacterExpression expression, bool arabic)
    {
        if (expression.Robot.Tone == RobotTone.Coral)
            return arabic ? "حدث خطأ" : "Something went wrong";
        if (expression.Mood is "warning" or "waiting")
            return arabic ? "تحتاج انتباهك أو موافقتك" : "Needs your attention or approval";
        if (expression.Robot.Tone == RobotTone.Amber)
            return arabic ? "قلقة وتحتاج انتباهك" : "Concerned and needs your attention";

        return expression.Mood switch
        {
            "listening" or "transcribing" or "understanding" => arabic ? "تستمع إليك" : "Listening",
            "thinking" or "planning" => arabic ? "تفكر" : "Thinking",
            "researching" or "reading" or "writing" or "coding" or "using_tool" or "validating" or
                "camera_processing" or "determined" => arabic ? "تعمل على المهمة" : "Working on the task",
            "responding" or "speaking" => arabic ? "تجيب" : "Responding",
            "success" or "celebrating" or "proud" => arabic ? "أنجزت المهمة" : "Task completed",
            "confused" or "curious" => arabic ? "متسائلة" : "Curious",
            _ => expression.Layal == LayalSprite.Calm
                ? (arabic ? "هادئة" : "Resting")
                : (arabic ? "جاهزة" : "Ready")
        };
    }

    private static CharacterExpression New(string mood, LayalSprite sprite, RobotFaceSpec face) => new(mood, sprite, face);
}
