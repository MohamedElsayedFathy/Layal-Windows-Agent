namespace Layal.Services;

/// <summary>
/// Selects the motion language for the floating mascot's cinematic particles.
/// Kept independent of WPF so task-to-effect behavior can be covered by unit tests.
/// </summary>
public enum MascotEffectStyle
{
    Quiet,
    GentleFloat,
    AffectionDrift,
    TaskOrbit,
    SpeechWave,
    WaitingPulse,
    CelebrationBurst,
    AlertPulse
}

public static class MascotEffectStyleResolver
{
    public static MascotEffectStyle Resolve(string? mood)
    {
        var normalized = (mood ?? "idle").Trim().ToLowerInvariant()
            .Replace('-', '_').Replace(' ', '_');

        return normalized switch
        {
            "offline" or "privacy" or "sleepy" => MascotEffectStyle.Quiet,
            "love" or "shy" or "embarrassed" or "grateful" or "apologetic" or
            "greeting" or "wink" or "playful" => MascotEffectStyle.AffectionDrift,
            "success" or "happy" or "excited" or "celebrating" or "proud" or
            "laughing" or "relieved" or "determined" or "confident" => MascotEffectStyle.CelebrationBurst,
            "planning" or "researching" or "reading" or "thinking" or "writing" or
            "coding" or "using_tool" or "validating" or "camera_processing" =>
                MascotEffectStyle.TaskOrbit,
            "listening" or "transcribing" or "speaking" or "responding" =>
                MascotEffectStyle.SpeechWave,
            "waiting" or "paused" or "reconnecting" or "recovering" =>
                MascotEffectStyle.WaitingPulse,
            "warning" or "error" or "frustrated" or "interrupted" or "annoyed" or
            "sad" or "worried" or "confused" => MascotEffectStyle.AlertPulse,
            _ => MascotEffectStyle.GentleFloat
        };
    }
}
