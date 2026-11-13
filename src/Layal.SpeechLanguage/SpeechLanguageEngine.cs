using System.Text;

namespace Layal.SpeechLanguage;

public sealed class SpeechLanguageEngine
{
    public const int MaximumTranscriptCharacters = 16_000;
    public LinguisticReference Reference { get; }

    public SpeechLanguageEngine(LinguisticReference reference) =>
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));

    public static SpeechLanguageEngine CreateDefault() => new(LinguisticReference.LoadDefault());

    public SpeechLanguageAnalysis Analyze(string? text)
    {
        var original = text ?? string.Empty;
        var normalized = SpeechTextNormalizer.NormalizeTranscript(original);
        if (normalized.Length > MaximumTranscriptCharacters)
            throw new ArgumentOutOfRangeException(nameof(text), $"Transcripts cannot exceed {MaximumTranscriptCharacters} characters.");

        var language = LanguageDetector.Detect(normalized, Reference);
        var intent = Reference.DetectIntent(normalized);
        if (intent == DialogueIntent.Unknown)
            intent = DialogueIntentDetector.Detect(normalized);

        return new SpeechLanguageAnalysis(
            original,
            normalized,
            language,
            intent,
            Reference.Find(normalized, language.PrimaryLanguage, 6),
            Reference.FindTopics(normalized, language.PrimaryLanguage, 4))
        {
            MatchingGrammarRules = Reference.FindGrammar(normalized, language.PrimaryLanguage, maximumResults: 3),
            MatchingDialoguePatterns = Reference.FindDialoguePatterns(
                normalized, intent, language.PrimaryLanguage, maximumResults: 3)
        };
    }
}

internal static class LanguageDetector
{
    private enum ScriptKind { None, Arabic, Latin }

    public static LanguageDetectionResult Detect(string text, LinguisticReference reference)
    {
        if (text.Length == 0)
            return new LanguageDetectionResult(SpeechLanguageKind.Unknown, 0, Array.Empty<LanguageSegment>(), 0, 0);

        var arabicCount = 0;
        var latinCount = 0;
        foreach (var character in text)
        {
            if (IsArabicLetter(character)) arabicCount++;
            else if (IsLatinLetter(character)) latinCount++;
        }

        var segments = BuildSegments(text, reference);
        SpeechLanguageKind primary;
        double confidence;
        if (arabicCount > 0 && latinCount > 0)
        {
            primary = SpeechLanguageKind.Mixed;
            confidence = 0.92;
        }
        else if (arabicCount > 0)
        {
            var egyptian = reference.IsEgyptianText(text);
            primary = egyptian ? SpeechLanguageKind.EgyptianArabic : SpeechLanguageKind.ArabicStandard;
            confidence = egyptian ? 0.82 : 0.94;
        }
        else if (latinCount > 0)
        {
            primary = SpeechLanguageKind.English;
            confidence = 0.94;
        }
        else
        {
            primary = SpeechLanguageKind.Unknown;
            confidence = 0;
        }

        return new LanguageDetectionResult(primary, confidence, segments, arabicCount, latinCount);
    }

    private static IReadOnlyList<LanguageSegment> BuildSegments(string text, LinguisticReference reference)
    {
        var segments = new List<LanguageSegment>();
        var current = new StringBuilder();
        var currentScript = ScriptKind.None;
        var startIndex = -1;

        void Flush(int endIndex)
        {
            var value = current.ToString().Trim();
            if (value.Length > 0 && startIndex >= 0)
            {
                var language = currentScript == ScriptKind.Arabic
                    ? (reference.IsEgyptianText(value) ? SpeechLanguageKind.EgyptianArabic : SpeechLanguageKind.ArabicStandard)
                    : SpeechLanguageKind.English;
                segments.Add(new LanguageSegment(value, language, startIndex, endIndex - startIndex));
            }
            current.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var script = IsArabicLetter(character)
                ? ScriptKind.Arabic
                : IsLatinLetter(character) ? ScriptKind.Latin : ScriptKind.None;

            if (script == ScriptKind.None)
            {
                if (currentScript != ScriptKind.None)
                    current.Append(character);
                continue;
            }

            if (currentScript == ScriptKind.None)
            {
                currentScript = script;
                startIndex = index;
                current.Append(character);
                continue;
            }

            if (currentScript != script)
            {
                Flush(index);
                currentScript = script;
                startIndex = index;
                current.Append(character);
            }
            else
            {
                current.Append(character);
            }
        }

        if (currentScript != ScriptKind.None)
            Flush(text.Length);
        return segments;
    }

    private static bool IsArabicLetter(char value) =>
        value is >= '\u0621' and <= '\u063A' or
            >= '\u0641' and <= '\u064A' or
            >= '\u066E' and <= '\u06D3' or
            >= '\u06FA' and <= '\u06FC' or
            >= '\u0750' and <= '\u077F' or
            >= '\u08A0' and <= '\u08FF';

    private static bool IsLatinLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '\u00C0' and <= '\u024F';
}

internal static class DialogueIntentDetector
{
    private static readonly string[] Farewells =
    [
        "مع السلامه", "الى اللقاء", "اشوفك", "سلام", "goodbye", "bye", "see you"
    ];
    private static readonly string[] Gratitude =
    [
        "شكرا", "متشكر", "تسلم", "thank you", "thanks", "much appreciated"
    ];
    private static readonly string[] Apologies =
    [
        "اسف", "اسفه", "اعتذر", "sorry", "my apologies", "excuse me"
    ];
    private static readonly string[] QuestionMarkers =
    [
        "ايه", "ماذا", "لماذا", "ليه", "ازاي", "كيف", "فين", "اين", "امتى", "متى",
        "هل", "مين", "من", "what", "why", "when", "where", "how", "who", "which"
    ];
    private static readonly string[] CommandStarts =
    [
        "افتح", "افتحي", "اعمل", "اعملي", "شغل", "شغلي", "انشئ", "اكتب", "اكتبي",
        "نفذ", "نفذي", "ابحث", "دور", "اقرا", "اقرأ",
        "open", "create", "make", "run", "execute", "search", "read"
    ];

    public static DialogueIntent Detect(string text)
    {
        var value = SpeechTextNormalizer.NormalizeForLookup(text);
        if (value.Length == 0) return DialogueIntent.Unknown;

        if (MatchesPhrase(value, Farewells)) return DialogueIntent.Farewell;
        if (MatchesPhrase(value, Gratitude)) return DialogueIntent.Gratitude;
        if (MatchesPhrase(value, Apologies)) return DialogueIntent.Apology;
        if (text.Contains('?') || text.Contains('؟') || MatchesPhrase(value, QuestionMarkers))
            return DialogueIntent.Question;
        if (CommandStarts.Any(start => StartsWithPhrase(value, SpeechTextNormalizer.NormalizeForLookup(start))))
            return DialogueIntent.Command;
        return DialogueIntent.GeneralConversation;
    }

    private static bool MatchesPhrase(string source, IEnumerable<string> values) =>
        values.Any(value => StartsWithPhrase(source, SpeechTextNormalizer.NormalizeForLookup(value)) ||
                            ContainsPhrase(source, SpeechTextNormalizer.NormalizeForLookup(value)));

    private static bool StartsWithPhrase(string source, string phrase) =>
        phrase.Length > 0 && (source == phrase || source.StartsWith(phrase + " ", StringComparison.Ordinal));

    private static bool ContainsPhrase(string source, string phrase) =>
        phrase.Length > 0 && (" " + source + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);
}
