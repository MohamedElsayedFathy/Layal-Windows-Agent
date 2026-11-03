using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LayalIdentityTests
{
    [TestMethod]
    public void SystemPromptDoesNotAssumeEveryUserIsTheDeveloper()
    {
        var prompt = LayalIdentity.SystemPrompt;

        Assert.IsTrue(prompt.Contains("Do not assume that the current user is the developer", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(prompt.Contains("verified information", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(prompt.Contains("The person using you is the developer and project owner", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ArabicInterfaceUsesEgyptianArabicVoiceUnlessTheUserRequestsAnotherLanguage()
    {
        var prompt = LayalIdentity.ForInterfaceLanguage("ar");

        Assert.IsTrue(prompt.Contains("لغة الواجهة الحالية: العربية", StringComparison.Ordinal));
        Assert.IsTrue(prompt.Contains("لهجة مصرية طبيعية", StringComparison.Ordinal));
        Assert.IsTrue(prompt.Contains("عند طلب صياغة بلغة أخرى", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResponseStylesChangePresentationWithoutRemovingSafetyRules()
    {
        var warm = LayalIdentity.ForInterfaceLanguage("en", "warm");
        var focused = LayalIdentity.ForInterfaceLanguage("en", "focused");
        var professional = LayalIdentity.ForInterfaceLanguage("en", "professional");
        var unknown = LayalIdentity.ForInterfaceLanguage("en", "unrecognized");

        StringAssert.Contains(warm, "Response style: warm and supportive");
        StringAssert.Contains(focused, "Response style: focused and concise");
        StringAssert.Contains(professional, "Response style: professional and structured");
        StringAssert.Contains(unknown, "Response style: warm and supportive");
        foreach (var prompt in new[] { warm, focused, professional })
        {
            StringAssert.Contains(prompt, "Never attempt unauthorized access, credential theft, covert monitoring, or bypassing authentication.");
            StringAssert.Contains(prompt, "Never bypass, pre-approve, hide, or misrepresent the local confirmation UI.");
        }
    }

    [TestMethod]
    public void EnglishInterfaceUsesEnglishVoiceAndKeepsTechnicalIdentifiersPrecise()
    {
        var prompt = LayalIdentity.ForInterfaceLanguage("en");

        Assert.IsTrue(prompt.Contains("Active interface language: English", StringComparison.Ordinal));
        Assert.IsTrue(prompt.Contains("avoid translating code identifiers or paths", StringComparison.Ordinal));
    }
}
