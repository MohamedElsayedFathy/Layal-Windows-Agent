using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Layal.Services;

/// <summary>
/// First independent conversational surface for Layal. It uses deterministic language rules,
/// local memory retrieval and extractive document summarization; it never calls an AI endpoint.
/// </summary>
public sealed class NativeConversationEngine
{
    private readonly NativeLanguageParser _parser;
    private readonly NativeLearningService _learning;
    private readonly MemoryService _memory;

    public NativeConversationEngine(NativeLanguageParser parser, NativeLearningService learning, MemoryService memory)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public string Respond(string input, string language, string? documentText = null, string? documentName = null)
    {
        var isArabic = !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        if (_learning.TryTeachFromUtterance(input, out var learningMessage))
            return learningMessage;
        if (!string.IsNullOrWhiteSpace(documentText))
            return RespondToDocument(input, documentText, documentName ?? "attachment", isArabic);

        var command = _parser.Parse(input);
        return command.Intent switch
        {
            NativeIntent.Greeting => isArabic
                ? "أهلًا بيك. أنا ليال، والرد ده صادر من نواتي المحلية من غير استدعاء نموذج ذكاء اصطناعي خارجي."
                : "Hello. I'm Layal, and this reply came from my local native core without calling an external AI model.",
            NativeIntent.Identity => isArabic
                ? "أنا ليال. في هذا المسار أستخدم نواة محلية رمزية: محلّل أوامر، قواعد قرار، تخطيط محدود، ذاكرة محلية، وتحقق من نتائج الأدوات. لا أستدعي Ollama أو Qwen أو مزود API لتوليد الرد. قدرات الفهم المفتوح والتعلم الإحصائي العميق ما زالت قيد البناء."
                : "I'm Layal. This path uses a local symbolic core: an intent parser, decision rules, bounded planning, local memory, and tool-result verification. It does not call Ollama, Qwen, or an API model to generate replies. Open-ended language understanding and deep statistical learning are still under development.",
            NativeIntent.Capabilities => Capabilities(isArabic),
            NativeIntent.CurrentTime => DateTimeOffset.Now.ToString("T", CultureInfo.GetCultureInfo(isArabic ? "ar-EG" : "en-US")),
            NativeIntent.CurrentDate => DateTimeOffset.Now.ToString("D", CultureInfo.GetCultureInfo(isArabic ? "ar-EG" : "en-US")),
            NativeIntent.TeachAlias => isArabic
                ? "علّميني اختصارًا بهذه الصيغة: «لما أقول [عبارة] اعملي [أمر]». سأحفظه محليًا، ولن ينفّذ إلا أمرًا موجودًا في قائمة قدراتي."
                : "Teach me an alias with: “When I say [phrase], do [command]”. I will store it locally and only map it to a command already supported by my core.",
            _ => RespondFromLocalKnowledge(input, isArabic)
        };
    }

    private string RespondToDocument(string question, string document, string name, bool isArabic)
    {
        if (document.Length > 1_000_000)
            return isArabic
                ? "المرفق أكبر من حد المعالجة المحلية الحالي (مليون حرف). لم أرسل محتواه إلى أي نموذج."
                : "The attachment exceeds the current local-processing limit (one million characters). Its contents were not sent to any model.";

        var normalizedQuestion = NativeTextNormalizer.Normalize(question);
        if (normalizedQuestion.Contains("لخص", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("ملخص", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("summarize", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("summary", StringComparison.Ordinal))
            return BuildExtractiveSummary(document, name, isArabic);

        var queryTerms = GetTerms(question);
        if (queryTerms.Count > 0)
        {
            var matches = SplitSentences(document)
                .Select(sentence => new
                {
                    Sentence = sentence,
                    Score = queryTerms.Count(term =>
                        NativeTextNormalizer.Normalize(sentence).Contains(term, StringComparison.Ordinal))
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Sentence.Length)
                .Take(5)
                .ToList();
            if (matches.Count > 0)
                return (isArabic ? $"وجدت المقاطع التالية داخل «{name}» باستخدام بحث محلي بالكلمات:" : $"I found these passages in “{name}” using local keyword matching:")
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, matches.Select((item, index) => $"{index + 1}. {Trim(item.Sentence, 500)}"));
        }

        return isArabic
            ? $"استخرجت النص من «{name}» محليًا. أقدر ألخّصه استخراجيًا أو أبحث داخله عن كلمات محددة؛ لا أستخدم نموذجًا لغويًا خارجيًا لإعادة صياغة المستند."
            : $"I extracted the text from “{name}” locally. I can create an extractive summary or search it for specific terms; I do not use an external language model to rewrite the document.";
    }

    private static string BuildExtractiveSummary(string document, string name, bool isArabic)
    {
        var sentences = SplitSentences(document).Where(sentence => sentence.Length >= 25).Take(5000).ToList();
        if (sentences.Count == 0)
            return isArabic ? $"لم أجد جملًا كافية لتلخيص «{name}»." : $"I did not find enough sentences to summarize “{name}”.";

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sentence in sentences)
            foreach (var term in GetTerms(sentence))
                frequencies[term] = frequencies.GetValueOrDefault(term) + 1;

        var scored = sentences.Select((sentence, index) =>
        {
            var terms = GetTerms(sentence);
            var score = terms.Count == 0 ? 0d : terms.Sum(term => 1d + Math.Log(1 + frequencies.GetValueOrDefault(term))) / Math.Sqrt(terms.Count);
            return new { Sentence = sentence, Index = index, Score = score };
        });
        var selected = scored.OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(5)
            .OrderBy(item => item.Index)
            .Select(item => Trim(item.Sentence, 450))
            .ToList();

        return (isArabic
                ? $"ملخص استخراجي محلي لـ «{name}» (اخترت الجمل الأعلى تكرارًا من حيث الكلمات المهمة؛ هذا ليس تلخيصًا توليديًا):"
                : $"Local extractive summary of “{name}” (sentences ranked by important-word frequency; this is not generative summarization):")
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, selected.Select((sentence, index) => $"{index + 1}. {sentence}"));
    }

    private string RespondFromLocalKnowledge(string input, bool isArabic)
    {
        var terms = GetTerms(input);
        if (terms.Count > 0)
        {
            var memories = _memory.GetAll()
                .Select(note => new
                {
                    Note = note,
                    Score = terms.Count(term =>
                    {
                        var searchable = NativeTextNormalizer.Normalize(note.Title + " " + note.Content);
                        return searchable.Contains(term, StringComparison.Ordinal);
                    })
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Note.UpdatedAt)
                .Take(3)
                .ToList();

            if (memories.Count > 0)
                return (isArabic ? "وجدت معلومات مرتبطة في ذاكرتي المحلية:" : "I found related information in my local memory:")
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine + Environment.NewLine,
                        memories.Select(item => $"**{item.Note.Title}**{Environment.NewLine}{Trim(item.Note.Content, 1200)}"));
        }

        var recent = _learning.GetRecentExperiences(10);
        if (recent.Count > 0 && isArabic)
        {
            var verified = recent.Count(item => item.Verified);
            return $"لم أفهم الطلب بدرجة كافية لتنفيذه بأمان. نواتي الحالية تدعم أوامر محددة، وسجل التعلم المحلي يحتوي {recent.Count} خبرات تنفيذية، منها {verified} نتيجة متحققة. وضّح الهدف أو اسأليني عن قدراتي.";
        }

        return isArabic
            ? "لم أتعرف على المقصود بعد. لن أخمّن أو أستدعي نموذجًا خارجيًا بدلًا مني. حاليًا أستطيع فهم التحية، الوقت والتاريخ، معلومات النظام، الذاكرة المحلية، وبعض أوامر الملفات والبحث وتشغيل البرامج من تبويب المهام. اسأليني «إيه قدراتك؟» لمعرفة القائمة."
            : "I could not identify the intent with enough confidence. I will not guess or call an external model as a substitute. I currently understand greetings, time and date, system information, local memory, and a bounded set of file, search, and application commands in the Tasks tab. Ask “What can you do?” for the current list.";
    }

    private static string Capabilities(bool isArabic) => isArabic
        ? "قدرات النواة المحلية الحالية:\n1. فهم أوامر محددة بالعربية والإنجليزية.\n2. الرد عن الوقت والتاريخ وهوية ليال وقدراتها.\n3. البحث في الذاكرة المحلية المشفرة.\n4. تعلّم اختصارات يحددها المستخدم وحفظها محليًا.\n5. تلخيص المستندات بطريقة استخراجيّة والبحث بالكلمات داخلها.\n6. تنفيذ أوامر محددة من تبويب المهام: عرض ملفات مساحة العمل، قراءة/إنشاء ملف بموافقة، البحث على الويب، فتح رابط، تشغيل برامج مسموح بها، وعمليات dotnet build/test.\n\nلا تدّعي النواة الحالية فهمًا عامًا أو تدريبًا ذاتيًا لنموذج عصبي؛ هذه قدرات لم تكتمل بعد."
        : "Current native-core capabilities:\n1. Parse a bounded set of Arabic and English commands.\n2. Answer time, date, identity, and capability questions.\n3. Search encrypted local memory.\n4. Learn user-defined command aliases and persist them locally.\n5. Produce extractive document summaries and keyword matches.\n6. Run bounded task commands: list workspace files, read/create files with approval, web search, open URLs, launch allow-listed apps, and dotnet build/test.\n\nThe current core does not claim general understanding or self-training of a neural language model; those capabilities are not complete.";

    private static List<string> GetTerms(string text)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "من", "في", "على", "عن", "الى", "الي", "ده", "دي", "دا", "ايه", "ما", "هو", "هي", "انا",
            "the", "and", "for", "with", "from", "that", "this", "what", "when", "where", "how", "you", "your"
        };
        return NativeTextNormalizer.Normalize(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length >= 3 && !ignored.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
    }

    private static List<string> SplitSentences(string text) =>
        Regex.Split(text, @"(?<=[.!?؟])\s+|\r?\n+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

    private static string Trim(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum] + "…";
}
