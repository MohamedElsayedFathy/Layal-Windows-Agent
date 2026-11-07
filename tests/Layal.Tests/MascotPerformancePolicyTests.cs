using System;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class MascotPerformancePolicyTests
{
    [TestMethod]
    public void NormalVisibleMascotKeepsExpectedMotionAndPollingBudget()
    {
        var budget = MascotPerformancePolicy.Evaluate(
            isVisible: true,
            performanceModeEnabled: false,
            animationsEnabled: true,
            clientAreaAnimationsEnabled: true,
            highContrastEnabled: false);

        Assert.IsTrue(budget.RunPollingTimers);
        Assert.IsTrue(budget.AnimateMascot);
        Assert.IsTrue(budget.RenderCinematicEffects);
        Assert.IsTrue(budget.CanLoadGlbPreview);
        Assert.IsTrue(budget.PlayGlbAnimation);
        Assert.AreEqual(TimeSpan.FromMilliseconds(8), budget.HitTestInterval);
        Assert.AreEqual(TimeSpan.FromMilliseconds(45), budget.FloatInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(15), budget.IdleInterval);
    }

    [TestMethod]
    public void HighPerformanceModeStopsMotionAndGlbButRetainsLowerRateHitTesting()
    {
        var budget = MascotPerformancePolicy.Evaluate(
            isVisible: true,
            performanceModeEnabled: true,
            animationsEnabled: true,
            clientAreaAnimationsEnabled: true,
            highContrastEnabled: false);

        Assert.IsTrue(budget.RunPollingTimers,
            "The transparent-window hit test must keep running while the mascot is visible.");
        Assert.IsFalse(budget.AnimateMascot);
        Assert.IsFalse(budget.RenderCinematicEffects);
        Assert.IsFalse(budget.CanLoadGlbPreview);
        Assert.IsFalse(budget.PlayGlbAnimation);
        Assert.AreEqual(TimeSpan.FromMilliseconds(33), budget.HitTestInterval);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), budget.FloatInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(45), budget.IdleInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(10), budget.BlinkInterval);
    }

    [TestMethod]
    public void UserAnimationPreferenceAndWindowsAnimationSettingAreIndependentGates()
    {
        var animationsOff = MascotPerformancePolicy.Evaluate(
            isVisible: true,
            performanceModeEnabled: false,
            animationsEnabled: false,
            clientAreaAnimationsEnabled: true,
            highContrastEnabled: false);
        var windowsMotionOff = MascotPerformancePolicy.Evaluate(
            isVisible: true,
            performanceModeEnabled: false,
            animationsEnabled: true,
            clientAreaAnimationsEnabled: false,
            highContrastEnabled: false);

        foreach (var budget in new[] { animationsOff, windowsMotionOff })
        {
            Assert.IsTrue(budget.RunPollingTimers);
            Assert.IsFalse(budget.AnimateMascot);
            Assert.IsFalse(budget.RenderCinematicEffects);
            Assert.IsFalse(budget.CanLoadGlbPreview);
            Assert.IsFalse(budget.PlayGlbAnimation);
        }
    }

    [TestMethod]
    public void HiddenMascotStopsPollingAndGlbPlaybackButCanRetainARequestedPreviewForResume()
    {
        var budget = MascotPerformancePolicy.Evaluate(
            isVisible: false,
            performanceModeEnabled: false,
            animationsEnabled: true,
            clientAreaAnimationsEnabled: true,
            highContrastEnabled: false);

        Assert.IsFalse(budget.RunPollingTimers);
        Assert.IsFalse(budget.AnimateMascot);
        Assert.IsFalse(budget.RenderCinematicEffects);
        Assert.IsTrue(budget.CanLoadGlbPreview);
        Assert.IsFalse(budget.PlayGlbAnimation);
    }

    [TestMethod]
    public void HighContrastSuppressesDecorativeParticlesWithoutChangingMotionPreference()
    {
        var budget = MascotPerformancePolicy.Evaluate(
            isVisible: true,
            performanceModeEnabled: false,
            animationsEnabled: true,
            clientAreaAnimationsEnabled: true,
            highContrastEnabled: true);

        Assert.IsTrue(budget.AnimateMascot);
        Assert.IsFalse(budget.RenderCinematicEffects);
    }
}
