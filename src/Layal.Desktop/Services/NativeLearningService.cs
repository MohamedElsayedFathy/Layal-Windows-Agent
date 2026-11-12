using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Layal.Services;

/// <summary>
/// Local, user-directed learning for aliases and bounded execution outcomes.
/// This stores explicit rules and experience signals; it does not claim to train model weights.
/// </summary>
public sealed class NativeLearningService
{
    public const int MaximumRules = 100;
    public const int MaximumExperiences = 500;

    private static readonly Regex ArabicTeachingPattern = new(
        @"^\s*(?:لما|عندما)\s+(?:[اأإآ]?قول)\s+[""«]?(?<trigger>.+?)[""»]?\s+(?:اعملي|نفذي|قومي\s+بتنفيذ)\s+(?<command>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EnglishTeachingPattern = new(
        @"^\s*when\s+i\s+say\s+[""'](?<trigger>.+?)[""']\s*,?\s*(?:then\s+)?(?:do|run|execute)\s+(?<command>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ProtectedJsonStore<NativeLearningState> _store;
    private readonly object _sync = new();

    public NativeLearningService(string path) =>
        _store = new ProtectedJsonStore<NativeLearningState>(path);

    public bool TryTeachFromUtterance(string utterance, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(utterance) || utterance.Length > 500)
            return false;

        var match = ArabicTeachingPattern.Match(utterance);
        if (!match.Success)
            match = EnglishTeachingPattern.Match(utterance);
        if (!match.Success)
            return false;

        var trigger = match.Groups["trigger"].Value.Trim().Trim('"', '\'', '«', '»');
        var command = match.Groups["command"].Value.Trim().Trim('"', '\'', '«', '»');
        TeachAlias(trigger, command);
        message = $"تم حفظ الاختصار المحلي «{trigger}». سأستخدمه فقط عندما تطابق عبارتك هذا الاختصار، وسأنفذ الهدف إذا كان من الأوامر التي تدعمها نواة ليال الحالية.";
        return true;
    }

    public void TeachAlias(string trigger, string command)
    {
        var safeTrigger = NormalizeRuleText(trigger, "trigger", 2, 80);
        var safeCommand = NormalizeRuleText(command, "command", 2, 250);
        var normalizedTrigger = NativeTextNormalizer.Normalize(safeTrigger);
        if (normalizedTrigger.Length < 2)
            throw new ArgumentException("The trigger must contain at least two meaningful characters.", nameof(trigger));

        lock (_sync)
        {
            var state = LoadState();
            var existing = state.Rules.FirstOrDefault(rule =>
                string.Equals(rule.NormalizedTrigger, normalizedTrigger, StringComparison.Ordinal));
            if (existing is null)
            {
                if (state.Rules.Count >= MaximumRules)
                    throw new InvalidOperationException($"Layal can store at most {MaximumRules} learned aliases.");
                state.Rules.Add(new NativeLearnedRule
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Trigger = safeTrigger,
                    NormalizedTrigger = normalizedTrigger,
                    Command = safeCommand,
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                });
            }
            else
            {
                existing.Trigger = safeTrigger;
                existing.Command = safeCommand;
                existing.UpdatedAt = DateTimeOffset.Now;
            }

            _store.Save(state);
        }
    }

    public bool TryResolveAlias(string input, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var normalized = NativeTextNormalizer.Normalize(input);
        lock (_sync)
        {
            var match = LoadState().Rules
                .OrderByDescending(rule => rule.NormalizedTrigger.Length)
                .FirstOrDefault(rule => string.Equals(
                    rule.NormalizedTrigger, normalized, StringComparison.Ordinal));
            if (match is null)
                return false;
            command = match.Command;
            return true;
        }
    }

    public IReadOnlyList<NativeLearnedRule> GetRules()
    {
        lock (_sync)
            return LoadState().Rules.Select(Clone).ToList();
    }

    /// <summary>Stores a compact outcome signal without retaining prompts, file contents, or secrets.</summary>
    public void RecordExperience(string intent, string outcome, bool verified)
    {
        if (string.IsNullOrWhiteSpace(intent) || string.IsNullOrWhiteSpace(outcome))
            return;

        lock (_sync)
        {
            var state = LoadState();
            state.Experiences.Add(new NativeExperience
            {
                Intent = intent.Length <= 80 ? intent : intent[..80],
                Outcome = outcome.Length <= 40 ? outcome : outcome[..40],
                Verified = verified,
                RecordedAt = DateTimeOffset.Now
            });
            if (state.Experiences.Count > MaximumExperiences)
                state.Experiences.RemoveRange(0, state.Experiences.Count - MaximumExperiences);
            _store.Save(state);
        }
    }

    public IReadOnlyList<NativeExperience> GetRecentExperiences(int maximum = 50)
    {
        var count = Math.Clamp(maximum, 1, 100);
        lock (_sync)
            return LoadState().Experiences
                .OrderByDescending(item => item.RecordedAt)
                .Take(count)
                .Select(item => new NativeExperience
                {
                    Intent = item.Intent,
                    Outcome = item.Outcome,
                    Verified = item.Verified,
                    RecordedAt = item.RecordedAt
                })
                .ToList();
    }

    private NativeLearningState LoadState()
    {
        var state = _store.Load(new NativeLearningState());
        state.Rules ??= new List<NativeLearnedRule>();
        state.Experiences ??= new List<NativeExperience>();
        state.Rules = state.Rules
            .Where(rule => rule is not null &&
                !string.IsNullOrWhiteSpace(rule.NormalizedTrigger) &&
                !string.IsNullOrWhiteSpace(rule.Command))
            .Take(MaximumRules)
            .ToList();
        state.Experiences = state.Experiences
            .Where(item => item is not null &&
                !string.IsNullOrWhiteSpace(item.Intent) &&
                !string.IsNullOrWhiteSpace(item.Outcome))
            .TakeLast(MaximumExperiences)
            .ToList();
        return state;
    }

    private static string NormalizeRuleText(string value, string parameter, int minimum, int maximum)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length < minimum || normalized.Length > maximum ||
            normalized.Any(char.IsControl))
            throw new ArgumentException($"A learned {parameter} must contain {minimum}–{maximum} printable characters.", parameter);
        return normalized;
    }

    private static NativeLearnedRule Clone(NativeLearnedRule rule) => new()
    {
        Id = rule.Id,
        Trigger = rule.Trigger,
        NormalizedTrigger = rule.NormalizedTrigger,
        Command = rule.Command,
        CreatedAt = rule.CreatedAt,
        UpdatedAt = rule.UpdatedAt
    };
}

public sealed class NativeLearningState
{
    public List<NativeLearnedRule> Rules { get; set; } = new();
    public List<NativeExperience> Experiences { get; set; } = new();
}

public sealed class NativeLearnedRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Trigger { get; set; } = string.Empty;
    public string NormalizedTrigger { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class NativeExperience
{
    public string Intent { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.Now;
}

public static class NativeTextNormalizer
{
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var source = input.Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (character == '\u0640')
                continue;
            var category = char.GetUnicodeCategory(character);
            if (category is System.Globalization.UnicodeCategory.NonSpacingMark or
                System.Globalization.UnicodeCategory.SpacingCombiningMark or
                System.Globalization.UnicodeCategory.EnclosingMark)
                continue;

            var normalized = character switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ة' => 'ه',
                _ => char.ToLowerInvariant(character)
            };

            if (char.IsLetterOrDigit(normalized) || normalized is '/' or '\\' or '.' or '-' or '_' or ':')
                result.Append(normalized);
            else
                result.Append(' ');
        }

        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
    }
}
