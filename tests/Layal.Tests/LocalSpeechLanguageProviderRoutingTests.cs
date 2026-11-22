using Layal.Services;
using Layal.SpeechLanguage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalSpeechLanguageProviderRoutingTests
{
    private readonly SpeechLanguageEngine _engine = SpeechLanguageEngine.CreateDefault();

    [TestMethod]
    public void EgyptianArabicUsesEgyptianVoiceRoute()
    {
        var analysis = _engine.Analyze("إزيك؟ عاملة إيه النهارده؟");
        Assert.AreEqual(SpeechLanguageKind.EgyptianArabic, analysis.Language.PrimaryLanguage);
        Assert.AreEqual("arz", LocalSpeechLanguageProvider.SelectLanguageCode(analysis));
    }

    [TestMethod]
    public void ModernStandardArabicUsesExploratoryArabicRouteRatherThanEgyptianRoute()
    {
        var analysis = _engine.Analyze("كيف حالك؟ أحتاج إلى معرفة التفاصيل.");
        Assert.AreEqual(SpeechLanguageKind.ArabicStandard, analysis.Language.PrimaryLanguage);
        Assert.AreEqual("ar", LocalSpeechLanguageProvider.SelectLanguageCode(analysis));
    }

    [TestMethod]
    public void ArabicWithWeakDialectEvidenceDefaultsToEgyptianVoiceRoute()
    {
        var analysis = _engine.Analyze("تمام، شكرًا لك.");
        Assert.AreEqual(SpeechLanguageKind.ArabicUnspecified, analysis.Language.PrimaryLanguage);
        Assert.AreEqual("arz", LocalSpeechLanguageProvider.SelectLanguageCode(analysis));
    }

    [TestMethod]
    public void EnglishUsesEnglishVoiceRoute()
    {
        var analysis = _engine.Analyze("Hello! I'm Layal. How can I help you today?");
        Assert.AreEqual(SpeechLanguageKind.English, analysis.Language.PrimaryLanguage);
        Assert.AreEqual("en", LocalSpeechLanguageProvider.SelectLanguageCode(analysis));
    }

    [TestMethod]
    public void MixedTextUsesEgyptianRouteWhenAnyArabicSegmentIsEgyptian()
    {
        var analysis = _engine.Analyze("إزيك؟ Please review the final report.");
        Assert.AreEqual(SpeechLanguageKind.Mixed, analysis.Language.PrimaryLanguage);
        Assert.AreEqual("arz", LocalSpeechLanguageProvider.SelectLanguageCode(analysis));
    }

    [TestMethod]
    public void MixedTextUsesMsaRouteWhenEveryArabicSegmentHasMsaEvidence()
    {
        var analysis = _engine.Analyze("كيف حالك؟ Please review the details.");
        Assert.AreEqual(SpeechLanguageKind.Mixed, analysis.Language.PrimaryLanguage);
        Assert.AreEqual("ar", LocalSpeechLanguageProvider.SelectLanguageCode(analysis));
    }
}
