using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Layal.Services;

public sealed class LanguageReferenceCatalog
{
    public int SchemaVersion { get; set; }
    public string CatalogVersion { get; set; } = string.Empty;
    public List<LanguageTopic> Topics { get; set; } = new();
    public List<LanguageConcept> Concepts { get; set; } = new();
    public List<LanguageDialogue> Dialogues { get; set; } = new();
}

public sealed class LanguageTopic
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string DescriptionAr { get; set; } = string.Empty;
    public string DescriptionEn { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class LanguageConcept
{
    public string Id { get; set; } = string.Empty;
    public string TopicId { get; set; } = string.Empty;
    public string MeaningAr { get; set; } = string.Empty;
    public string MeaningEn { get; set; } = string.Empty;
    public string UsageNotesAr { get; set; } = string.Empty;
    public string UsageNotesEn { get; set; } = string.Empty;
    public List<LanguageForm> Forms { get; set; } = new();
    public string SourceId { get; set; } = "layal-curated";
    public string License { get; set; } = "project-internal; review required";
}

public sealed class LanguageForm
{
    public string Language { get; set; } = string.Empty;
    public string Variety { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Transliteration { get; set; } = string.Empty;
    public string Pronunciation { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;
    public string Register { get; set; } = "conversational";
    public List<string> Variants { get; set; } = new();
    public List<LanguageExample> Examples { get; set; } = new();
}

public sealed class LanguageExample
{
    public string Text { get; set; } = string.Empty;
    public string TranslationAr { get; set; } = string.Empty;
    public string TranslationEn { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
}

public sealed class LanguageDialogue
{
    public string Id { get; set; } = string.Empty;
    public string TopicId { get; set; } = string.Empty;
    public string ScenarioAr { get; set; } = string.Empty;
    public string ScenarioEn { get; set; } = string.Empty;
    public List<LanguageDialogueVersion> Versions { get; set; } = new();
}

public sealed class LanguageDialogueVersion
{
    public string Language { get; set; } = string.Empty;
    public string Variety { get; set; } = string.Empty;
    public List<LanguageDialogueTurn> Turns { get; set; } = new();
}

public sealed class LanguageDialogueTurn
{
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed record LanguageReferenceMatch(LanguageConcept Concept, int Score, string MatchReason);

/// <summary>
/// Offline, provenance-aware linguistic reference for Egyptian Arabic, MSA, English and code-switching.
/// The built-in catalogue is deliberately curated and finite; it is not presented as a complete dictionary.
/// </summary>
public sealed class LanguageReferenceService
{
    public const string BuiltInResourceName = "Layal.Resources.LanguageReference.reference.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    private readonly LanguageReferenceCatalog _catalog;
    private readonly Dictionary<string, LanguageTopic> _topics;

    public LanguageReferenceCatalog Catalog => _catalog;

    public LanguageReferenceService(LanguageReferenceCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ValidateCatalog(_catalog);
        _topics = _catalog.Topics.ToDictionary(topic => topic.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static LanguageReferenceService LoadBuiltIn()
    {
        using var stream = typeof(LanguageReferenceService).Assembly.GetManifestResourceStream(BuiltInResourceName)
            ?? throw new InvalidOperationException($"Embedded language reference resource '{BuiltInResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    public static LanguageReferenceService FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Language reference JSON is empty.", nameof(json));
        var catalog = JsonSerializer.Deserialize<LanguageReferenceCatalog>(json, JsonOptions)
            ?? throw new InvalidDataException("Language reference JSON did not contain a catalogue.");
        return new LanguageReferenceService(catalog);
    }

    public IReadOnlyList<LanguageReferenceMatch> Search(
        string query,
        string? language = null,
        string? topicId = null,
        int maximumResults = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || maximumResults <= 0)
            return Array.Empty<LanguageReferenceMatch>();

        var normalizedQuery = LanguageTextNormalizer.Normalize(query);
        if (normalizedQuery.Length == 0)
            return Array.Empty<LanguageReferenceMatch>();

        var queryTokens = LanguageTextNormalizer.Tokenize(normalizedQuery);
        var requestedLanguage = NormalizeLanguageFilter(language);
        var matches = new List<LanguageReferenceMatch>();

        foreach (var concept in _catalog.Concepts)
        {
            if (!string.IsNullOrWhiteSpace(topicId) &&
                !string.Equals(concept.TopicId, topicId, StringComparison.OrdinalIgnoreCase))
                continue;

            var bestScore = 0;
            var bestReason = string.Empty;
            foreach (var form in concept.Forms)
            {
                if (requestedLanguage is not null &&
                    !string.Equals(form.Language, requestedLanguage, StringComparison.OrdinalIgnoreCase))
                    continue;

                ScoreForm(form, normalizedQuery, queryTokens, ref bestScore, ref bestReason);
            }

            var meaningAr = LanguageTextNormalizer.Normalize(concept.MeaningAr);
            var meaningEn = LanguageTextNormalizer.Normalize(concept.MeaningEn);
            if (requestedLanguage is null or "ar" or "ar-EG")
                ScoreText(meaningAr, normalizedQuery, queryTokens, 5, "definition", ref bestScore, ref bestReason);
            if (requestedLanguage is null or "en")
                ScoreText(meaningEn, normalizedQuery, queryTokens, 5, "definition", ref bestScore, ref bestReason);

            if (bestScore > 0)
                matches.Add(new LanguageReferenceMatch(concept, bestScore, bestReason));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Concept.Id, StringComparer.Ordinal)
            .Take(maximumResults)
            .ToArray();
    }

    public LanguageConcept? GetConcept(string id) =>
        _catalog.Concepts.FirstOrDefault(concept => string.Equals(concept.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LanguageTopic> GetTopics(string? parentId = null) =>
        _catalog.Topics
            .Where(topic => string.Equals(topic.ParentId ?? string.Empty, parentId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .OrderBy(topic => topic.SortOrder)
            .ThenBy(topic => topic.Id, StringComparer.Ordinal)
            .ToArray();

    public LanguageTopic? GetTopic(string id) =>
        _topics.TryGetValue(id, out var topic) ? topic : null;

    public IReadOnlyList<LanguageDialogue> SearchDialogues(
        string query,
        string? language = null,
        string? topicId = null,
        int maximumResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || maximumResults <= 0)
            return Array.Empty<LanguageDialogue>();

        var normalized = LanguageTextNormalizer.Normalize(query);
        var tokens = LanguageTextNormalizer.Tokenize(normalized);
        var requestedLanguage = NormalizeLanguageFilter(language);

        return _catalog.Dialogues
            .Where(dialogue => string.IsNullOrWhiteSpace(topicId) ||
                string.Equals(dialogue.TopicId, topicId, StringComparison.OrdinalIgnoreCase))
            .Select(dialogue => new
            {
                Dialogue = dialogue,
                Score = ScoreTextValue(LanguageTextNormalizer.Normalize(dialogue.ScenarioAr + " " + dialogue.ScenarioEn), normalized, tokens) +
                    dialogue.Versions
                    .Where(version => requestedLanguage is null ||
                        string.Equals(version.Language, requestedLanguage, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(version => version.Turns)
                    .Select(turn => LanguageTextNormalizer.Normalize(turn.Text))
                    .Sum(text => ScoreTextValue(text, normalized, tokens))
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Dialogue.Id, StringComparer.Ordinal)
            .Take(maximumResults)
            .Select(item => item.Dialogue)
            .ToArray();
    }

    private static void ScoreForm(
        LanguageForm form,
        string query,
        IReadOnlyList<string> tokens,
        ref int bestScore,
        ref string bestReason)
    {
        ScoreText(LanguageTextNormalizer.Normalize(form.Text), query, tokens, 120, "term", ref bestScore, ref bestReason);
        ScoreText(LanguageTextNormalizer.Normalize(form.Transliteration), query, tokens, 70, "transliteration", ref bestScore, ref bestReason);
        foreach (var variant in form.Variants)
            ScoreText(LanguageTextNormalizer.Normalize(variant), query, tokens, 100, "variant", ref bestScore, ref bestReason);
        foreach (var example in form.Examples)
            ScoreText(LanguageTextNormalizer.Normalize(example.Text), query, tokens, 15, "example", ref bestScore, ref bestReason);
    }

    private static void ScoreText(
        string candidate,
        string query,
        IReadOnlyList<string> tokens,
        int exactPhraseScore,
        string reason,
        ref int bestScore,
        ref string bestReason)
    {
        var score = ScoreTextValue(candidate, query, tokens, exactPhraseScore);
        if (score > bestScore)
        {
            bestScore = score;
            bestReason = reason;
        }
    }

    private static int ScoreTextValue(
        string candidate,
        string query,
        IReadOnlyList<string> tokens,
        int exactPhraseScore = 35)
    {
        if (candidate.Length == 0)
            return 0;
        if (candidate == query)
            return exactPhraseScore;
        if (candidate.StartsWith(query + " ", StringComparison.Ordinal) ||
            candidate.EndsWith(" " + query, StringComparison.Ordinal) ||
            candidate.Contains(" " + query + " ", StringComparison.Ordinal))
            return Math.Max(1, exactPhraseScore - 10);

        var candidateTokens = LanguageTextNormalizer.Tokenize(candidate);
        if (candidateTokens.Count == 0)
            return 0;
        var overlap = tokens.Count(token => candidateTokens.Contains(token, StringComparer.Ordinal));
        if (overlap == 0)
            return 0;
        var coverage = (double)overlap / Math.Max(1, tokens.Count);
        var precision = (double)overlap / Math.Max(1, candidateTokens.Count);
        return (int)Math.Round(8 + (coverage * 16) + (precision * 8), MidpointRounding.AwayFromZero);
    }

    private static string? NormalizeLanguageFilter(string? language)
    {
        var value = language?.Trim().ToLowerInvariant();
        return value switch
        {
            "ar-eg" or "arz" or "egyptian" or "egyptian-arabic" => "ar-EG",
            "ar" or "msa" or "fusha" or "modern-standard-arabic" => "ar",
            "en" or "en-us" or "english" => "en",
            "mixed" or "code-switching" => "mixed",
            _ => null
        };
    }

    private static void ValidateCatalog(LanguageReferenceCatalog catalog)
    {
        if (catalog.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported language reference schema version: {catalog.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(catalog.CatalogVersion))
            throw new InvalidDataException("Language reference catalog version is required.");

        var topicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var topic in catalog.Topics)
        {
            if (string.IsNullOrWhiteSpace(topic.Id) || !topicIds.Add(topic.Id))
                throw new InvalidDataException("Language reference topic IDs must be unique and non-empty.");
            if (string.IsNullOrWhiteSpace(topic.NameAr) || string.IsNullOrWhiteSpace(topic.NameEn))
                throw new InvalidDataException($"Topic '{topic.Id}' must have Arabic and English names.");
        }

        foreach (var topic in catalog.Topics)
            if (!string.IsNullOrWhiteSpace(topic.ParentId) && !topicIds.Contains(topic.ParentId))
                throw new InvalidDataException($"Topic '{topic.Id}' references missing parent '{topic.ParentId}'.");

        var conceptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var concept in catalog.Concepts)
        {
            if (string.IsNullOrWhiteSpace(concept.Id) || !conceptIds.Add(concept.Id))
                throw new InvalidDataException("Language reference concept IDs must be unique and non-empty.");
            if (!topicIds.Contains(concept.TopicId))
                throw new InvalidDataException($"Concept '{concept.Id}' references missing topic '{concept.TopicId}'.");
            if (string.IsNullOrWhiteSpace(concept.MeaningAr) || string.IsNullOrWhiteSpace(concept.MeaningEn))
                throw new InvalidDataException($"Concept '{concept.Id}' must include Arabic and English meanings.");
            if (concept.Forms.Count == 0)
                throw new InvalidDataException($"Concept '{concept.Id}' has no language forms.");
            foreach (var form in concept.Forms)
            {
                if (form.Language is not ("ar-EG" or "ar" or "en" or "mixed"))
                    throw new InvalidDataException($"Concept '{concept.Id}' contains unsupported language '{form.Language}'.");
                if (string.IsNullOrWhiteSpace(form.Text))
                    throw new InvalidDataException($"Concept '{concept.Id}' contains an empty language form.");
            }
        }

        var dialogueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dialogue in catalog.Dialogues)
        {
            if (string.IsNullOrWhiteSpace(dialogue.Id) || !dialogueIds.Add(dialogue.Id))
                throw new InvalidDataException("Dialogue IDs must be unique and non-empty.");
            if (!topicIds.Contains(dialogue.TopicId))
                throw new InvalidDataException($"Dialogue '{dialogue.Id}' references missing topic '{dialogue.TopicId}'.");
            if (dialogue.Versions.Count == 0 || dialogue.Versions.Any(version => version.Turns.Count == 0))
                throw new InvalidDataException($"Dialogue '{dialogue.Id}' must contain at least one non-empty version.");
        }
    }
}

/// <summary>Conservative normalization for lookup only; original display forms are always preserved.</summary>
public static class LanguageTextNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var source = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var result = new System.Text.StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (character == '\u0640' || IsDirectionalControl(character))
                continue;

            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;

            var normalized = character switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' or 'ی' => 'ي',
                'ک' => 'ك',
                _ => character
            };

            if (char.IsLetterOrDigit(normalized))
                result.Append(normalized);
            else if (result.Length > 0 && result[^1] != ' ')
                result.Append(' ');
        }

        return result.ToString().Trim();
    }

    public static IReadOnlyList<string> Tokenize(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2) continue;
            result.Add(token);
            // Egyptian/MSA searches often differ only by the Arabic definite article.
            // Preserve the original token and add a conservative article-stripped lookup key.
            if (token.StartsWith("ال", StringComparison.Ordinal) && token.Length > 4)
                result.Add(token[2..]);
        }
        return result.ToArray();
    }

    private static bool IsDirectionalControl(char character) =>
        character is '\u061C' or '\u200E' or '\u200F' or '\u202A' or '\u202B' or
            '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069';
}
