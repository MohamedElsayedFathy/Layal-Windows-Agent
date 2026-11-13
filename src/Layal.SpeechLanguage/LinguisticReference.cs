using System.Reflection;
using System.Text.Json;

namespace Layal.SpeechLanguage;

/// <summary>
/// Versioned local linguistic reference with lexical, grammatical, conversational, and verb data.
/// The reference is designed for incremental, reviewed extension rather than claiming to enumerate
/// every word or construction in any language.
/// </summary>
public sealed class LinguisticReference
{
    private const string ResourceName = "Layal.SpeechLanguage.Data.speech-language-reference.json";
    private readonly List<LinguisticEntry> _entries;
    private readonly List<ConversationTopic> _topics;
    private readonly List<GrammarRule> _grammarRules;
    private readonly List<DialoguePattern> _dialoguePatterns;
    private readonly List<VerbParadigm> _verbParadigms;

    public int SchemaVersion { get; }
    public string ReferenceVersion { get; }
    public string CoverageNote { get; }
    public IReadOnlyList<LinguisticEntry> Entries => _entries.AsReadOnly();
    public IReadOnlyList<ConversationTopic> Topics => _topics.AsReadOnly();
    public IReadOnlyList<GrammarRule> GrammarRules => _grammarRules.AsReadOnly();
    public IReadOnlyList<DialoguePattern> DialoguePatterns => _dialoguePatterns.AsReadOnly();
    public IReadOnlyList<VerbParadigm> VerbParadigms => _verbParadigms.AsReadOnly();

    public LinguisticReference(
        IEnumerable<LinguisticEntry> entries,
        IEnumerable<ConversationTopic> topics,
        int schemaVersion = 2,
        string referenceVersion = "2.0.0",
        string coverageNote = "Expanded practical reference; extend with reviewed entries.",
        IEnumerable<GrammarRule>? grammarRules = null,
        IEnumerable<DialoguePattern>? dialoguePatterns = null,
        IEnumerable<VerbParadigm>? verbParadigms = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(topics);

        _entries = entries
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Term))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        _topics = topics
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        _grammarRules = (grammarRules ?? Array.Empty<GrammarRule>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        _dialoguePatterns = (dialoguePatterns ?? Array.Empty<DialoguePattern>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        _verbParadigms = (verbParadigms ?? Array.Empty<VerbParadigm>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Lemma))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        SchemaVersion = schemaVersion;
        ReferenceVersion = string.IsNullOrWhiteSpace(referenceVersion) ? "2.0.0" : referenceVersion;
        CoverageNote = coverageNote;
    }

    public static LinguisticReference LoadDefault()
    {
        var assembly = typeof(LinguisticReference).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded linguistic reference '{ResourceName}' was not found.");

        var document = JsonSerializer.Deserialize<ReferenceDocument>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("The embedded linguistic reference is empty or invalid.");

        if (document.SchemaVersion != 2)
            throw new InvalidDataException($"Unsupported linguistic reference schema version: {document.SchemaVersion}.");

        return new LinguisticReference(
            document.Entries ?? new List<LinguisticEntry>(),
            document.Topics ?? new List<ConversationTopic>(),
            document.SchemaVersion,
            document.ReferenceVersion ?? "2.0.0",
            document.CoverageNote ?? "Expanded practical reference.",
            document.GrammarRules,
            document.DialoguePatterns,
            document.VerbParadigms);
    }

    public LinguisticReferenceCoverage GetCoverage() => new(
        _entries.Count,
        _topics.Count,
        _grammarRules.Count,
        _dialoguePatterns.Count,
        _verbParadigms.Count,
        ReferenceVersion,
        CoverageNote);

    public IReadOnlyList<LinguisticEntry> Find(
        string? query,
        SpeechLanguageKind language = SpeechLanguageKind.Unknown,
        int maximumResults = 8)
    {
        var normalizedQuery = SpeechTextNormalizer.NormalizeForLookup(query);
        if (normalizedQuery.Length < 2 || maximumResults <= 0)
            return Array.Empty<LinguisticEntry>();

        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _entries
            .Select(entry => new { Entry = entry, Score = ScoreEntry(entry, normalizedQuery, tokens, language) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.Term, StringComparer.Ordinal)
            .Take(Math.Clamp(maximumResults, 1, 50))
            .Select(item => item.Entry)
            .ToArray();
    }

    public IReadOnlyList<ConversationTopic> FindTopics(
        string? query,
        SpeechLanguageKind language = SpeechLanguageKind.Unknown,
        int maximumResults = 5)
    {
        var normalizedQuery = SpeechTextNormalizer.NormalizeForLookup(query);
        if (normalizedQuery.Length < 2 || maximumResults <= 0)
            return Array.Empty<ConversationTopic>();

        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _topics
            .Select(topic => new { Topic = topic, Score = ScoreTopic(topic, normalizedQuery, tokens, language) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Topic.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(maximumResults, 1, 20))
            .Select(item => item.Topic)
            .ToArray();
    }

    public IReadOnlyList<ConversationTopic> GetTopics(int maximumResults = 20) =>
        _topics.Take(Math.Clamp(maximumResults, 0, 50)).ToArray();

    public ConversationTopic? GetTopic(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _topics.FirstOrDefault(topic => string.Equals(topic.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<GrammarRule> FindGrammar(
        string? query,
        SpeechLanguageKind language = SpeechLanguageKind.Unknown,
        string? category = null,
        int maximumResults = 5)
    {
        var normalizedQuery = SpeechTextNormalizer.NormalizeForLookup(query);
        var normalizedCategory = SpeechTextNormalizer.NormalizeForLookup(category);
        if (maximumResults <= 0)
            return Array.Empty<GrammarRule>();

        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _grammarRules
            .Select(rule => new
            {
                Rule = rule,
                Score = ScoreGrammarRule(rule, normalizedQuery, tokens, language, normalizedCategory)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Rule.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(maximumResults, 1, 20))
            .Select(item => item.Rule)
            .ToArray();
    }

    public IReadOnlyList<DialoguePattern> FindDialoguePatterns(
        string? query,
        DialogueIntent intent = DialogueIntent.Unknown,
        SpeechLanguageKind language = SpeechLanguageKind.Unknown,
        int maximumResults = 5)
    {
        var normalizedQuery = SpeechTextNormalizer.NormalizeForLookup(query);
        if (maximumResults <= 0)
            return Array.Empty<DialoguePattern>();

        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _dialoguePatterns
            .Select(pattern => new
            {
                Pattern = pattern,
                Score = ScoreDialoguePattern(pattern, normalizedQuery, tokens, intent, language)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Pattern.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(maximumResults, 1, 30))
            .Select(item => item.Pattern)
            .ToArray();
    }

    public DialoguePattern? GetDialoguePattern(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _dialoguePatterns.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<VerbParadigm> FindVerbParadigms(
        string? query,
        SpeechLanguageKind language = SpeechLanguageKind.Unknown,
        int maximumResults = 5)
    {
        var normalizedQuery = SpeechTextNormalizer.NormalizeForLookup(query);
        if (normalizedQuery.Length < 2 || maximumResults <= 0)
            return Array.Empty<VerbParadigm>();

        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _verbParadigms
            .Select(verb => new { Verb = verb, Score = ScoreVerb(verb, normalizedQuery, tokens, language) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Verb.Lemma, StringComparer.Ordinal)
            .Take(Math.Clamp(maximumResults, 1, 15))
            .Select(item => item.Verb)
            .ToArray();
    }

    public VerbParadigm? GetVerbParadigm(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _verbParadigms.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns a literal-trigger conversational intent from the reviewed local phrasebook.
    /// Triggers are data, never executable commands or regular expressions.
    /// </summary>
    public DialogueIntent DetectIntent(string? text)
    {
        var normalized = SpeechTextNormalizer.NormalizeForLookup(text);
        if (normalized.Length == 0)
            return DialogueIntent.Unknown;

        var matches = _dialoguePatterns
            .SelectMany(pattern => pattern.Triggers.Select(trigger => new
            {
                Pattern = pattern,
                Trigger = SpeechTextNormalizer.NormalizeForLookup(trigger)
            }))
            .Where(item => item.Trigger.Length >= 2 && ContainsPhrase(normalized, item.Trigger))
            .OrderByDescending(item => item.Trigger.Length)
            .ThenBy(item => item.Pattern.Id, StringComparer.Ordinal);

        foreach (var match in matches)
        {
            if (Enum.TryParse<DialogueIntent>(match.Pattern.Intent, ignoreCase: true, out var intent))
                return intent;
        }

        return DialogueIntent.Unknown;
    }

    public bool IsEgyptianText(string? text)
    {
        var normalized = SpeechTextNormalizer.NormalizeForLookup(text);
        if (normalized.Length == 0)
            return false;

        var padded = " " + normalized + " ";
        return _entries
            .Where(entry => entry.IsDialectMarker && string.Equals(entry.Dialect, "egyptian", StringComparison.OrdinalIgnoreCase))
            .Any(entry =>
            {
                var marker = SpeechTextNormalizer.NormalizeForLookup(entry.Term);
                return marker.Length > 0 && padded.Contains(" " + marker + " ", StringComparison.Ordinal);
            });
    }

    private static int ScoreEntry(
        LinguisticEntry entry,
        string normalizedQuery,
        IReadOnlyList<string> queryTokens,
        SpeechLanguageKind language)
    {
        var term = SpeechTextNormalizer.NormalizeForLookup(entry.Term);
        if (term.Length == 0)
            return 0;

        var score = 0;
        if (string.Equals(term, normalizedQuery, StringComparison.Ordinal))
            score += 100;
        else if (ContainsPhrase(normalizedQuery, term))
            score += 45;
        else if (ContainsPhrase(term, normalizedQuery))
            score += 35;

        var aliases = entry.Synonyms.Concat(entry.Forms)
            .Append(entry.Transliteration)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(SpeechTextNormalizer.NormalizeForLookup);
        foreach (var alias in aliases)
        {
            if (alias.Length == 0) continue;
            if (string.Equals(alias, normalizedQuery, StringComparison.Ordinal))
                score += 65;
            else if (ContainsPhrase(normalizedQuery, alias))
                score += 20;
            else if (queryTokens.Any(token => token.Length > 1 && alias.Contains(token, StringComparison.Ordinal)))
                score += 8;
        }

        var termTokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        score += queryTokens.Count(token => token.Length > 1 && termTokens.Contains(token, StringComparer.Ordinal)) * 12;
        if (score == 0)
            return 0;

        score += LanguageBonus(entry.Language, entry.Dialect, language);
        return score;
    }

    private static int ScoreTopic(
        ConversationTopic topic,
        string normalizedQuery,
        IReadOnlyList<string> queryTokens,
        SpeechLanguageKind language)
    {
        var candidates = topic.Keywords
            .Concat(new[] { topic.NameArabic, topic.NameEnglish, topic.DescriptionArabic, topic.DescriptionEnglish })
            .Concat(topic.PromptsArabicFusha)
            .Concat(topic.PromptsEgyptian)
            .Concat(topic.PromptsEnglish)
            .Select(SpeechTextNormalizer.NormalizeForLookup)
            .Where(value => value.Length > 0);
        var score = 0;
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, normalizedQuery, StringComparison.Ordinal))
                score += 60;
            else if (ContainsPhrase(candidate, normalizedQuery))
                score += 30;
            else if (ContainsPhrase(normalizedQuery, candidate) && candidate.Length >= 3)
                score += 15;
            var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            score += queryTokens.Count(token => token.Length > 2 && candidateTokens.Contains(token, StringComparer.Ordinal)) * 3;
        }
        if (language == SpeechLanguageKind.EgyptianArabic && topic.PromptsEgyptian.Count > 0)
            score += 2;
        if (language == SpeechLanguageKind.English && topic.PromptsEnglish.Count > 0)
            score += 2;
        return score;
    }

    private static int ScoreGrammarRule(
        GrammarRule rule,
        string normalizedQuery,
        IReadOnlyList<string> tokens,
        SpeechLanguageKind language,
        string normalizedCategory)
    {
        if (normalizedQuery.Length == 0 && normalizedCategory.Length == 0)
            return 0;

        var score = 0;
        var candidates = new[] { rule.TitleArabic, rule.TitleEnglish, rule.ExplanationArabic, rule.ExplanationEnglish, rule.Pattern }
            .Concat(rule.Keywords)
            .Concat(rule.Examples)
            .Select(SpeechTextNormalizer.NormalizeForLookup)
            .Where(value => value.Length > 0);
        foreach (var candidate in candidates)
        {
            if (candidate == normalizedQuery && candidate.Length > 0)
                score += 60;
            else if (normalizedQuery.Length > 0 && ContainsPhrase(candidate, normalizedQuery))
                score += 30;
            else if (normalizedQuery.Length > 0 && ContainsPhrase(normalizedQuery, candidate) && candidate.Length >= 3)
                score += 12;

            var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            score += tokens.Count(token => token.Length > 2 && candidateTokens.Contains(token, StringComparer.Ordinal)) * 2;
        }

        var ruleCategory = SpeechTextNormalizer.NormalizeForLookup(rule.Category);
        if (normalizedCategory.Length > 0)
            score += ruleCategory == normalizedCategory ? 40 : ruleCategory.Contains(normalizedCategory, StringComparison.Ordinal) ? 8 : 0;

        if (score > 0)
            score += LanguageBonus(rule.Language, rule.Dialect, language);
        return score;
    }

    private static int ScoreDialoguePattern(
        DialoguePattern pattern,
        string normalizedQuery,
        IReadOnlyList<string> tokens,
        DialogueIntent intent,
        SpeechLanguageKind language)
    {
        if (intent == DialogueIntent.Unknown && normalizedQuery.Length == 0)
            return 0;

        var score = 0;
        if (intent != DialogueIntent.Unknown &&
            Enum.TryParse<DialogueIntent>(pattern.Intent, true, out var candidateIntent))
            score += candidateIntent == intent ? 35 : -100;

        var candidates = pattern.Triggers.Concat(pattern.Utterances).Concat(pattern.Replies)
            .Append(pattern.SituationArabic).Append(pattern.SituationEnglish)
            .Select(SpeechTextNormalizer.NormalizeForLookup)
            .Where(value => value.Length > 0);
        foreach (var candidate in candidates)
        {
            if (normalizedQuery.Length == 0)
                continue;
            if (candidate == normalizedQuery)
                score += 75;
            else if (ContainsPhrase(candidate, normalizedQuery))
                score += 35;
            else if (ContainsPhrase(normalizedQuery, candidate) && candidate.Length > 3)
                score += 15;
            var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            score += tokens.Count(token => token.Length > 2 && candidateTokens.Contains(token, StringComparer.Ordinal)) * 3;
        }

        if (language == SpeechLanguageKind.EgyptianArabic && pattern.Dialect == "egyptian")
            score += 8;
        else if (language == SpeechLanguageKind.ArabicStandard && pattern.Dialect == "msa")
            score += 8;
        else if (language == SpeechLanguageKind.English && pattern.Language == "en")
            score += 8;
        else if (language is SpeechLanguageKind.ArabicStandard or SpeechLanguageKind.EgyptianArabic && pattern.Language == "ar")
            score += 2;

        return score;
    }

    private static int ScoreVerb(
        VerbParadigm verb,
        string normalizedQuery,
        IReadOnlyList<string> tokens,
        SpeechLanguageKind language)
    {
        var candidates = new[]
        {
            verb.Lemma, verb.Past, verb.Present, verb.Future, verb.Imperative, verb.VerbalNoun,
            verb.MeaningArabic, verb.MeaningEnglish
        }.Select(SpeechTextNormalizer.NormalizeForLookup).Where(value => value.Length > 0);
        var score = 0;
        foreach (var candidate in candidates)
        {
            if (candidate == normalizedQuery)
                score += candidate == SpeechTextNormalizer.NormalizeForLookup(verb.Lemma) ? 100 : 70;
            else if (ContainsPhrase(candidate, normalizedQuery))
                score += 35;
            else if (ContainsPhrase(normalizedQuery, candidate))
                score += 20;
            var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            score += tokens.Count(token => token.Length > 1 && candidateTokens.Contains(token, StringComparer.Ordinal)) * 8;
        }

        if (score > 0)
            score += LanguageBonus(verb.Language, verb.Dialect, language);
        return score;
    }

    private static int LanguageBonus(string itemLanguage, string dialect, SpeechLanguageKind language)
    {
        if (language == SpeechLanguageKind.English && itemLanguage == "en")
            return 8;
        if (language is SpeechLanguageKind.ArabicStandard or SpeechLanguageKind.EgyptianArabic && itemLanguage == "ar")
            return 5;
        if (language == SpeechLanguageKind.EgyptianArabic && dialect == "egyptian")
            return 12;
        if (language == SpeechLanguageKind.ArabicStandard && dialect == "msa")
            return 8;
        return 0;
    }

    private static bool ContainsPhrase(string source, string phrase)
    {
        if (source.Length == 0 || phrase.Length == 0)
            return false;

        var paddedSource = " " + source + " ";
        var paddedPhrase = " " + phrase + " ";
        return paddedSource.Contains(paddedPhrase, StringComparison.Ordinal);
    }

    private sealed class ReferenceDocument
    {
        public int SchemaVersion { get; set; }
        public string? ReferenceVersion { get; set; }
        public string? CoverageNote { get; set; }
        public List<LinguisticEntry>? Entries { get; set; }
        public List<ConversationTopic>? Topics { get; set; }
        public List<GrammarRule>? GrammarRules { get; set; }
        public List<DialoguePattern>? DialoguePatterns { get; set; }
        public List<VerbParadigm>? VerbParadigms { get; set; }
    }
}
