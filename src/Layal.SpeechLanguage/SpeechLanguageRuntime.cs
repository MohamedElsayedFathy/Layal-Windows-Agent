namespace Layal.SpeechLanguage;

public interface ISpeechRecognitionProvider
{
    Task<string> RecognizeAsync(ReadOnlyMemory<byte> waveAudio, CancellationToken cancellationToken);
}

public interface ISpeechSynthesisProvider
{
    Task SynthesizeAndPlayAsync(string text, SpeechSynthesisOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Provider-independent orchestration. The host retains microphone access, device selection,
/// consent, and privacy policy; this library only receives bounded audio and text.
/// </summary>
public sealed class SpeechLanguageRuntime
{
    public const int MaximumWaveAudioBytes = 64 * 1024 * 1024;
    private readonly SpeechLanguageEngine _engine;
    private readonly ISpeechRecognitionProvider? _recognitionProvider;
    private readonly ISpeechSynthesisProvider? _synthesisProvider;

    public SpeechLanguageRuntime(
        SpeechLanguageEngine engine,
        ISpeechRecognitionProvider? recognitionProvider = null,
        ISpeechSynthesisProvider? synthesisProvider = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _recognitionProvider = recognitionProvider;
        _synthesisProvider = synthesisProvider;
    }

    public SpeechLanguageAnalysis AnalyzeText(string? text) => _engine.Analyze(text);

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> waveAudio,
        CancellationToken cancellationToken = default)
    {
        if (_recognitionProvider is null)
            throw new NotSupportedException("No speech-recognition provider is configured.");
        if (waveAudio.Length <= 44 || waveAudio.Length > MaximumWaveAudioBytes || !IsWavePayload(waveAudio))
            throw new ArgumentOutOfRangeException(nameof(waveAudio), "Audio must be a valid RIFF/WAVE payload within the local size limit.");

        cancellationToken.ThrowIfCancellationRequested();
        var transcript = await _recognitionProvider.RecognizeAsync(waveAudio, cancellationToken).ConfigureAwait(false);
        var analysis = _engine.Analyze(transcript);
        return new SpeechRecognitionResult(analysis.NormalizedText, analysis);
    }

    private static bool IsWavePayload(ReadOnlyMemory<byte> payload)
    {
        var audio = payload.Span;
        if (audio.Length < 44 ||
            audio[0] != (byte)'R' || audio[1] != (byte)'I' ||
            audio[2] != (byte)'F' || audio[3] != (byte)'F' ||
            audio[8] != (byte)'W' || audio[9] != (byte)'A' ||
            audio[10] != (byte)'V' || audio[11] != (byte)'E')
            return false;

        var declaredLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(4, 4));
        return declaredLength >= 36 && declaredLength <= audio.Length - 8;
    }

    public Task SpeakAsync(
        string? text,
        SpeechSynthesisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_synthesisProvider is null)
            throw new NotSupportedException("No speech-synthesis provider is configured.");
        var normalized = SpeechTextNormalizer.NormalizeTranscript(text);
        if (normalized.Length == 0)
            return Task.CompletedTask;
        if (normalized.Length > SpeechLanguageEngine.MaximumTranscriptCharacters)
            throw new ArgumentOutOfRangeException(nameof(text), "Speech text exceeds the configured character limit.");

        cancellationToken.ThrowIfCancellationRequested();
        return _synthesisProvider.SynthesizeAndPlayAsync(
            normalized,
            options ?? new SpeechSynthesisOptions(),
            cancellationToken);
    }
}

/// <summary>Short in-memory dialogue context; no transcript is persisted by this class.</summary>
public sealed class DialogueSession
{
    public const int MaximumTurns = 32;
    private readonly SpeechLanguageEngine _engine;
    private readonly List<DialogueTurn> _turns = new();

    public string? CurrentTopicId { get; private set; }
    public IReadOnlyList<DialogueTurn> Turns => _turns.ToArray();

    public DialogueSession(SpeechLanguageEngine engine) =>
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public void AddUserTurn(string? text) => AddTurn(DialogueSpeaker.User, text);
    public void AddAssistantTurn(string? text) => AddTurn(DialogueSpeaker.Assistant, text);

    public IReadOnlyList<DialogueTurn> GetRecentTurns(int maximum = 10) =>
        _turns.TakeLast(Math.Clamp(maximum, 1, MaximumTurns)).ToArray();

    public void Clear()
    {
        _turns.Clear();
        CurrentTopicId = null;
    }

    private void AddTurn(DialogueSpeaker speaker, string? text)
    {
        var analysis = _engine.Analyze(text);
        if (analysis.NormalizedText.Length == 0)
            return;
        _turns.Add(new DialogueTurn(
            speaker,
            analysis.NormalizedText,
            analysis.Language.PrimaryLanguage,
            DateTimeOffset.UtcNow,
            analysis.MatchingTopics.FirstOrDefault()?.Id));
        if (_turns.Count > MaximumTurns)
            _turns.RemoveRange(0, _turns.Count - MaximumTurns);
        var detectedTopic = analysis.MatchingTopics.FirstOrDefault()?.Id;
        if (!string.IsNullOrWhiteSpace(detectedTopic))
            CurrentTopicId = detectedTopic;
    }
}

/// <summary>
/// Deterministic, bounded replies from the local reference. It never invents definitions,
/// executes commands, or turns a recognized request into permission to use a tool.
/// </summary>
public sealed class SpeechLanguageReferenceResponder
{
    private readonly SpeechLanguageEngine _engine;

    public SpeechLanguageReferenceResponder(SpeechLanguageEngine engine) =>
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public bool TryRespond(string? input, bool preferArabic, out string response)
    {
        response = string.Empty;
        var query = SpeechTextNormalizer.NormalizeForLookup(input);
        if (query.Length == 0)
            return false;

        var language = _engine.Analyze(input).Language.PrimaryLanguage;

        if (TryExtractVerbQuery(query, out var verbTerm))
        {
            var verbs = _engine.Reference.FindVerbParadigms(verbTerm, language, 3);
            if (verbs.Count == 0) return false;
            response = FormatVerbs(verbs, preferArabic);
            return true;
        }

        if (TryExtractGrammarQuery(query, out var grammarTerm))
        {
            var rules = string.IsNullOrWhiteSpace(grammarTerm)
                ? _engine.Reference.GrammarRules.Take(3).ToArray()
                : _engine.Reference.FindGrammar(grammarTerm, language, maximumResults: 4);
            if (rules.Count == 0) return false;
            response = FormatGrammar(rules, preferArabic);
            return true;
        }

        if (TryExtractPhraseQuery(query, out var phraseTerm))
        {
            var patterns = _engine.Reference.FindDialoguePatterns(
                phraseTerm, DialogueIntent.Unknown, language, 4);
            if (patterns.Count == 0) return false;
            response = FormatPatterns(patterns, language, preferArabic);
            return true;
        }

        if (TryExtractDefinitionQuery(query, out var term))
        {
            var matches = _engine.Reference.Find(term, language, 3);
            if (matches.Count == 0) return false;
            response = FormatDefinitions(matches, preferArabic);
            return true;
        }

        if (TryExtractTopicQuery(query, out var topicQuery))
        {
            var topics = topicQuery.Length == 0
                ? _engine.Reference.GetTopics(4)
                : _engine.Reference.FindTopics(topicQuery, language, 4);
            if (topics.Count == 0) return false;
            response = FormatTopics(topics, language, preferArabic);
            return true;
        }

        return false;
    }

    private static bool TryExtractDefinitionQuery(string value, out string term)
    {
        string[] prefixes =
        [
            "ما معنى كلمة ", "ما معنى ", "معنى كلمة ", "معنى ", "يعني ايه كلمة ",
            "يعني ايه ", "ايه معنى ", "اشرح كلمة ", "ما المقصود ب ",
            "what is the meaning of ", "meaning of ", "define "
        ];
        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            term = value[prefix.Length..].Trim();
            if (term.StartsWith("كلمة ", StringComparison.Ordinal))
                term = term["كلمة ".Length..].Trim();
            return term.Length >= 2;
        }

        const string whatDoes = "what does ";
        if (value.StartsWith(whatDoes, StringComparison.Ordinal) &&
            value.EndsWith(" mean", StringComparison.Ordinal))
        {
            term = value[whatDoes.Length..^" mean".Length].Trim();
            return term.Length >= 2;
        }

        term = string.Empty;
        return false;
    }

    private static bool TryExtractGrammarQuery(string value, out string term)
    {
        string[] prefixes =
        [
            "اشرح قاعدة ", "اشرحلي قاعدة ", "ما قاعدة ", "ايه قاعدة ",
            "القاعدة النحوية ", "قاعدة ", "explain grammar ", "grammar rule ",
            "what is the grammar of ", "explain the rule "
        ];
        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            term = value[prefix.Length..].Trim();
            return true;
        }

        if (value is "اشرح القواعد" or "قواعد اللغة" or "grammar")
        {
            term = string.Empty;
            return true;
        }

        term = string.Empty;
        return false;
    }

    private static bool TryExtractVerbQuery(string value, out string term)
    {
        string[] prefixes =
        [
            "صرف الفعل ", "تصريف الفعل ", "تصريف ", "صرف ",
            "conjugate ", "conjugation of ", "verb forms of "
        ];
        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            term = value[prefix.Length..].Trim();
            return term.Length >= 2;
        }

        term = string.Empty;
        return false;
    }

    private static bool TryExtractPhraseQuery(string value, out string term)
    {
        string[] prefixes =
        [
            "ازاي اقول ", "كيف اقول ", "عبارة عن ", "عبارة ل ",
            "قول بالانجليزي ", "قول بالمصري ", "how do i say ", "how to say ",
            "phrase for ", "expression for "
        ];
        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            term = value[prefix.Length..].Trim();
            return term.Length >= 2;
        }

        term = string.Empty;
        return false;
    }

    private static bool TryExtractTopicQuery(string value, out string topic)
    {
        string[] generic =
        [
            "اقترح موضوع", "اقترح لي موضوع", "عايز موضوع", "عاوز موضوع", "عايزه موضوع",
            "موضوع نتكلم عنه", "موضوع نتكلم فيه", "خلينا نتكلم عن",
            "suggest a topic", "give me a topic", "what should we talk about"
        ];
        if (generic.Any(marker => value.Contains(marker, StringComparison.Ordinal)))
        {
            var separator = value.Contains("عن ", StringComparison.Ordinal) ? "عن " :
                value.Contains("about ", StringComparison.Ordinal) ? "about " : string.Empty;
            topic = separator.Length == 0 ? string.Empty :
                value[(value.IndexOf(separator, StringComparison.Ordinal) + separator.Length)..].Trim();
            return true;
        }
        topic = string.Empty;
        return false;
    }

    private static string FormatDefinitions(IReadOnlyList<LinguisticEntry> entries, bool arabic)
    {
        var lines = entries.Select(entry =>
        {
            var meaning = arabic ? entry.MeaningArabic : entry.MeaningEnglish;
            var example = entry.Examples.FirstOrDefault() ?? string.Empty;
            var pronunciation = string.IsNullOrWhiteSpace(entry.Pronunciation) ? string.Empty : " (" + entry.Pronunciation + ")";
            var exampleLine = string.IsNullOrWhiteSpace(example) ? string.Empty :
                Environment.NewLine + (arabic ? "مثال: " : "Example: ") + example;
            return "• " + entry.Term + pronunciation + ": " + meaning + exampleLine;
        });
        return (arabic ? "وجدت هذه المدخلات في المرجع اللغوي المحلي:" : "I found these entries in the local language reference:")
            + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string FormatTopics(IReadOnlyList<ConversationTopic> topics, SpeechLanguageKind language, bool arabic)
    {
        var lines = topics.Select(topic =>
        {
            var name = arabic ? topic.NameArabic : topic.NameEnglish;
            var prompt = language switch
            {
                SpeechLanguageKind.EgyptianArabic => topic.PromptsEgyptian.FirstOrDefault(),
                SpeechLanguageKind.English => topic.PromptsEnglish.FirstOrDefault(),
                _ => topic.PromptsArabicFusha.FirstOrDefault()
            };
            prompt ??= arabic ? topic.PromptsArabicFusha.FirstOrDefault() : topic.PromptsEnglish.FirstOrDefault();
            return "• " + name + (string.IsNullOrWhiteSpace(prompt) ? string.Empty : Environment.NewLine + "  " + prompt);
        });
        return (arabic ? "إليك موضوعات مناسبة لبدء الحوار:" : "Here are some topics to start a conversation:")
            + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string FormatGrammar(IReadOnlyList<GrammarRule> rules, bool arabic)
    {
        var lines = rules.Select(rule =>
        {
            var title = arabic ? rule.TitleArabic : rule.TitleEnglish;
            var explanation = arabic ? rule.ExplanationArabic : rule.ExplanationEnglish;
            var examples = rule.Examples.Count == 0 ? string.Empty :
                Environment.NewLine + (arabic ? "أمثلة: " : "Examples: ") + string.Join("؛ ", rule.Examples.Take(3));
            var pattern = string.IsNullOrWhiteSpace(rule.Pattern) ? string.Empty :
                Environment.NewLine + (arabic ? "النمط: " : "Pattern: ") + rule.Pattern;
            return "• " + title + Environment.NewLine + explanation + pattern + examples;
        });
        return (arabic ? "هذه القواعد الموجودة في المرجع المحلي:" : "These rules are available in the local reference:")
            + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string FormatVerbs(IReadOnlyList<VerbParadigm> verbs, bool arabic)
    {
        var lines = verbs.Select(verb =>
        {
            var meaning = arabic ? verb.MeaningArabic : verb.MeaningEnglish;
            var labels = arabic
                ? new[] { "الماضي", "المضارع", "المستقبل", "الأمر", "المصدر" }
                : new[] { "Past", "Present", "Future", "Imperative", "Verbal noun" };
            var forms = new[] { verb.Past, verb.Present, verb.Future, verb.Imperative, verb.VerbalNoun };
            var rows = labels.Zip(forms, (label, form) => string.IsNullOrWhiteSpace(form)
                ? string.Empty : label + ": " + form).Where(value => value.Length > 0);
            return "• " + verb.Lemma + " — " + meaning + Environment.NewLine + string.Join(Environment.NewLine, rows)
                + (string.IsNullOrWhiteSpace(verb.NotesArabic) ? string.Empty : Environment.NewLine + verb.NotesArabic);
        });
        return (arabic ? "هذه صيغ الأفعال في المرجع المحلي:" : "These verb forms are in the local reference:")
            + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string FormatPatterns(IReadOnlyList<DialoguePattern> patterns, SpeechLanguageKind language, bool arabic)
    {
        var lines = patterns.Select(pattern =>
        {
            var utterance = pattern.Utterances.FirstOrDefault() ?? string.Empty;
            var reply = pattern.Replies.FirstOrDefault() ?? string.Empty;
            var label = arabic ? pattern.SituationArabic : pattern.SituationEnglish;
            return "• " + label
                + (utterance.Length == 0 ? string.Empty : Environment.NewLine + (arabic ? "يمكنك أن تقول: " : "You can say: ") + utterance)
                + (reply.Length == 0 ? string.Empty : Environment.NewLine + (arabic ? "رد مناسب: " : "Possible reply: ") + reply);
        });
        return (arabic ? "هذه تعبيرات مناسبة للموقف:" : "Here are phrases for that situation:")
            + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, lines);
    }
}
