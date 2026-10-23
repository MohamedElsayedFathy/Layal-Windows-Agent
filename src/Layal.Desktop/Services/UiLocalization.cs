using System.Globalization;
using System.Text.RegularExpressions;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Presentation-only translations for UI feedback that originates in the task engine or Windows APIs.
/// Canonical task states and persisted event text remain unchanged for diagnostics and integrations.
/// </summary>
public static class UiLocalization
{
    private static readonly CultureInfo ArabicCulture = CultureInfo.GetCultureInfo("ar-EG");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    public static CultureInfo GetCulture(bool isArabic) => isArabic ? ArabicCulture : EnglishCulture;

    public static string FormatDateTime(DateTime value, bool isArabic) =>
        value.ToString("g", GetCulture(isArabic));

    public static string FormatTaskStatus(string status, bool isArabic) => status switch
    {
        "Pending" => isArabic ? "قيد الانتظار" : "Pending",
        "Running" => isArabic ? "قيد التنفيذ" : "Running",
        "Completed" => isArabic ? "مكتملة" : "Completed",
        "CompletedWithWarnings" => isArabic ? "مكتملة مع تحذيرات" : "Completed with warnings",
        "Failed" => isArabic ? "فشلت" : "Failed",
        "Cancelled" => isArabic ? "أُلغيت" : "Cancelled",
        _ => status
    };

    public static string FormatTaskResult(AgentTaskRecord task, bool isArabic) =>
        $"{FormatTaskStatus(task.Status, isArabic)}{Environment.NewLine}{FormatKnownTaskResult(task.Result, isArabic)}";

    public static string FormatKnownTaskResult(string result, bool isArabic)
    {
        if (result == "The model returned an empty final answer.")
            return isArabic ? "أعاد النموذج إجابة نهائية فارغة." : result;

        var safety = Regex.Match(result, @"^Safety limit reached: no more than (?<count>\d+) tool calls are allowed per task\.$");
        if (safety.Success)
            return isArabic
                ? $"تم بلوغ حد الأمان: لا يُسمح بأكثر من {safety.Groups["count"].Value} استدعاء أداة للمهمة الواحدة."
                : $"Safety limit reached: no more than {safety.Groups["count"].Value} tool calls are allowed per task.";

        var rounds = Regex.Match(result, @"^Execution stopped after the (?<count>\d+)-round model limit\. Review the activity log and split the request into smaller tasks\.$");
        if (rounds.Success)
            return isArabic
                ? $"توقف التنفيذ بعد بلوغ حد النموذج البالغ {rounds.Groups["count"].Value} جولات. راجع سجل النشاط وقسّم الطلب إلى مهام أصغر."
                : $"Execution stopped after the {rounds.Groups["count"].Value}-round model limit. Review the activity log and split the request into smaller tasks.";

        if (result.StartsWith("Task was cancelled.", StringComparison.Ordinal))
            return isArabic
                ? "أُلغيت المهمة. تظهر الإجراءات التي اكتملت في سجل النشاط؛ ولا يمكن التراجع تلقائيًا عن كل إجراء خارجي."
                : "Task was cancelled. Completed side effects are listed in the activity log; cancellation cannot automatically undo every external action.";

        return result;
    }

    public static string FormatTaskEvent(string eventLine, bool isArabic)
    {
        // Agent events start with a stable HH:mm:ss prefix. Keep it intact for chronology.
        var timestamp = string.Empty;
        var message = eventLine;
        if (eventLine.Length >= 10 &&
            char.IsAsciiDigit(eventLine[0]) && char.IsAsciiDigit(eventLine[1]) &&
            eventLine[2] == ':' && eventLine[5] == ':' && eventLine[8] == ' ')
        {
            timestamp = eventLine[..10];
            message = eventLine[10..];
        }

        return timestamp + FormatTaskEventMessage(message, isArabic);
    }

    private static string FormatTaskEventMessage(string message, bool isArabic)
    {
        if (message.StartsWith("Task accepted;", StringComparison.Ordinal))
            return isArabic ? "تم قبول المهمة؛ يجري إعداد خطة تنفيذ محدودة باستخدام النموذج." : "Task accepted; building a bounded execution plan through the configured model.";

        var turn = Regex.Match(message, @"^Requesting model turn (?<current>\d+)/(?<total>\d+)\.$");
        if (turn.Success)
            return isArabic
                ? $"جارٍ طلب جولة النموذج {turn.Groups["current"].Value} من {turn.Groups["total"].Value}."
                : $"Requesting model turn {turn.Groups["current"].Value} of {turn.Groups["total"].Value}.";

        if (message == "Model returned a final response, but one or more tool actions failed or were declined. Review the event log before treating the goal as complete.")
            return isArabic ? "أعاد النموذج ردًا نهائيًا، لكن إجراء أداة واحدًا أو أكثر فشل أو رُفض. راجع سجل النشاط قبل اعتبار المهمة مكتملة." : message;

        if (message == "Model returned a final response. Tool activity is recorded above.")
            return isArabic ? "أعاد النموذج ردًا نهائيًا. تفاصيل الأدوات مسجلة أعلاه." : message;

        if (message == "Model provided an intermediate note before requesting tools.")
            return isArabic ? "قدّم النموذج ملاحظة وسيطة قبل طلب الأدوات." : message;

        if (message == "Task cancellation requested; no new tool calls will be started.")
            return isArabic ? "طُلب إلغاء المهمة؛ لن يبدأ أي استدعاء جديد للأدوات." : message;

        if (message == "Task cancelled by the user.")
            return isArabic ? "ألغى المستخدم المهمة." : message;

        if (message.StartsWith("Tool requested: ", StringComparison.Ordinal))
            return isArabic ? "طلبت المهمة الأداة: " + message["Tool requested: ".Length..] : "Tool requested: " + message["Tool requested: ".Length..];

        if (message.StartsWith("Tool result recorded: ", StringComparison.Ordinal))
            return isArabic ? "تم تسجيل نتيجة الأداة: " + message["Tool result recorded: ".Length..] : message;

        if (message.StartsWith("Tool succeeded: ", StringComparison.Ordinal))
            return isArabic ? "نجحت الأداة: " + message["Tool succeeded: ".Length..] : message;

        if (message.StartsWith("Tool failed: ", StringComparison.Ordinal))
            return isArabic ? "فشل تنفيذ الأداة: " + message["Tool failed: ".Length..] : message;

        if (message.StartsWith("Task failed: ", StringComparison.Ordinal))
            return isArabic ? "فشلت المهمة: " + message["Task failed: ".Length..] : message;

        if (message.StartsWith("User declined", StringComparison.OrdinalIgnoreCase))
            return isArabic ? "رفض المستخدم الإجراء المطلوب. " + message : message;

        if (message == "Model request cancelled.")
            return isArabic ? "أُلغيت مطالبة النموذج." : message;

        return FormatKnownTaskResult(message, isArabic);
    }

    public static string LocalizeApprovalTitle(string title, bool isArabic)
    {
        if (!isArabic) return title;
        return title switch
        {
            "Read and share file" => "قراءة ملف ومشاركته",
            "Create file" => "إنشاء ملف",
            "Modify file" => "تعديل ملف",
            "Launch Notepad" => "فتح المفكرة",
            "Open website" => "فتح موقع",
            "Share open-window titles" => "مشاركة عناوين النوافذ المفتوحة",
            "Activate window" => "تنشيط نافذة",
            "Share memory with AI" => "مشاركة الذاكرة مع الذكاء الاصطناعي",
            "Save memory" => "حفظ الذاكرة",
            _ when title.StartsWith("Run dotnet build", StringComparison.Ordinal) => "تشغيل dotnet build",
            _ when title.StartsWith("Run dotnet test", StringComparison.Ordinal) => "تشغيل dotnet test",
            _ => title
        };
    }

    public static string LocalizeApprovalDetails(string details, bool isArabic)
    {
        if (!isArabic) return details;

        if (details == "Allow Layal to launch the Windows Notepad application?")
            return "هل تسمح لليال بفتح تطبيق المفكرة في Windows؟";

        if (details.StartsWith("Open this URL in the default browser?\n", StringComparison.Ordinal))
            return "هل تريد فتح الرابط التالي في المتصفح الافتراضي؟\n" + details["Open this URL in the default browser?\n".Length..];

        if (details.StartsWith("Try to bring a window with this exact title to the foreground?\n", StringComparison.Ordinal))
            return "هل تريد محاولة إظهار النافذة ذات العنوان المطابق في المقدمة؟\n" + details["Try to bring a window with this exact title to the foreground?\n".Length..];

        if (details.StartsWith("Read '", StringComparison.Ordinal) &&
            details.Contains("' and send its contents to the configured AI provider?", StringComparison.Ordinal))
        {
            var end = details.IndexOf("' and send its contents to the configured AI provider?", StringComparison.Ordinal);
            var path = details[6..end];
            return $"هل تريد قراءة الملف '{path}' وإرسال محتواه إلى مزود الذكاء الاصطناعي؟ لا تفعل ذلك إلا إذا تأكدت من خلو الملف من الأسرار والبيانات الخاصة.";
        }

        if (details.StartsWith("Create a new file at '", StringComparison.Ordinal))
            return details.Replace("Create a new file at '", "هل تريد إنشاء ملف جديد في '", StringComparison.Ordinal)
                .Replace(" characters)?", " حرفًا)؟", StringComparison.Ordinal);

        if (details.StartsWith("Replace one exact text match in '", StringComparison.Ordinal))
            return details.Replace("Replace one exact text match in '", "هل تريد استبدال تطابق نصي واحد في '", StringComparison.Ordinal)
                .Replace("'? A timestamped backup will be created first.", "'؟ سيتم إنشاء نسخة احتياطية مؤرخة أولًا.", StringComparison.Ordinal);

        if (details.StartsWith("The following window titles and process IDs will be sent", StringComparison.Ordinal))
        {
            var separator = details.IndexOf("\n\n", StringComparison.Ordinal);
            var listed = separator >= 0 ? details[(separator + 2)..] : string.Empty;
            return "سيتم إرسال عناوين النوافذ التالية ومعرّفات العمليات إلى مزود الذكاء الاصطناعي. قد تكشف معلومات خاصة. هل تريد المتابعة؟"
                   + (listed.Length > 0 ? "\n\n" + listed : string.Empty);
        }

        if (details.StartsWith("Run `dotnet ", StringComparison.Ordinal))
        {
            var match = Regex.Match(details, @"^Run `dotnet (?<verb>build|test)` on '(?<path>.*)' using (?<config>Debug|Release)\? Build/test targets may execute code in this project and may restore packages from the internet\. Only continue if you trust the project\.$");
            if (match.Success)
            {
                var verb = match.Groups["verb"].Value;
                var action = verb == "build" ? "البناء" : "الاختبار";
                return $"هل تريد تشغيل dotnet {verb} على المشروع '{match.Groups["path"].Value}' باستخدام {match.Groups["config"].Value}؟ قد تنفذ أهداف المشروع كودًا وتستعيد حزمًا من الإنترنت. لا تتابع إلا إذا كنت تثق بالمشروع.";
            }
        }

        if (details.StartsWith("Matching memory notes will be sent to the configured AI provider.", StringComparison.Ordinal))
        {
            var separator = details.IndexOf("\n\n", StringComparison.Ordinal);
            var listed = separator >= 0 ? details[(separator + 2)..] : string.Empty;
            return "ستُرسل ملاحظات الذاكرة المطابقة إلى مزود الذكاء الاصطناعي. راجع المحتوى ووافق على مشاركته؟"
                   + (listed.Length > 0 ? "\n\n" + listed : string.Empty);
        }

        if (details.StartsWith("Save this note encrypted on this Windows user profile?", StringComparison.Ordinal))
            return details.Replace("Save this note encrypted on this Windows user profile?", "هل تريد حفظ هذه الملاحظة مشفرة على ملف مستخدم Windows الحالي؟", StringComparison.Ordinal)
                .Replace("Title:", "العنوان:", StringComparison.Ordinal)
                .Replace("Content:", "المحتوى:", StringComparison.Ordinal);

        return details;
    }

    public static string FormatTechnicalError(string contextArabic, string contextEnglish, Exception exception, bool isArabic) =>
        isArabic
            ? $"{contextArabic} (التفاصيل التقنية: {exception.Message})"
            : $"{contextEnglish} (Technical details: {exception.Message})";

    public static string TranslateKnownFeedback(string value, bool isArabic)
    {
        var pairs = new (string Arabic, string English)[]
        {
            ("تم حفظ الإعدادات. لم يتم تأكيد الاتصال حتى الآن؛ استخدم اختبار الاتصال.", "Settings saved. Connectivity is not confirmed yet; run the connection test."),
            ("أدخل مفتاح API ثم اختبر الاتصال. لن يتم حفظه إلا إذا ضغطت حفظ الإعدادات.", "Enter an API key to test. It is saved only when you select Save settings."),
            ("جارٍ اختبار الاتصال الفعلي...", "Testing the actual endpoint..."),
            ("تم حذف المفتاح المحفوظ.", "Saved key deleted."),
            ("تم تطبيق إعدادات الشخصية.", "Mascot settings applied."),
            ("بانتظار أول جزء من الرد...", "Waiting for the first response chunk..."),
            ("ليال بتكتب...", "Layal is responding..."),
            ("اكتمل الرد", "Response completed"),
            ("تم الإيقاف", "Cancelled"),
            ("فشل الطلب", "Request failed"),
            ("لا يوجد نص لنسخه.", "There is no text to copy."),
            ("تعذر تحديد الرسالة لنسخها.", "Could not identify the message to copy."),
            ("تعذر النسخ.", "Copy failed."),
            ("تم إلغاء المهمة.", "Task was cancelled."),
            ("اكتب وصف المهمة أولًا.", "Enter a task first."),
            ("لا يوجد مفتاح API محفوظ. افتح الإعدادات واختبر الاتصال أولًا.", "No saved API key. Configure and test the provider in Settings first."),
            ("استخدم 3 مهارات كحد أقصى في المهمة الواحدة.", "Select no more than 3 skills for one task."),
            ("بدأت المهمة. راقب سجل التنفيذ والموافقات.", "Task started. Watch the activity log and approval prompts."),
            ("اكتب ملاحظة جديدة ثم اضغط حفظ.", "Write a new note and select Save."),
            ("تم حفظ الملاحظة محليًا بصورة مشفرة.", "Note saved locally with encryption."),
            ("حدد ملاحظة لحذفها.", "Select a note to delete."),
            ("تم حذف الملاحظة.", "Memory note deleted."),
            ("تم تصدير الذاكرة. الملف غير مشفر.", "Memory exported. The file is not encrypted.")
        };

        foreach (var pair in pairs)
        {
            if (value == pair.Arabic || value == pair.English)
                return isArabic ? pair.Arabic : pair.English;

            if (value.StartsWith(pair.Arabic, StringComparison.Ordinal))
                return (isArabic ? pair.Arabic : pair.English) + value[pair.Arabic.Length..];

            if (value.StartsWith(pair.English, StringComparison.Ordinal))
                return (isArabic ? pair.Arabic : pair.English) + value[pair.English.Length..];
        }

        if (value.StartsWith("Connection test failed: ", StringComparison.Ordinal) ||
            value.StartsWith("فشل اختبار الاتصال: ", StringComparison.Ordinal))
        {
            var index = value.StartsWith("Connection test failed: ", StringComparison.Ordinal) ? "Connection test failed: ".Length : "فشل اختبار الاتصال: ".Length;
            return (isArabic ? "فشل اختبار الاتصال: " : "Connection test failed: ") + value[index..];
        }

        if (value.StartsWith("Copy failed: ", StringComparison.Ordinal) || value.StartsWith("تعذر النسخ: ", StringComparison.Ordinal))
        {
            var index = value.StartsWith("Copy failed: ", StringComparison.Ordinal) ? "Copy failed: ".Length : "تعذر النسخ: ".Length;
            return (isArabic ? "تعذر النسخ: " : "Copy failed: ") + value[index..];
        }

        return value;
    }
}
