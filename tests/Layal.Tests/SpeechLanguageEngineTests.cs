using System;
using System.Linq;
using System.Text;
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
    public void ReferenceContainsThreeLanguageLexiconAndTopics()
    {
        Assert.IsTrue(_engine.Reference.Find("معلش", SpeechLanguageKind.EgyptianArabic).Any());
        Assert.IsTrue(_engine.Reference.Find("من فضلك", SpeechLanguageKind.ArabicStandard).Any());
        Assert.IsTrue(_engine.Reference.Find("thank you", SpeechLanguageKind.English).Any());
        Assert.IsTrue(_engine.Reference.Entries.Count >= 200);
        Assert.IsTrue(_engine.Reference.Topics.Count >= 20);
    }

    [TestMethod]
    public void ReferenceCoverageIncludesLexiconGrammarDialogueAndVerbs()
    {
        var coverage = _engine.Reference.GetCoverage();
        Assert.AreEqual("2.0.0", coverage.ReferenceVersion);
        Assert.IsTrue(coverage.LexicalEntries >= 200);
        Assert.IsTrue(coverage.ConversationTopics >= 20);
        Assert.IsTrue(coverage.GrammarRules >= 60);
        Assert.IsTrue(coverage.DialoguePatterns >= 60);
        Assert.IsTrue(coverage.VerbParadigms >= 30);
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
    public void GrammarLookupFindsFushaEgyptianAndEnglishRules()
    {
        Assert.IsTrue(_engine.Reference.FindGrammar("الجملة الاسمية", SpeechLanguageKind.ArabicStandard)
            .Any(rule => rule.Id == "msa-nominal-sentence"));
        Assert.IsTrue(_engine.Reference.FindGrammar("النفي بما ش", SpeechLanguageKind.EgyptianArabic)
            .Any(rule => rule.Id == "eg-negation-ma-sh"));
        Assert.IsTrue(_engine.Reference.FindGrammar("subject verb object", SpeechLanguageKind.English)
            .Any(rule => rule.Id == "en-word-order"));
    }

    [TestMethod]
    public void VerbLookupSupportsLemmaAndInflectedForms()
    {
        Assert.IsTrue(_engine.Reference.FindVerbParadigms("كتب", SpeechLanguageKind.ArabicStandard)
            .Any(verb => verb.Id == "msa-write"));
        Assert.IsTrue(_engine.Reference.FindVerbParadigms("بشوف", SpeechLanguageKind.EgyptianArabic)
            .Any(verb => verb.Id == "eg-see"));
        Assert.IsTrue(_engine.Reference.FindVerbParadigms("went", SpeechLanguageKind.English)
            .Any(verb => verb.Id == "en-go"));
    }

    [TestMethod]
    public void DialoguePatternsClassifyRequestAndAgreementWithoutExecutingAnything()
    {
        var request = _engine.Analyze("لو سمحت ابعتلي الملف.");
        Assert.AreEqual(DialogueIntent.Request, request.Intent);
        Assert.IsTrue(request.MatchingDialoguePatterns.Any(pattern => pattern.Intent == "Request"));

        var agreement = _engine.Analyze("أتفق معك في هذه النقطة.");
        Assert.AreEqual(DialogueIntent.Agreement, agreement.Intent);
    }

    [TestMethod]
    public void EnglishPhrasebookClassifiesGratitude()
    {
        var analysis = _engine.Analyze("Thank you for your help.");
        Assert.AreEqual(DialogueIntent.Gratitude, analysis.Intent);
        Assert.IsTrue(analysis.MatchingDialoguePatterns.Any(pattern => pattern.Intent == "Gratitude"));
    }

    [TestMethod]
    public void AnalysisExposesRelatedGrammarAndDialogueKnowledge()
    {
        var analysis = _engine.Analyze("I prefer the first option because it is simpler.");
        Assert.IsTrue(analysis.MatchingGrammarRules.Count > 0 || analysis.MatchingDialoguePatterns.Count > 0);
    }

    [TestMethod]
    public void ReferenceResponderAnswersDefinitionTopicGrammarVerbAndPhraseRequests()
    {
        var responder = new SpeechLanguageReferenceResponder(_engine);

        Assert.IsTrue(responder.TryRespond("يعني ايه معلش", true, out var definition));
        StringAssert.Contains(definition, "معلش");

        Assert.IsTrue(responder.TryRespond("عايز موضوع نتكلم عنه", true, out var topic));
        StringAssert.Contains(topic, "موضوعات");

        Assert.IsTrue(responder.TryRespond("اشرح قاعدة الجملة الاسمية", true, out var grammar));
        StringAssert.Contains(grammar, "الجملة الاسمية");

        Assert.IsTrue(responder.TryRespond("صرف الفعل كتب", true, out var verb));
        StringAssert.Contains(verb, "كتب");

        Assert.IsTrue(responder.TryRespond("ازاي اقول شكرا", true, out var phrase));
        StringAssert.Contains(phrase, "تعبيرات مناسبة");
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
        var audio = CreateWaveAudio();

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
    public async Task RuntimeRejectsMalformedWaveBeforeCallingProvider()
    {
        var provider = new FakeSpeechProvider("hello");
        var runtime = new SpeechLanguageRuntime(_engine, provider, provider);
        var malformed = new byte[128];
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => runtime.RecognizeAsync(malformed));
        Assert.IsFalse(provider.RecognitionWasCalled);
    }

    [TestMethod]
    public void TextAnalysisDoesNotRequireSpeechProviders()
    {
        var runtime = new SpeechLanguageRuntime(_engine);
        Assert.AreEqual(SpeechLanguageKind.English, runtime.AnalyzeText("Hello").Language.PrimaryLanguage);
        Assert.ThrowsExactly<NotSupportedException>(() => runtime.SpeakAsync("Hello"));
    }

    private static byte[] CreateWaveAudio()
    {
        var bytes = new byte[46];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BitConverter.GetBytes(16u).CopyTo(bytes, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(16_000u).CopyTo(bytes, 24);
        BitConverter.GetBytes(32_000u).CopyTo(bytes, 28);
        BitConverter.GetBytes((ushort)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((ushort)16).CopyTo(bytes, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BitConverter.GetBytes(2u).CopyTo(bytes, 40);
        bytes[44] = 0;
        bytes[45] = 0;
        return bytes;
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
