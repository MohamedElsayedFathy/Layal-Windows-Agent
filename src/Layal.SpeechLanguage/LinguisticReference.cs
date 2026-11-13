using System.Reflection;
using System.Text.Json;

namespace Layal.SpeechLanguage;

/// <summary>
/// Versioned, searchable language and dialogue-topic reference. The bundled data is a curated
/// starter corpus and is deliberately not represented as a complete dictionary or grammar.
/// </summary>
public sealed class LinguisticReference
{
    private const string ResourceName = "Layal.SpeechLanguage.Data.speech-language-reference.json";
    private readonly List<LinguisticEntry> _entries;
    private readonly List<ConversationTopic> _topics;

    public int SchemaVersion { get; }
    public string ReferenceVersion { get; }
    public string CoverageNote { get; }
    public IReadOnlyList<LinguisticEntry> Entries => _entries.AsReadOnly();
    public IReadOnlyList<ConversationTopic> Topics => _topics.AsReadOnly();

    public LinguisticReference(
        IEnumerable<LinguisticEntry> entries,
        IEnumerable<ConversationTopic> topics,
        int schemaVersion = 1,
        string referenceVersion = "1.0.0",
        string coverageNote = "Curated starter reference; extend with reviewed entries.")
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(topics);
        _entries = entries
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Term))
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        _topics = topics
            .Where(topic => topic is not null && !string.IsNullOrWhiteSpace(topic.Id))
            .GroupBy(topic => topic.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        SchemaVersion = schemaVersion;
        ReferenceVersion = string.IsNullOrWhiteSpace(referenceVersion) ? "1.0.0" : referenceVersion;
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
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported linguistic reference schema version: {document.SchemaVersion}.");

        return new LinguisticReference(
            document.Entries ?? new List<LinguisticEntry>(),
            document.Topics ?? new List<ConversationTopic>(),
            document.SchemaVersion,
            document.ReferenceVersion ?? "1.0.0",
            document.CoverageNote ?? "Curated starter reference.");
    }

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
            .Select(topic => new { Topic = topic, Score = ScoreTopic(topic, normalizedQuery, tokens) })
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
        else if (normalizedQuery.Contains(term, StringComparison.Ordinal))
            score += 45;
        else if (term.Contains(normalizedQuery, StringComparison.Ordinal))
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
            else if (queryTokens.Any(token => token.Length > 1 && alias.Contains(token, StringComparison.Ordinal)))
                score += 8;
        }

        var termTokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        score += queryTokens.Count(token => token.Length > 1 && termTokens.Contains(token, StringComparer.Ordinal)) * 12;
        if (score == 0)
            return 0;

        if (language == SpeechLanguageKind.English && entry.Language == "en")
            score += 5;
        else if (language is SpeechLanguageKind.ArabicStandard or SpeechLanguageKind.EgyptianArabic && entry.Language == "ar")
            score += 5;
        if (language == SpeechLanguageKind.EgyptianArabic && entry.Dialect == "egyptian")
            score += 12;
        if (language == SpeechLanguageKind.ArabicStandard && entry.Dialect == "msa")
            score += 8;
        return score;
    }

    private static int ScoreTopic(ConversationTopic topic, string normalizedQuery, IReadOnlyList<string> queryTokens)
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
            else if (candidate.Contains(normalizedQuery, StringComparison.Ordinal))
                score += 30;
            else if (normalizedQuery.Contains(candidate, StringComparison.Ordinal) && candidate.Length >= 3)
                score += 15;
            var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            score += queryTokens.Count(token => token.Length > 2 && candidateTokens.Contains(token, StringComparer.Ordinal)) * 3;
        }
        return score;
    }

    private sealed class ReferenceDocument
    {
        public int SchemaVersion { get; set; }
        public string? ReferenceVersion { get; set; }
        public string? CoverageNote { get; set; }
        public List<LinguisticEntry>? Entries { get; set; }
        public List<ConversationTopic>? Topics { get; set; }
    }
}
