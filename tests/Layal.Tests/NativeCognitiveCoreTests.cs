using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class NativeCognitiveCoreTests
{
    [DataTestMethod]
    [DataRow("الساعة كام دلوقتي", NativeIntent.CurrentTime)]
    [DataRow("معلومات النظام", NativeIntent.SystemInfo)]
    [DataRow("اعرض الملفات", NativeIntent.ListWorkspaceFiles)]
    [DataRow("افتحي الحاسبة", NativeIntent.LaunchProgram)]
    [DataRow("ابحثي عن حالة الطقس", NativeIntent.SearchWeb)]
    [DataRow("create file notes.txt with content Hello Layal", NativeIntent.CreateTextFile)]
    [DataRow("read file notes.txt", NativeIntent.ReadTextFile)]
    [DataRow("build Layal.sln", NativeIntent.BuildProject)]
    [DataRow("test Layal.sln", NativeIntent.TestProject)]
    public void ParserMapsSupportedPhrasesToNativeIntents(string phrase, NativeIntent expected)
    {
        using var temp = new TestTempDirectory();
        var learning = new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi"));
        var parser = new NativeLanguageParser(learning);

        var command = parser.Parse(phrase);

        Assert.AreEqual(expected, command.Intent, command.ToString());
    }

    [TestMethod]
    public void ParserDoesNotInventAnIntentForUnsupportedLanguage()
    {
        using var temp = new TestTempDirectory();
        var parser = new NativeLanguageParser(new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi")));

        Assert.AreEqual(NativeIntent.Unknown, parser.Parse("Please autonomously refactor my entire codebase").Intent);
    }

    [DataTestMethod]
    [DataRow("لا تفتحي المفكرة")]
    [DataRow("من فضلك لا تفتحي المفكرة")]
    [DataRow("don't open notepad")]
    [DataRow("I don't want to start paint")]
    [DataRow("The word notepad appears in this sentence")]
    [DataRow("Could you tell me how to open calculator?")]
    [DataRow("please open https://example.com")]
    [DataRow("don't open https://example.com")]
    [DataRow("don't list files")]
    [DataRow("show me how to list files")]
    public void ParserDoesNotTreatNegatedOrDescriptiveTextAsAnAction(string phrase)
    {
        using var temp = new TestTempDirectory();
        var parser = new NativeLanguageParser(
            new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi")));

        Assert.AreEqual(NativeIntent.Unknown, parser.Parse(phrase).Intent, phrase);
    }

    [DataTestMethod]
    [DataRow("open notepad", "notepad")]
    [DataRow("launch calculator", "calculator")]
    [DataRow("start paint", "paint")]
    [DataRow("open file explorer", "file_explorer")]
    [DataRow("launch task manager", "task_manager")]
    [DataRow("افتحي المفكرة", "notepad")]
    [DataRow("افتحي الحاسبة", "calculator")]
    public void ParserRecognizesOnlyExplicitAllowListedLaunchCommands(string phrase, string expectedProgram)
    {
        using var temp = new TestTempDirectory();
        var parser = new NativeLanguageParser(
            new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi")));

        var command = parser.Parse(phrase);

        Assert.AreEqual(NativeIntent.LaunchProgram, command.Intent);
        Assert.AreEqual(expectedProgram, command.ProgramId);
    }

    [TestMethod]
    public void ParserRequiresAnExplicitOpenVerbForUrls()
    {
        using var temp = new TestTempDirectory();
        var parser = new NativeLanguageParser(
            new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi")));

        Assert.AreEqual(NativeIntent.OpenUrl, parser.Parse("open https://example.com").Intent);
        Assert.AreEqual(NativeIntent.Unknown, parser.Parse("Please consider https://example.com as a reference.").Intent);
        Assert.AreEqual(NativeIntent.Unknown, parser.Parse("do not open https://example.com").Intent);
    }

    [TestMethod]
    public void ConversationRespondsWithoutNeedingAProviderOrModel()
    {
        using var temp = new TestTempDirectory();
        var learning = new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi"));
        var parser = new NativeLanguageParser(learning);
        var memory = new MemoryService(Path.Combine(temp.Path, "memory.dpapi"));
        var engine = new NativeConversationEngine(parser, learning, memory);

        var greeting = engine.Respond("السلام عليكم", "ar");
        var capabilities = engine.Respond("ايه قدراتك؟", "ar");
        var time = engine.Respond("الساعة كام", "ar");

        StringAssert.Contains(greeting, "نواتي المحلية");
        StringAssert.Contains(capabilities, "النواة المحلية الحالية");
        Assert.IsFalse(string.IsNullOrWhiteSpace(time));
    }

    [TestMethod]
    public void DocumentSummaryIsExplicitlyExtractiveAndKeepsKeySentences()
    {
        using var temp = new TestTempDirectory();
        var learning = new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi"));
        var engine = new NativeConversationEngine(
            new NativeLanguageParser(learning),
            learning,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        const string document = "Layal keeps local encrypted memory for user approved notes. The memory can be searched without sending it to a provider. The application stores task outcomes locally and verifies file writes before reporting completion.";

        var response = engine.Respond("لخص المستند", "ar", document, "native.txt");

        StringAssert.Contains(response, "ملخص استخراجي محلي");
        StringAssert.Contains(response, "local encrypted memory");
        StringAssert.Contains(response, "ليس تلخيصًا توليديًا");
    }

    [TestMethod]
    public void UserTaughtAliasIsPersistedAndResolved()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The native learning store is protected with Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "native-learning.dpapi");
        var learning = new NativeLearningService(path);

        Assert.IsTrue(learning.TryTeachFromUtterance(
            "لما أقول الوقت اعملي الساعة كام",
            out var message));
        StringAssert.Contains(message, "تم حفظ الاختصار المحلي");

        var reloaded = new NativeLearningService(path);
        Assert.IsTrue(reloaded.TryResolveAlias("الوقت", out var command));
        Assert.AreEqual("الساعة كام", command);
        Assert.AreEqual(NativeIntent.CurrentTime, new NativeLanguageParser(reloaded).Parse("الوقت").Intent);
    }
}
