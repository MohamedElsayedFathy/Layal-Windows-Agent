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
        Assert.AreEqual("Pending", UiLocalization.FormatTaskStatus("قيد الانتظار", isArabic: false));
        Assert.AreEqual("Running", UiLocalization.FormatTaskStatus("قيد التنفيذ", isArabic: false));
        Assert.AreEqual("Completed", UiLocalization.FormatTaskStatus("مكتملة", isArabic: false));
        Assert.AreEqual("Completed with warnings", UiLocalization.FormatTaskStatus("مكتملة مع تحذيرات", isArabic: false));
        Assert.AreEqual("Failed", UiLocalization.FormatTaskStatus("فشلت", isArabic: false));
        Assert.AreEqual("Cancelled", UiLocalization.FormatTaskStatus("أُلغيت", isArabic: false));

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
    public void ApprovalTitlesAreLocalizedWithoutChangingCommandIdentifiers()
    {
        Assert.AreEqual("فتح المفكرة", UiLocalization.LocalizeApprovalTitle("Launch Notepad", isArabic: true));
        Assert.AreEqual("Launch Notepad", UiLocalization.LocalizeApprovalTitle("Launch Notepad", isArabic: false));
        Assert.AreEqual("Run dotnet test", UiLocalization.LocalizeApprovalTitle("Run dotnet test on project", isArabic: false));
    }

    [TestMethod]
    public void DynamicStatusPrefixesSwitchLanguagesWithoutDamagingTheirPayload()
    {
        const string english = "Test succeeded: model-42";
        var arabic = UiLocalization.TranslateKnownFeedback(english, isArabic: true);
        var roundTrip = UiLocalization.TranslateKnownFeedback(arabic, isArabic: false);

        Assert.AreEqual("نجح الاختبار: model-42", arabic);
        Assert.AreEqual(english, roundTrip);
    }

    [TestMethod]
    public void TechnicalErrorPrefixesSwitchLanguagesButPreserveDiagnosticPayload()
    {
        const string english = "Connection test failed: HTTP 503 from https://provider.example/api";
        var arabic = UiLocalization.TranslateKnownFeedback(english, isArabic: true);
        var roundTrip = UiLocalization.TranslateKnownFeedback(arabic, isArabic: false);

        Assert.AreEqual("فشل اختبار الاتصال: HTTP 503 from https://provider.example/api", arabic);
        Assert.AreEqual(english, roundTrip);
    }

    [TestMethod]
    public void DateFormattingCanBeParsedByTheSelectedCulture()
    {
        var date = new DateTime(2026, 10, 26, 13, 27, 0);

        var arabic = UiLocalization.FormatDateTime(date, isArabic: true);
        var english = UiLocalization.FormatDateTime(date, isArabic: false);

        Assert.IsTrue(UiLocalization.IsFormattedDateTime(arabic, cultureIsArabic: true));
        Assert.IsTrue(UiLocalization.IsFormattedDateTime(english, cultureIsArabic: false));
    }

    [TestMethod]
    public void TaskResultDisplayCanSwitchLanguagesWithoutChangingUnknownText()
    {
        const string english = "Completed\nThe model returned an empty final answer.";
        var arabic = UiLocalization.TranslateTaskResultDisplay(english, isArabic: true);
        var roundTrip = UiLocalization.TranslateTaskResultDisplay(arabic, isArabic: false);

        Assert.AreEqual("مكتملة\nأعاد النموذج إجابة نهائية فارغة.", arabic);
        Assert.AreEqual(english, roundTrip);
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
