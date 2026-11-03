using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class PrivacyShieldPolicyTests
{
    [TestMethod]
    public void CapturedAudioIsDiscardedWhenShieldIsEnabledAfterRecordingStops()
    {
        Assert.IsTrue(PrivacyShieldPolicy.ShouldDiscardCapturedAudio(
            privacyShieldEnabled: true,
            windowVisible: true,
            applicationExiting: false,
            cancellationRequested: false));
    }

    [TestMethod]
    public void CapturedAudioIsDiscardedWhenEscapeWasPressedDuringStopFinalization()
    {
        Assert.IsTrue(PrivacyShieldPolicy.ShouldDiscardCapturedAudio(
            privacyShieldEnabled: false,
            windowVisible: true,
            applicationExiting: false,
            cancellationRequested: true));
    }

    [TestMethod]
    public void CapturedAudioIsDiscardedWhenWindowIsHiddenOrApplicationIsExiting()
    {
        Assert.IsTrue(PrivacyShieldPolicy.ShouldDiscardCapturedAudio(
            privacyShieldEnabled: false,
            windowVisible: false,
            applicationExiting: false,
            cancellationRequested: false));
        Assert.IsTrue(PrivacyShieldPolicy.ShouldDiscardCapturedAudio(
            privacyShieldEnabled: false,
            windowVisible: true,
            applicationExiting: true,
            cancellationRequested: false));
    }

    [TestMethod]
    public void CapturedAudioCanProceedOnlyWhenAllPrivacyAndLifecycleChecksPass()
    {
        Assert.IsFalse(PrivacyShieldPolicy.ShouldDiscardCapturedAudio(
            privacyShieldEnabled: false,
            windowVisible: true,
            applicationExiting: false,
            cancellationRequested: false));
    }
}
