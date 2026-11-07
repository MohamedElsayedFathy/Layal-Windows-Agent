using System;

namespace Layal.Services;

/// <summary>
/// Immutable rendering budget for the floating mascot. The saved animation preference is
/// independent from the temporary high-performance mode and the Windows client-animation setting.
/// </summary>
public readonly record struct MascotPerformanceBudget(
    bool RunPollingTimers,
    bool AnimateMascot,
    bool RenderCinematicEffects,
    bool CanLoadGlbPreview,
    bool PlayGlbAnimation,
    TimeSpan BlinkInterval,
    TimeSpan FloatInterval,
    TimeSpan IdleInterval,
    TimeSpan HitTestInterval);

public static class MascotPerformancePolicy
{
    public static MascotPerformanceBudget Evaluate(
        bool isVisible,
        bool performanceModeEnabled,
        bool animationsEnabled,
        bool clientAreaAnimationsEnabled,
        bool highContrastEnabled)
    {
        var animationPreferenceAllowsMotion =
            animationsEnabled && !performanceModeEnabled && clientAreaAnimationsEnabled;
        var animateMascot = isVisible && animationPreferenceAllowsMotion;
        var canLoadGlbPreview = animationPreferenceAllowsMotion;

        return new MascotPerformanceBudget(
            RunPollingTimers: isVisible,
            AnimateMascot: animateMascot,
            RenderCinematicEffects: animateMascot && !highContrastEnabled,
            CanLoadGlbPreview: canLoadGlbPreview,
            PlayGlbAnimation: animateMascot && canLoadGlbPreview,
            BlinkInterval: TimeSpan.FromMilliseconds(performanceModeEnabled ? 10_000 : 3_650),
            FloatInterval: TimeSpan.FromMilliseconds(performanceModeEnabled ? 100 : 45),
            IdleInterval: TimeSpan.FromSeconds(performanceModeEnabled ? 45 : 15),
            HitTestInterval: TimeSpan.FromMilliseconds(performanceModeEnabled ? 33 : 8));
    }
}
