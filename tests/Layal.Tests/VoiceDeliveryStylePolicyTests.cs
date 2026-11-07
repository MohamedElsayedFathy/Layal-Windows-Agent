using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class VoiceDeliveryStylePolicyTests
{
    [TestMethod]
    public void EveryNamedStyleIsSupportedAndUnknownValuesFallBackToNatural()
    {
        foreach (var style in new[] { "natural", "gentle", "bright", "clear" })
        {
            Assert.IsTrue(VoiceDeliveryStylePolicy.IsSupported(style));
            Assert.AreEqual(style, VoiceDeliveryStylePolicy.Normalize(style));
            Assert.IsFalse(string.IsNullOrWhiteSpace(VoiceDeliveryStylePolicy.BuildRealtimeInstruction(style)));
        }

        Assert.AreEqual("natural", VoiceDeliveryStylePolicy.Normalize(null));
        Assert.AreEqual("natural", VoiceDeliveryStylePolicy.Normalize(" UNKNOWN "));
        Assert.IsFalse(VoiceDeliveryStylePolicy.IsSupported("unknown"));
    }

    [TestMethod]
    public void LocalPiperDeliveryChangesPaceWithinConservativeBoundsOnly()
    {
        var scales = new[]
        {
            VoiceDeliveryStylePolicy.GetLocalLengthScale("natural"),
            VoiceDeliveryStylePolicy.GetLocalLengthScale("gentle"),
            VoiceDeliveryStylePolicy.GetLocalLengthScale("bright"),
            VoiceDeliveryStylePolicy.GetLocalLengthScale("clear")
        };

        Assert.AreEqual(1.00, scales[0], 0.0001);
        Assert.AreEqual(1.10, scales[1], 0.0001);
        Assert.AreEqual(0.94, scales[2], 0.0001);
        Assert.AreEqual(1.03, scales[3], 0.0001);
        Assert.IsTrue(scales.All(scale => scale is >= 0.90 and <= 1.15));
        Assert.AreEqual(1.00, VoiceDeliveryStylePolicy.GetLocalLengthScale("invalid"), 0.0001);
    }

    [TestMethod]
    public void LocalizedStyleNamesRemainDistinct()
    {
        var ids = new[] { "natural", "gentle", "bright", "clear" };
        Assert.AreEqual(4, ids.Select(id => VoiceDeliveryStylePolicy.GetDisplayName(id, true)).Distinct().Count());
        Assert.AreEqual(4, ids.Select(id => VoiceDeliveryStylePolicy.GetDisplayName(id, false)).Distinct().Count());
    }
}
