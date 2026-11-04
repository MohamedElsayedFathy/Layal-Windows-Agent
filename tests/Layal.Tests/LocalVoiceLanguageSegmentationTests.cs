using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalVoiceLanguageSegmentationTests
{
    [TestMethod]
    public void SplitTextByLanguage_UsesArabicVoiceForArabicAndEnglishVoiceForLatinSegments()
    {
        var segments = LocalVoiceService.SplitTextByLanguage("إزيك how are you? تمام");

        Assert.AreEqual(3, segments.Count);
        Assert.IsTrue(segments[0].IsArabic);
        Assert.AreEqual("إزيك", segments[0].Text);
        Assert.IsFalse(segments[1].IsArabic);
        Assert.AreEqual("how are you?", segments[1].Text);
        Assert.IsTrue(segments[2].IsArabic);
        Assert.AreEqual("تمام", segments[2].Text);
    }

    [TestMethod]
    public void SplitTextByLanguage_PreservesPunctuationAndNormalizesWhitespace()
    {
        var segments = LocalVoiceService.SplitTextByLanguage("  Hello   world!  ");

        Assert.AreEqual(1, segments.Count);
        Assert.IsFalse(segments[0].IsArabic);
        Assert.AreEqual("Hello world!", segments[0].Text);
    }

    [TestMethod]
    public void SplitTextByLanguage_ReturnsNoSegmentsForEmptyOrFormattingOnlyInput()
    {
        Assert.AreEqual(0, LocalVoiceService.SplitTextByLanguage(null).Count);
        Assert.AreEqual(0, LocalVoiceService.SplitTextByLanguage("   \n   ").Count);
    }

    [TestMethod]
    public void SplitTextByLanguage_KeepsDigitsWithTheLatinVoice()
    {
        var segments = LocalVoiceService.SplitTextByLanguage("النتيجة 42 is ready");

        Assert.AreEqual(2, segments.Count);
        Assert.IsTrue(segments[0].IsArabic);
        Assert.IsFalse(segments[1].IsArabic);
        StringAssert.Contains(segments[1].Text, "42");
        StringAssert.Contains(segments[1].Text, "ready");
    }
}
