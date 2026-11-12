using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Executes rule-planned local tasks without a language model. Every step is selected by the native
/// intent parser, sent through Layal's existing allow-listed tool executor, and verified where possible.
/// </summary>
public sealed class NativeAgentOrchestrator
{
    private const int MaximumPlanSteps = 6;
    private readonly NativeLanguageParser _parser;
    private readonly NativeLearningService _learning;
    private readonly AgentToolExecutor _tools;
    private readonly TaskHistoryService _history;

    public NativeAgentOrchestrator(
        NativeLanguageParser parser,
        NativeLearningService learning,
        AgentToolExecutor tools,
        TaskHistoryService history)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public async Task<AgentTaskRecord> RunTaskAsync(
        string goal,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> onEvent,
        CancellationToken cancellationToken,
        string interfaceLanguage = "ar")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(onEvent);

        var task = new AgentTaskRecord { Goal = goal.Trim(), Status = "Running" };
        var lastPersist = DateTimeOffset.MinValue;
        void Log(string message)
        {
            var line = $"{DateTimeOffset.Now:HH:mm:ss}  {message}";
            task.Events.Add(line);
            if (task.Events.Count > 300) task.Events.RemoveAt(0);
            task.UpdatedAt = DateTimeOffset.Now;
            if (DateTimeOffset.UtcNow - lastPersist >= TimeSpan.FromSeconds(2))
            {
                lastPersist = DateTimeOffset.UtcNow;
                _history.Upsert(task);
            }
            onEvent(line);
        }

        _history.Upsert(task);
        var verified = false;
        var intentName = "Unknown";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log("Native cognitive core selected. No external model, Ollama, Qwen, or AI API will be called.");

            if (_learning.TryTeachFromUtterance(goal, out var learnedMessage))
            {
                task.Status = "Completed";
                task.Result = learnedMessage;
                intentName = "TeachAlias";
                Log("A user-directed command alias was saved to encrypted local learning state.");
                _learning.RecordExperience(intentName, task.Status, verified: true);
                _history.Upsert(task);
                return task;
            }

            var command = _parser.Parse(goal);
            intentName = command.Intent.ToString();
            var plan = CreatePlan(command);
            if (plan.Count == 0)
            {
                task.Status = "Failed";
                task.Result = UnsupportedIntentMessage(interfaceLanguage);
                Log("No deterministic intent rule matched. Nothing was executed and no model fallback was attempted.");
                _learning.RecordExperience(intentName, task.Status, verified: false);
                _history.Upsert(task);
                return task;
            }

            if (plan.Count > MaximumPlanSteps)
                throw new InvalidOperationException("The native planner produced more than the permitted number of steps.");

            Log($"Native plan created: {plan.Count} bounded step(s).");
            foreach (var step in plan)
                Log("Planned: " + step.Description);

            var outputs = new List<string>();
            foreach (var step in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log("Starting native action: " + step.Description);
                var output = await _tools.ExecuteAsync(
                    step.ToolName, step.ArgumentsJson, confirm, Log, cancellationToken, keepReadDataLocal: true);
                outputs.Add(output);
                Log($"Native action returned: {step.ToolName}.");

                if (IsDeclined(output))
                {
                    task.Status = "Stopped";
                    task.Result = output + Environment.NewLine + "لم تبدأ ليال أي خطوة تابعة بعد رفض الموافقة.";
                    Log("The user declined the action. No dependent steps were started.");
                    _learning.RecordExperience(intentName, task.Status, verified: false);
                    _history.Upsert(task);
                    return task;
                }

                if (step.ToolName == "create_text_file" && output.StartsWith("File created: ", StringComparison.Ordinal))
                {
                    var relative = output["File created: ".Length..]
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (!string.IsNullOrWhiteSpace(relative) && !task.Artifacts.Contains(relative, StringComparer.OrdinalIgnoreCase))
                        task.Artifacts.Add(relative);
                }

                if (step.VerifyToolName is not null)
                {
                    Log("Verifying the intended result locally: " + step.VerificationDescription);
                    var verification = await _tools.ExecuteAsync(
                        step.VerifyToolName, step.VerifyArgumentsJson ?? "{}", confirm, Log, cancellationToken, keepReadDataLocal: true);
                    var passed = !IsDeclined(verification) &&
                        (step.ExpectedText is null || verification.Contains(step.ExpectedText, StringComparison.Ordinal));
                    if (!passed)
                    {
                        task.Status = "CompletedWithWarnings";
                        task.Result = string.Join(Environment.NewLine + Environment.NewLine, outputs) +
                            Environment.NewLine + Environment.NewLine +
                            "تعذر التحقق من النتيجة بعد التنفيذ. لم أعتبر المهمة مكتملة بالكامل.";
                        Log("Verification did not pass. The action result is retained, but completion is not claimed.");
                        _learning.RecordExperience(intentName, task.Status, verified: false);
                        _history.Upsert(task);
                        return task;
                    }
                    outputs.Add(verification);
                    verified = true;
                    Log("Local verification passed.");
                }
                else
                {
                    verified = step.VerificationDescription == "The tool result itself is the final read-only result.";
                }
            }

            task.Result = string.Join(Environment.NewLine + Environment.NewLine, outputs);
            task.Status = plan.Any(step => step.RequiresExternalOutcomeCheck) ? "CompletedWithWarnings" : "Completed";
            if (task.Status == "CompletedWithWarnings")
                task.Result += Environment.NewLine + Environment.NewLine +
                    "تم تنفيذ الطلب المحلي، لكن النتيجة الخارجية (مثل تحميل صفحة أو ظهور تطبيق) لم تُرصد بصورة مستقلة.";
            Log(task.Status == "Completed"
                ? "Native task completed with the available result verified."
                : "Native action was dispatched, but its external outcome still needs observation.");
            _learning.RecordExperience(intentName, task.Status, verified);
            _history.Upsert(task);
            return task;
        }
        catch (OperationCanceledException)
        {
            task.Status = "Cancelled";
            task.Result = "تم إلغاء المهمة. أي آثار اكتملت قبل الإلغاء موضحة في سجل التنفيذ، ولا يمكن التراجع تلقائيًا عن كل أثر خارجي.";
            Log("Native task cancelled. No new action will be started.");
            _learning.RecordExperience(intentName, task.Status, verified: false);
            _history.Upsert(task);
            return task;
        }
        catch (Exception ex)
        {
            task.Status = "Failed";
            task.Result = ex.Message;
            Log("Native task failed: " + ex.Message);
            _learning.RecordExperience(intentName, task.Status, verified: false);
            _history.Upsert(task);
            return task;
        }
    }

    private static List<NativePlanStep> CreatePlan(NativeCommand command)
    {
        var plan = new List<NativePlanStep>();
        switch (command.Intent)
        {
            case NativeIntent.CurrentTime:
                plan.Add(new NativePlanStep("Read the current local time", "get_current_time", "{}",
                    VerificationDescription: "The tool result itself is the final read-only result."));
                break;
            case NativeIntent.SystemInfo:
                plan.Add(new NativePlanStep("Read non-sensitive system information", "get_system_info", "{}",
                    VerificationDescription: "The tool result itself is the final read-only result."));
                break;
            case NativeIntent.WorkspaceInfo:
                plan.Add(new NativePlanStep("Read approved workspace metadata", "get_workspace_info", "{}",
                    VerificationDescription: "The tool result itself is the final read-only result."));
                break;
            case NativeIntent.ListWorkspaceFiles:
                plan.Add(new NativePlanStep("List entries inside the approved workspace", "list_workspace_files",
                    JsonSerializer.Serialize(new { directory = command.Directory ?? string.Empty }),
                    VerificationDescription: "The tool result itself is the final read-only result."));
                break;
            case NativeIntent.ReadTextFile when !string.IsNullOrWhiteSpace(command.Path):
                plan.Add(new NativePlanStep("Read one approved workspace text file locally", "read_text_file",
                    JsonSerializer.Serialize(new { path = command.Path, start_character = 0, max_characters = 18000 }),
                    VerificationDescription: "The approved file slice was returned to Layal's local core."));
                break;
            case NativeIntent.CreateTextFile when !string.IsNullOrWhiteSpace(command.Path) && command.Content is not null:
                plan.Add(new NativePlanStep("Create a new file after showing its exact content for approval", "create_text_file",
                    JsonSerializer.Serialize(new { path = command.Path, content = command.Content }),
                    VerifyToolName: "read_text_file",
                    VerifyArgumentsJson: JsonSerializer.Serialize(new { path = command.Path, start_character = 0, max_characters = 18000 }),
                    ExpectedText: command.Content,
                    VerificationDescription: "Read the created file back locally and compare its content."));
                break;
            case NativeIntent.SearchWeb when !string.IsNullOrWhiteSpace(command.Query):
                plan.Add(new NativePlanStep("Search the web through the dedicated browser after local approval", "web_search",
                    JsonSerializer.Serialize(new { query = command.Query }),
                    VerificationDescription: "The bounded search result itself is the returned evidence."));
                break;
            case NativeIntent.OpenUrl when !string.IsNullOrWhiteSpace(command.Url):
                plan.Add(new NativePlanStep("Open the approved URL in the default browser", "open_url",
                    JsonSerializer.Serialize(new { url = command.Url }),
                    RequiresExternalOutcomeCheck: true,
                    VerificationDescription: "The tool result itself is the final read-only result."));
                break;
            case NativeIntent.LaunchProgram when !string.IsNullOrWhiteSpace(command.ProgramId):
                plan.Add(new NativePlanStep("Launch one allow-listed Windows application after approval", "launch_allowed_program",
                    JsonSerializer.Serialize(new { program = command.ProgramId }),
                    RequiresExternalOutcomeCheck: true,
                    VerificationDescription: "The tool result itself is the final read-only result."));
                break;
            case NativeIntent.BuildProject when !string.IsNullOrWhiteSpace(command.Path):
                plan.Add(new NativePlanStep("Build the specified workspace project after approval", "run_dotnet_build",
                    JsonSerializer.Serialize(new { project_file = command.Path, configuration = "Release" }),
                    VerificationDescription: "The build tool returned its final process result."));
                break;
            case NativeIntent.TestProject when !string.IsNullOrWhiteSpace(command.Path):
                plan.Add(new NativePlanStep("Run tests for the specified workspace project after approval", "run_dotnet_test",
                    JsonSerializer.Serialize(new { project_file = command.Path, configuration = "Release" }),
                    VerificationDescription: "The test tool returned its final process result."));
                break;
            case NativeIntent.SaveMemory when !string.IsNullOrWhiteSpace(command.Title) && !string.IsNullOrWhiteSpace(command.Content):
                plan.Add(new NativePlanStep("Save the explicitly requested note to encrypted local memory", "save_memory",
                    JsonSerializer.Serialize(new { title = command.Title, content = command.Content }),
                    VerificationDescription: "The memory tool returned its saved-note result."));
                break;
        }
        return plan;
    }

    private static bool IsDeclined(string output) =>
        output.StartsWith("User declined", StringComparison.OrdinalIgnoreCase) ||
        output.StartsWith("WEB SEARCH NOT SHARED", StringComparison.OrdinalIgnoreCase);

    private static string UnsupportedIntentMessage(string language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "I could not map this request to a verified native capability. Nothing was executed and no cloud/local model was called. Ask for a supported capability list or phrase the task as a bounded operation such as listing workspace files, reading/creating a file, web search, launching an allow-listed app, or building/testing a workspace project."
            : "لم أستطع ربط الطلب بقدرة محلية متحققة. لم أنفذ أي إجراء ولم أستدعِ نموذجًا محليًا أو سحابيًا. جرّب صياغة عملية محددة مثل عرض ملفات مساحة العمل، قراءة/إنشاء ملف، البحث على الويب، تشغيل برنامج مسموح، أو بناء/اختبار مشروع داخل مساحة العمل.";

    private sealed record NativePlanStep(
        string Description,
        string ToolName,
        string ArgumentsJson,
        string? VerifyToolName = null,
        string? VerifyArgumentsJson = null,
        string? ExpectedText = null,
        bool RequiresExternalOutcomeCheck = false,
        string? VerificationDescription = null);
}
