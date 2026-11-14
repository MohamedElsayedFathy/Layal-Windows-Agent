using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class VoiceDeliveryStylePolicyTests
{
    private static readonly string[] Styles =
    [
        "natural", "gentle", "bright", "clear",
        "empathetic", "serious", "energetic", "playful"
    ];

    [TestMethod]
    public void EveryNamedStyleIsSupportedAndUnknownValuesFallBackToNatural()
    {
        foreach (var style in Styles)
        {
            Assert.IsTrue(VoiceDeliveryStylePolicy.IsSupported(style), style);
            Assert.AreEqual(style, VoiceDeliveryStylePolicy.Normalize(style));
            Assert.IsFalse(string.IsNullOrWhiteSpace(VoiceDeliveryStylePolicy.BuildRealtimeInstruction(style)));
            Assert.IsFalse(string.IsNullOrWhiteSpace(VoiceDeliveryStylePolicy.GetDisplayName(style, true)));
            Assert.IsFalse(string.IsNullOrWhiteSpace(VoiceDeliveryStylePolicy.GetDisplayName(style, false)));
        }

        Assert.AreEqual("natural", VoiceDeliveryStylePolicy.Normalize(null));
        Assert.AreEqual("natural", VoiceDeliveryStylePolicy.Normalize(" UNKNOWN "));
        Assert.IsFalse(VoiceDeliveryStylePolicy.IsSupported("unknown"));
    }

    [TestMethod]
    public void LocalPiperPresetsUseDistinctConservativePaceAndPauseValues()
    {
        var scales = Styles.Select(VoiceDeliveryStylePolicy.GetLocalLengthScale).ToArray();
        var pauses = Styles.Select(VoiceDeliveryStylePolicy.GetLocalSentenceSilenceSeconds).ToArray();

        Assert.AreEqual(1.00, VoiceDeliveryStylePolicy.GetLocalLengthScale("natural"), 0.0001);
        Assert.AreEqual(1.10, VoiceDeliveryStylePolicy.GetLocalLengthScale("gentle"), 0.0001);
        Assert.AreEqual(0.95, VoiceDeliveryStylePolicy.GetLocalLengthScale("bright"), 0.0001);
        Assert.AreEqual(1.03, VoiceDeliveryStylePolicy.GetLocalLengthScale("clear"), 0.0001);
        Assert.AreEqual(0.40, VoiceDeliveryStylePolicy.GetLocalSentenceSilenceSeconds("empathetic"), 0.0001);
        Assert.AreEqual(0.12, VoiceDeliveryStylePolicy.GetLocalSentenceSilenceSeconds("energetic"), 0.0001);

        Assert.AreEqual(Styles.Length, scales.Distinct().Count(), "Every style must have a distinct local pace preset.");
        Assert.AreEqual(Styles.Length, pauses.Distinct().Count(), "Every style must have a distinct sentence-pause preset.");
        Assert.IsTrue(scales.All(scale => scale is >= 0.90 and <= 1.15));
        Assert.IsTrue(pauses.All(pause => pause is >= 0.10 and <= 0.45));
        Assert.AreEqual(1.00, VoiceDeliveryStylePolicy.GetLocalLengthScale("invalid"), 0.0001);
        Assert.AreEqual(0.20, VoiceDeliveryStylePolicy.GetLocalSentenceSilenceSeconds("invalid"), 0.0001);
    }

    [TestMethod]
    public void LocalizedStyleNamesRemainDistinct()
    {
        Assert.AreEqual(Styles.Length,
            Styles.Select(id => VoiceDeliveryStylePolicy.GetDisplayName(id, true)).Distinct().Count());
        Assert.AreEqual(Styles.Length,
            Styles.Select(id => VoiceDeliveryStylePolicy.GetDisplayName(id, false)).Distinct().Count());
    }
}
