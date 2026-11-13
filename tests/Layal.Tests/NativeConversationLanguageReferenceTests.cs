using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class NativeConversationLanguageReferenceTests
{
    [TestMethod]
    public void Respond_DefinesAnEgyptianExpressionAndShowsItsCrossLanguageForms()
    {
        Assert.IsTrue(System.OperatingSystem.IsWindows(), "DPAPI-backed services are tested on Windows CI.");
        using var temp = new TestTempDirectory();
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var memory = new MemoryService(Path.Combine(temp.Path, "memory.dpapi"));
        var engine = new NativeConversationEngine(
            new NativeLanguageParser(learning), learning, memory, LanguageReferenceService.LoadBuiltIn());

        var answer = engine.Respond("يعني ايه إزيك؟", "ar");

        StringAssert.Contains(answer, "How are you?");
        StringAssert.Contains(answer, "كيف حالك؟");
        StringAssert.Contains(answer, "سؤال اجتماعي");
    }

    [TestMethod]
    public void Respond_CreatesAnOfflineExampleDialogueInTheSelectedLanguage()
    {
        Assert.IsTrue(System.OperatingSystem.IsWindows(), "DPAPI-backed services are tested on Windows CI.");
        using var temp = new TestTempDirectory();
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var memory = new MemoryService(Path.Combine(temp.Path, "memory.dpapi"));
        var engine = new NativeConversationEngine(
            new NativeLanguageParser(learning), learning, memory, LanguageReferenceService.LoadBuiltIn());

        var answer = engine.Respond("اعملي حوار عن المساعدة التقنية", "ar");

        StringAssert.Contains(answer, "ممكن تساعدني؟");
        StringAssert.Contains(answer, "Speaker");
        StringAssert.Contains(answer, "error");
    }
}
