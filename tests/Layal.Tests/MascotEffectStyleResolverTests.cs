using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class MascotEffectStyleResolverTests
{
    [TestMethod]
    [DataRow("planning", MascotEffectStyle.TaskOrbit)]
    [DataRow("researching", MascotEffectStyle.TaskOrbit)]
    [DataRow("reading", MascotEffectStyle.TaskOrbit)]
    [DataRow("writing", MascotEffectStyle.TaskOrbit)]
    [DataRow("coding", MascotEffectStyle.TaskOrbit)]
    [DataRow("using_tool", MascotEffectStyle.TaskOrbit)]
    [DataRow("validating", MascotEffectStyle.TaskOrbit)]
    public void WorkPhasesUseTaskOrbitEffects(string mood, MascotEffectStyle expected)
    {
        Assert.AreEqual(expected, MascotEffectStyleResolver.Resolve(mood));
    }

    [TestMethod]
    [DataRow("love", MascotEffectStyle.AffectionDrift)]
    [DataRow("embarrassed", MascotEffectStyle.AffectionDrift)]
    [DataRow("greeting", MascotEffectStyle.AffectionDrift)]
    [DataRow("success", MascotEffectStyle.CelebrationBurst)]
    [DataRow("celebrating", MascotEffectStyle.CelebrationBurst)]
    [DataRow("proud", MascotEffectStyle.CelebrationBurst)]
    public void SocialAndSuccessMoodsUseDistinctEffectLanguages(string mood, MascotEffectStyle expected)
    {
        Assert.AreEqual(expected, MascotEffectStyleResolver.Resolve(mood));
    }

    [TestMethod]
    [DataRow("listening", MascotEffectStyle.SpeechWave)]
    [DataRow("transcribing", MascotEffectStyle.SpeechWave)]
    [DataRow("speaking", MascotEffectStyle.SpeechWave)]
    [DataRow("responding", MascotEffectStyle.SpeechWave)]
    [DataRow("waiting", MascotEffectStyle.WaitingPulse)]
    [DataRow("paused", MascotEffectStyle.WaitingPulse)]
    [DataRow("warning", MascotEffectStyle.AlertPulse)]
    [DataRow("error", MascotEffectStyle.AlertPulse)]
    [DataRow("frustrated", MascotEffectStyle.AlertPulse)]
    public void CommunicationWaitAndAlertStatesRemainVisuallyDistinct(string mood, MascotEffectStyle expected)
    {
        Assert.AreEqual(expected, MascotEffectStyleResolver.Resolve(mood));
    }

    [TestMethod]
    public void QuietAndUnknownMoodsAreHandledSafely()
    {
        Assert.AreEqual(MascotEffectStyle.Quiet, MascotEffectStyleResolver.Resolve("sleepy"));
        Assert.AreEqual(MascotEffectStyle.Quiet, MascotEffectStyleResolver.Resolve("offline"));
        Assert.AreEqual(MascotEffectStyle.Quiet, MascotEffectStyleResolver.Resolve("privacy"));
        Assert.AreEqual(MascotEffectStyle.GentleFloat, MascotEffectStyleResolver.Resolve(null));
        Assert.AreEqual(MascotEffectStyle.GentleFloat, MascotEffectStyleResolver.Resolve("unmapped-mood"));
    }

    [TestMethod]
    public void ResolverNormalizesMoodSeparatorsAndCase()
    {
        Assert.AreEqual(MascotEffectStyle.TaskOrbit, MascotEffectStyleResolver.Resolve("USING TOOL"));
        Assert.AreEqual(MascotEffectStyle.TaskOrbit, MascotEffectStyleResolver.Resolve("Using-Tool"));
    }
}
