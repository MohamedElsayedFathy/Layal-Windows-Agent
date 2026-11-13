using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Layal.SpeechLanguage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SpeechLanguageEngineTests
{
    private SpeechLanguageEngine _engine = null!;

    [TestInitialize]
    public void Initialize() => _engine = SpeechLanguageEngine.CreateDefault();

    [TestMethod]
    public void DetectsEgyptianArabicFromColloquialMarkers()
    {
        var analysis = _engine.Analyze("إزيك؟ عامل إيه النهارده؟");
        Assert.AreEqual(SpeechLanguageKind.EgyptianArabic, analysis.Language.PrimaryLanguage);
        Assert.AreEqual(DialogueIntent.Greeting, analysis.Intent);
    }

    [TestMethod]
    public void DetectsStandardArabicWithoutEgyptianMarkers()
    {
        var analysis = _engine.Analyze("كيف حالك؟ أحتاج إلى معرفة التفاصيل.");
        Assert.AreEqual(SpeechLanguageKind.ArabicStandard, analysis.Language.PrimaryLanguage);
        Assert.AreEqual(DialogueIntent.Question, analysis.Intent);
    }

    [TestMethod]
    public void DetectsEnglishAndMixedArabicEnglish()
    {
        Assert.AreEqual(SpeechLanguageKind.English, _engine.Analyze("Hello, how are you?").Language.PrimaryLanguage);

        var mixed = _engine.Analyze("إزيك how are you؟");
        Assert.AreEqual(SpeechLanguageKind.Mixed, mixed.Language.PrimaryLanguage);
        Assert.IsTrue(mixed.Language.Segments.Any(segment => segment.Language == SpeechLanguageKind.EgyptianArabic));
        Assert.IsTrue(mixed.Language.Segments.Any(segment => segment.Language == SpeechLanguageKind.English));
    }

    [TestMethod]
    public void TranscriptNormalizationPreservesVisibleWordsAndPunctuation()
    {
        var normalized = SpeechTextNormalizer.NormalizeTranscript("\u200F  إزيك؟   how   are you! \u2069");
        Assert.AreEqual("إزيك؟ how are you!", normalized);
        Assert.AreEqual(string.Empty, SpeechTextNormalizer.NormalizeTranscript(" \t\r\n "));
    }

    [TestMethod]
    public void LookupNormalizationDoesNotReplaceDisplayedTranscript()
    {
        const string transcript = "إِنْتَ عايز إيه؟";
        var analysis = _engine.Analyze(transcript);
        Assert.AreEqual(transcript, analysis.NormalizedText);
        Assert.IsTrue(_engine.Reference.Find("انت عايز ايه").Count > 0);
    }

    [TestMethod]
    public void ReferenceContainsEgyptianFushaAndEnglishEntries()
    {
        Assert.IsTrue(_engine.Reference.Find("معلش", SpeechLanguageKind.EgyptianArabic).Any());
        Assert.IsTrue(_engine.Reference.Find("من فضلك", SpeechLanguageKind.ArabicStandard).Any());
        Assert.IsTrue(_engine.Reference.Find("thank you", SpeechLanguageKind.English).Any());
        Assert.IsTrue(_engine.Reference.Entries.Count >= 50);
        Assert.IsTrue(_engine.Reference.Topics.Count >= 10);
    }

    [TestMethod]
    public void TopicReferenceSupportsAllThreeLanguageStyles()
    {
        var topic = _engine.Reference.GetTopic("technology");
        Assert.IsNotNull(topic);
        Assert.IsTrue(topic!.PromptsArabicFusha.Count > 0);
        Assert.IsTrue(topic.PromptsEgyptian.Count > 0);
        Assert.IsTrue(topic.PromptsEnglish.Count > 0);
    }

    [TestMethod]
    public void ReferenceResponderAnswersExplicitDefinitionAndTopicRequests()
    {
        var responder = new SpeechLanguageReferenceResponder(_engine);
        Assert.IsTrue(responder.TryRespond("يعني ايه معلش", true, out var definition));
        StringAssert.Contains(definition, "معلش");
        Assert.IsTrue(responder.TryRespond("عايز موضوع نتكلم عنه", true, out var topic));
        StringAssert.Contains(topic, "موضوعات");
    }

    [TestMethod]
    public void DialogueSessionKeepsBoundedInMemoryContext()
    {
        var session = new DialogueSession(_engine);
        for (var index = 0; index < DialogueSession.MaximumTurns + 5; index++)
            session.AddUserTurn("إزيك، خلينا نتكلم عن التكنولوجيا.");

        Assert.AreEqual(DialogueSession.MaximumTurns, session.Turns.Count);
        Assert.IsNotNull(session.CurrentTopicId);
        session.Clear();
        Assert.AreEqual(0, session.Turns.Count);
        Assert.IsNull(session.CurrentTopicId);
    }

    [TestMethod]
    public async Task RuntimeUsesInjectedProvidersForRecognitionAndSynthesis()
    {
        var provider = new FakeSpeechProvider("إزيك");
        var runtime = new SpeechLanguageRuntime(_engine, provider, provider);
        var audio = new byte[128];

        var recognition = await runtime.RecognizeAsync(audio);
        Assert.AreEqual("إزيك", recognition.Text);
        Assert.AreEqual(SpeechLanguageKind.EgyptianArabic, recognition.Analysis.Language.PrimaryLanguage);

        var options = new SpeechSynthesisOptions("output-1", "gentle");
        await runtime.SpeakAsync("Hello", options);
        Assert.AreEqual("Hello", provider.LastSpokenText);
        Assert.AreEqual("output-1", provider.LastOptions!.OutputDeviceId);
        Assert.AreEqual("gentle", provider.LastOptions.DeliveryStyleId);
    }

    [TestMethod]
    public async Task RuntimeRejectsEmptyAudioBeforeCallingProvider()
    {
        var provider = new FakeSpeechProvider("hello");
        var runtime = new SpeechLanguageRuntime(_engine, provider, provider);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => runtime.RecognizeAsync(new byte[44]));
        Assert.IsFalse(provider.RecognitionWasCalled);
    }

    [TestMethod]
    public void TextAnalysisDoesNotRequireSpeechProviders()
    {
        var runtime = new SpeechLanguageRuntime(_engine);
        Assert.AreEqual(SpeechLanguageKind.English, runtime.AnalyzeText("Hello").Language.PrimaryLanguage);
        Assert.ThrowsExactly<NotSupportedException>(() => runtime.SpeakAsync("Hello"));
    }

    private sealed class FakeSpeechProvider : ISpeechRecognitionProvider, ISpeechSynthesisProvider
    {
        private readonly string _recognizedText;

        public FakeSpeechProvider(string recognizedText) => _recognizedText = recognizedText;
        public bool RecognitionWasCalled { get; private set; }
        public string? LastSpokenText { get; private set; }
        public SpeechSynthesisOptions? LastOptions { get; private set; }

        public Task<string> RecognizeAsync(ReadOnlyMemory<byte> waveAudio, CancellationToken cancellationToken)
        {
            RecognitionWasCalled = true;
            return Task.FromResult(_recognizedText);
        }

        public Task SynthesizeAndPlayAsync(
            string text,
            SpeechSynthesisOptions options,
            CancellationToken cancellationToken)
        {
            LastSpokenText = text;
            LastOptions = options;
            return Task.CompletedTask;
        }
    }
}
