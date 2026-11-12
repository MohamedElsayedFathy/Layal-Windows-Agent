using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Layal.Services;

public enum NativeIntent
{
    Unknown, Greeting, Identity, Capabilities, CurrentTime, CurrentDate, SystemInfo,
    WorkspaceInfo, ListWorkspaceFiles, ReadTextFile, CreateTextFile, SearchWeb,
    OpenUrl, LaunchProgram, BuildProject, TestProject, SaveMemory, TeachAlias
}

public sealed record NativeCommand(
    NativeIntent Intent,
    string OriginalText,
    string? Path = null,
    string? Content = null,
    string? Query = null,
    string? Directory = null,
    string? Url = null,
    string? ProgramId = null,
    string? Title = null,
    bool WasLearnedAlias = false);

/// <summary>Bilingual deterministic intent parser. It executes no text as code and never calls a model.</summary>
public sealed class NativeLanguageParser
{
    private static readonly Regex CreateFilePattern = new(
        @"^\s*(?:[اأإآ]نشئ|[اأإآ]نشئي|اعملي|اكتب|اكتبي|create|make)\s+(?:لي\s+)?(?:ملف|file)\s+(?<path>[^\s]+)\s+(?:بمحتوى|محتواه|واكتب\s+فيه|with\s+content)\s+(?<content>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReadFilePattern = new(
        @"^\s*(?:اقرا|اقرأ|اقرئي|read)\s+(?:(?:الملف|ملف|file)\s+)?(?<path>[^\s]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SearchPattern = new(
        @"^\s*(?:ابحث|ابحثي|دور|دوري)\s+(?:عن|على)\s+(?<query>.+?)\s*$|^\s*search\s+for\s+(?<query>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OpenUrlPattern = new(
        @"^\s*(?:افتح|افتحي|open)\s+(?<url>https?://[^\s]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ListFilesPattern = new(
        @"^\s*(?:اعرض\s+الملفات|اعرضي\s+الملفات|قائمة\s+الملفات|قائمه\s+الملفات|list\s+files|show\s+files)(?:\s+(?:في|داخل|بداخل|under)\s+(?<directory>[A-Za-z0-9_.\\/-]+))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BuildPattern = new(
        @"^\s*(?:ابني|ابنيلي|build)\s+(?<path>[^\s]+\.(?:sln|csproj))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestPattern = new(
        @"^\s*(?:اختبر|اختبري|شغل\s+الاختبارات|test)\s+(?<path>[^\s]+\.(?:sln|csproj))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SaveMemoryPattern = new(
        @"^\s*(?:احفظ|احفظي|سجل|سجلي)\s+(?:في\s+)?(?:الذاكره|الذاكرة|memory)\s+(?<title>[^:：]{2,80})[:：]\s*(?<content>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly NativeLearningService _learning;

    public NativeLanguageParser(NativeLearningService learning) =>
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));

    public NativeCommand Parse(string input, bool allowLearnedAliases = true)
    {
        var original = (input ?? string.Empty).Trim();
        if (original.Length == 0)
            return new NativeCommand(NativeIntent.Unknown, original);

        if (allowLearnedAliases && _learning.TryResolveAlias(original, out var learnedCommand))
        {
            var resolved = Parse(learnedCommand, allowLearnedAliases: false);
            if (resolved.Intent != NativeIntent.Unknown && resolved.Intent != NativeIntent.TeachAlias)
                return resolved with { OriginalText = original, WasLearnedAlias = true };
        }

        var normalized = NativeTextNormalizer.Normalize(original);
        if (normalized.Length == 0)
            return new NativeCommand(NativeIntent.Unknown, original);

        if (ContainsAny(normalized, "لما اقول", "عندما اقول", "when i say"))
            return new NativeCommand(NativeIntent.TeachAlias, original);
        if (IsGreeting(normalized))
            return new NativeCommand(NativeIntent.Greeting, original);
        if (ContainsAny(normalized, "مين انتي", "من انتي", "عرفيني بنفسك", "who are you", "introduce yourself"))
            return new NativeCommand(NativeIntent.Identity, original);
        if (new[]
            {
                "تقدري تعملي ايه", "ايه قدراتك", "ما هي قدراتك", "ماذا تستطيعين",
                "help", "what can you do"
            }.Contains(normalized, StringComparer.Ordinal))
            return new NativeCommand(NativeIntent.Capabilities, original);
        if (ContainsAny(normalized, "الساعه كام", "الوقت كام", "كام الساعه", "الوقت الحالي", "current time", "what time is it", "time now"))
            return new NativeCommand(NativeIntent.CurrentTime, original);
        if (ContainsAny(normalized, "تاريخ النهارده", "تاريخ اليوم", "النهارده كام", "ما تاريخ اليوم", "today date", "what is the date", "date today"))
            return new NativeCommand(NativeIntent.CurrentDate, original);
        if (ContainsAny(normalized, "معلومات النظام", "مواصفات الجهاز", "معلومات الجهاز", "system info", "device info", "system information"))
            return new NativeCommand(NativeIntent.SystemInfo, original);
        if (ContainsAny(normalized, "مساحه العمل", "مجلد العمل", "workspace info", "workspace details"))
            return new NativeCommand(NativeIntent.WorkspaceInfo, original);

        var openUrl = OpenUrlPattern.Match(original);
        if (openUrl.Success)
            return new NativeCommand(NativeIntent.OpenUrl, original, Url: TrimTrailingPunctuation(openUrl.Groups["url"].Value));

        var createFile = CreateFilePattern.Match(original);
        if (createFile.Success)
            return new NativeCommand(NativeIntent.CreateTextFile, original,
                Path: createFile.Groups["path"].Value.Trim(),
                Content: createFile.Groups["content"].Value.Trim());

        var readFile = ReadFilePattern.Match(original);
        if (readFile.Success)
            return new NativeCommand(NativeIntent.ReadTextFile, original, Path: readFile.Groups["path"].Value.Trim());

        var search = SearchPattern.Match(original);
        if (search.Success)
            return new NativeCommand(NativeIntent.SearchWeb, original, Query: search.Groups["query"].Value.Trim());

        var build = BuildPattern.Match(original);
        if (build.Success)
            return new NativeCommand(NativeIntent.BuildProject, original, Path: build.Groups["path"].Value.Trim());

        var test = TestPattern.Match(original);
        if (test.Success)
            return new NativeCommand(NativeIntent.TestProject, original, Path: test.Groups["path"].Value.Trim());

        var saveMemory = SaveMemoryPattern.Match(original);
        if (saveMemory.Success)
            return new NativeCommand(NativeIntent.SaveMemory, original,
                Title: saveMemory.Groups["title"].Value.Trim(),
                Content: saveMemory.Groups["content"].Value.Trim());

        var listFiles = ListFilesPattern.Match(original);
        if (listFiles.Success)
        {
            var directory = listFiles.Groups["directory"].Success
                ? listFiles.Groups["directory"].Value.Trim()
                : string.Empty;
            return new NativeCommand(NativeIntent.ListWorkspaceFiles, original, Directory: directory);
        }

        var programId = ResolveProgram(normalized);
        if (programId is not null)
            return new NativeCommand(NativeIntent.LaunchProgram, original, ProgramId: programId);

        return new NativeCommand(NativeIntent.Unknown, original);
    }

    private static string? ResolveProgram(string normalized) =>
        normalized switch
        {
            "افتحي الحاسبه" or "افتح الحاسبه" or "افتحي الاله الحاسبه" or "افتح الاله الحاسبه" or
                "شغلي الحاسبه" or "شغل الحاسبه" or "open calculator" or "launch calculator" or "start calculator"
                => "calculator",
            "افتحي المفكره" or "افتح المفكره" or "شغلي المفكره" or "شغل المفكره" or
                "open notepad" or "launch notepad" or "start notepad"
                => "notepad",
            "افتحي الرسام" or "افتح الرسام" or "شغلي الرسام" or "شغل الرسام" or
                "open paint" or "launch paint" or "start paint"
                => "paint",
            "افتحي مستكشف الملفات" or "افتح مستكشف الملفات" or
                "open file explorer" or "launch file explorer" or "start file explorer"
                => "file_explorer",
            "افتحي مدير المهام" or "افتح مدير المهام" or
                "open task manager" or "launch task manager" or "start task manager"
                => "task_manager",
            _ => null
        };

    private static bool IsGreeting(string normalized)
    {
        var greetings = new[]
        {
            "اهلا", "اهلين", "مرحبا", "السلام عليكم", "صباح الخير", "مساء الخير",
            "hello", "hi", "hey"
        };
        // Match only a complete greeting. A greeting followed by a real request must
        // continue through intent parsing instead of being swallowed as a salutation.
        return greetings.Contains(normalized, StringComparer.Ordinal);
    }

    private static bool ContainsAny(string source, params string[] values) =>
        values.Any(value => source.Contains(value, StringComparison.Ordinal));

    private static string TrimTrailingPunctuation(string value) =>
        value.TrimEnd('.', ',', ';', ')', ']', '}', '،', '؛');
}
