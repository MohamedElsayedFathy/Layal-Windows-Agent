using System.IO;
using Layal.Services;
using Layal.SpeechLanguage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class NativeLanguageIntentOrderTests
{
    [TestMethod]
    public void StrictCommandsWinOverLooseTimeDateAndSystemIntents()
    {
        if (!System.OperatingSystem.IsWindows())
            Assert.Inconclusive("NativeLearningService uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var parser = new NativeLanguageParser(new NativeLearningService(Path.Combine(temp.Path, "native-learning.dpapi")));

        var save = parser.Parse("احفظ في الذاكرة موعد: الساعة كام");
        Assert.AreEqual(NativeIntent.SaveMemory, save.Intent, save.ToString());

        var search = parser.Parse("ابحث عن معلومات النظام");
        Assert.AreEqual(NativeIntent.SearchWeb, search.Intent, search.ToString());

        // Plain loose phrases still work.
        Assert.AreEqual(NativeIntent.CurrentTime, parser.Parse("الساعة كام").Intent);
        Assert.AreEqual(NativeIntent.SystemInfo, parser.Parse("معلومات النظام").Intent);
    }

    [TestMethod]
    public void LookupNormalizerUnifiesTaMarbutaAndHa()
    {
        Assert.AreEqual(
            SpeechTextNormalizer.NormalizeForLookup("مع السلامه"),
            SpeechTextNormalizer.NormalizeForLookup("مع السلامة"));
    }

    [TestMethod]
    public void StandardFarewellAndCommandWithMinAreClassified()
    {
        var engine = SpeechLanguageEngine.CreateDefault();
        Assert.AreEqual(DialogueIntent.Farewell, engine.Analyze("مع السلامة").Intent);
        Assert.AreEqual(DialogueIntent.Command, engine.Analyze("افتح الملف من المجلد").Intent);
        Assert.AreEqual(DialogueIntent.Question, engine.Analyze("من هو الرئيس").Intent);
    }
}
