using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class BidiTextNormalizerTests
{
    [TestMethod]
    public void LatinTokensAreIsolatedInsideArabicSentences()
    {
        var normalized = BidiTextNormalizer.NormalizeMixedText(
            "هذه النسخة تستخدم Windows وتتصل عبر OpenAI API.");

        Assert.IsTrue(normalized.Contains("\u2066Windows\u2069", StringComparison.Ordinal));
        Assert.IsTrue(normalized.Contains("\u2066OpenAI API\u2069", StringComparison.Ordinal));
        Assert.IsTrue(normalized.Contains("هذه النسخة تستخدم", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PureLatinTextIsIsolatedAndArabicOnlyTextIsUnchanged()
    {
        Assert.AreEqual("\u2066English\u2069",
            BidiTextNormalizer.NormalizeMixedText("English"));
        const string arabic = "أهلًا، أنا ليال";
        Assert.AreEqual(arabic, BidiTextNormalizer.NormalizeMixedText(arabic));
    }

    [TestMethod]
    public void NormalizationIsIdempotentAndHandlesEmptyInput()
    {
        var normalized = BidiTextNormalizer.NormalizeMixedText("ZIP وGitHub");
        Assert.AreEqual(normalized, BidiTextNormalizer.NormalizeMixedText(normalized));
        Assert.AreEqual(string.Empty, BidiTextNormalizer.NormalizeMixedText(null));
        Assert.AreEqual(string.Empty, BidiTextNormalizer.NormalizeMixedText(string.Empty));
    }
}
