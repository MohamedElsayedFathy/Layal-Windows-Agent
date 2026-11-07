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
    public void CaptureStartsOnlyWhenShieldIsOffAndApplicationIsLive()
    {
        Assert.IsTrue(PrivacyShieldPolicy.CanStartCapture(privacyShieldEnabled: false, applicationExiting: false));
        Assert.IsFalse(PrivacyShieldPolicy.CanStartCapture(privacyShieldEnabled: true, applicationExiting: false));
        Assert.IsFalse(PrivacyShieldPolicy.CanStartCapture(privacyShieldEnabled: false, applicationExiting: true));
    }

    [TestMethod]
    public void CapturedImagesAreRejectedWhenShieldShutdownOrCancellationWins()
    {
        Assert.IsTrue(PrivacyShieldPolicy.ShouldDiscardCapturedImage(true, false, false));
        Assert.IsTrue(PrivacyShieldPolicy.ShouldDiscardCapturedImage(false, true, false));
        Assert.IsTrue(PrivacyShieldPolicy.ShouldDiscardCapturedImage(false, false, true));
        Assert.IsFalse(PrivacyShieldPolicy.ShouldDiscardCapturedImage(false, false, false));
    }

    [TestMethod]
    public void CapturedImageBuffersAreZeroedBeforeReferencesAreDiscarded()
    {
        var bytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        PrivacyShieldPolicy.ClearCapturedBuffer(bytes);
        CollectionAssert.AreEqual(new byte[4], bytes);
        PrivacyShieldPolicy.ClearCapturedBuffer(null);
    }
    [TestMethod]
    public void ActiveAgentTaskIsCancelledOnlyWhenPrivacyShieldIsEnabled()
    {
        Assert.IsTrue(PrivacyShieldPolicy.ShouldCancelAgentTask(privacyShieldEnabled: true));
        Assert.IsFalse(PrivacyShieldPolicy.ShouldCancelAgentTask(privacyShieldEnabled: false));
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
