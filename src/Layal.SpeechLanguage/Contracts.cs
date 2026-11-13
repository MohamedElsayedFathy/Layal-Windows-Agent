namespace Layal.SpeechLanguage;

public enum SpeechLanguageKind
{
    Unknown,
    ArabicStandard,
    EgyptianArabic,
    English,
    Mixed
}

public enum DialogueIntent
{
    Unknown,
    Greeting,
    Farewell,
    Gratitude,
    Apology,
    Question,
    Command,
    GeneralConversation
}

public enum DialogueSpeaker
{
    User,
    Assistant
}

public sealed record LanguageSegment(
    string Text,
    SpeechLanguageKind Language,
    int StartIndex,
    int Length);

public sealed record LanguageDetectionResult(
    SpeechLanguageKind PrimaryLanguage,
    double Confidence,
    IReadOnlyList<LanguageSegment> Segments,
    int ArabicLetterCount,
    int LatinLetterCount);

public sealed class LinguisticEntry
{
    public string Id { get; init; } = string.Empty;
    public string Term { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Dialect { get; init; } = string.Empty;
    public string MeaningArabic { get; init; } = string.Empty;
    public string MeaningEnglish { get; init; } = string.Empty;
    public string Pronunciation { get; init; } = string.Empty;
    public string Transliteration { get; init; } = string.Empty;
    public string PartOfSpeech { get; init; } = string.Empty;
    public string Register { get; init; } = string.Empty;
    public string UsageNotesArabic { get; init; } = string.Empty;
    public string UsageNotesEnglish { get; init; } = string.Empty;
    public List<string> Synonyms { get; init; } = new();
    public List<string> Antonyms { get; init; } = new();
    public List<string> Forms { get; init; } = new();
    public List<string> Examples { get; init; } = new();
    public List<string> Topics { get; init; } = new();
    public bool IsDialectMarker { get; init; }
}

public sealed class ConversationTopic
{
    public string Id { get; init; } = string.Empty;
    public string NameArabic { get; init; } = string.Empty;
    public string NameEnglish { get; init; } = string.Empty;
    public string DescriptionArabic { get; init; } = string.Empty;
    public string DescriptionEnglish { get; init; } = string.Empty;
    public List<string> Keywords { get; init; } = new();
    public List<string> PromptsArabicFusha { get; init; } = new();
    public List<string> PromptsEgyptian { get; init; } = new();
    public List<string> PromptsEnglish { get; init; } = new();
}

public sealed record SpeechLanguageAnalysis(
    string OriginalText,
    string NormalizedText,
    LanguageDetectionResult Language,
    DialogueIntent Intent,
    IReadOnlyList<LinguisticEntry> MatchingEntries,
    IReadOnlyList<ConversationTopic> MatchingTopics)
{
    public string GetLanguageLabel(bool arabic) => Language.PrimaryLanguage switch
    {
        SpeechLanguageKind.ArabicStandard => arabic ? "العربية الفصحى" : "Modern Standard Arabic",
        SpeechLanguageKind.EgyptianArabic => arabic ? "اللهجة المصرية" : "Egyptian Arabic",
        SpeechLanguageKind.English => arabic ? "الإنجليزية" : "English",
        SpeechLanguageKind.Mixed => arabic ? "عربي وإنجليزي مختلط" : "Mixed Arabic and English",
        _ => arabic ? "لغة غير محددة" : "Undetermined language"
    };
}

public sealed record DialogueTurn(
    DialogueSpeaker Speaker,
    string Text,
    SpeechLanguageKind Language,
    DateTimeOffset Timestamp,
    string? TopicId);

public sealed record SpeechSynthesisOptions(
    string? OutputDeviceId = null,
    string? DeliveryStyleId = null);

public sealed record SpeechRecognitionResult(
    string Text,
    SpeechLanguageAnalysis Analysis);
