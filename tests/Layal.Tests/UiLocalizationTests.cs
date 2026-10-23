using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class UiLocalizationTests
{
    [TestMethod]
    public void TaskStatusesAreRenderedInTheSelectedLanguageWithoutChangingStoredValues()
    {
        Assert.AreEqual("مكتملة مع تحذيرات", UiLocalization.FormatTaskStatus("CompletedWithWarnings", isArabic: true));
        Assert.AreEqual("Completed with warnings", UiLocalization.FormatTaskStatus("CompletedWithWarnings", isArabic: false));

        var record = new AgentTaskRecord { Status = "Completed", Result = "The model returned an empty final answer." };
        Assert.IsTrue(UiLocalization.FormatTaskResult(record, isArabic: true).StartsWith("مكتملة", StringComparison.Ordinal));
        Assert.AreEqual("Completed", record.Status);
    }

    [TestMethod]
    public void TaskEventTranslationRetainsTimestampAndToolIdentifier()
    {
        const string original = "12:34:56  Tool requested: create_text_file (1/12).";

        var arabic = UiLocalization.FormatTaskEvent(original, isArabic: true);
        var english = UiLocalization.FormatTaskEvent(original, isArabic: false);

        Assert.IsTrue(arabic.StartsWith("12:34:56  طلبت المهمة الأداة:", StringComparison.Ordinal));
        StringAssert.Contains(arabic, "create_text_file");
        Assert.AreEqual(original, english);
    }

    [TestMethod]
    public void ApprovalDetailsAreLocalizedButPreserveTheActualUrl()
    {
        const string url = "https://example.test/path?q=layal";
        var original = "Open this URL in the default browser?\n" + url;

        var arabic = UiLocalization.LocalizeApprovalDetails(original, isArabic: true);
        var english = UiLocalization.LocalizeApprovalDetails(original, isArabic: false);

        StringAssert.Contains(arabic, "هل تريد فتح الرابط التالي");
        StringAssert.Contains(arabic, url);
        Assert.AreEqual(original, english);
    }

    [TestMethod]
    public void UnrecognizedModelOutputIsPreservedVerbatim()
    {
        const string output = "This is a user-facing answer in a language selected by the user.";
        Assert.AreEqual(output, UiLocalization.FormatKnownTaskResult(output, isArabic: true));
        Assert.AreEqual(output, UiLocalization.FormatKnownTaskResult(output, isArabic: false));
    }

    [TestMethod]
    public void PlaceholderFeedbackCanBeSwitchedInBothDirections()
    {
        var english = UiLocalization.TranslateKnownFeedback("Settings saved. Connectivity is not confirmed yet; run the connection test.", isArabic: true);
        var arabic = UiLocalization.TranslateKnownFeedback(english, isArabic: false);

        Assert.AreEqual("تم حفظ الإعدادات. لم يتم تأكيد الاتصال حتى الآن؛ استخدم اختبار الاتصال.", english);
        Assert.AreEqual("Settings saved. Connectivity is not confirmed yet; run the connection test.", arabic);
    }
}
