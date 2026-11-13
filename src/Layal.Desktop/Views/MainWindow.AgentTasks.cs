using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

public partial class MainWindow
{
    private async void RunAgentTaskButton_Click(object sender, RoutedEventArgs e)
    {

        if (!PrivateChatModePolicy.CanStartAgentTask(_incognitoMode))
        {
            SetAccessibleStatusText(TaskResult, IsArabic
                ? "تنفيذ المهام متوقف أثناء المحادثة الخاصة."
                : "Task execution is unavailable during private chat.");
            return;
        }

        var goal = TaskInput.Text.Trim();
        if (goal.Length == 0)
        {
            SetAccessibleStatusText(TaskResult, IsArabic ? "اكتب وصف المهمة أولًا." : "Enter a task first.");
            return;
        }
        if (_agentTaskCancellation is not null) return;

        TaskEvents.Clear();
        _rawTaskEvents.Clear();
        TaskProgressBar.Minimum = 0;
        TaskProgressBar.Maximum = 1;
        TaskProgressBar.Value = 0;
        TaskProgressStatus.Text = IsArabic
            ? "النواة المحلية تحلل الطلب وتبني خطة محدودة."
            : "The native core is interpreting the request and building a bounded plan.";
        SetAccessibleStatusText(TaskResult, IsArabic
            ? "بدأت المهمة باستخدام نواة ليال المحلية المستقلة؛ لن يتم استدعاء أي نموذج ذكاء اصطناعي."
            : "Task started with Layal's independent native core; no AI model will be called.");

        SetAccessibleStatusText(TaskCopyStatus, string.Empty);
        var cancellation = new CancellationTokenSource();
        _agentTaskCancellation = cancellation;
        RunAgentTaskButton.IsEnabled = false;
        CancelAgentTaskButton.IsEnabled = true;
        AppRuntime.MascotWindow?.SetTaskActive(true);
        AppRuntime.MascotWindow?.SetMood("thinking");

        var finalMood = "error";
        try
        {
            var record = await AppRuntime.NativeAgentOrchestrator.RunTaskAsync(
                goal,
                ConfirmToolActionAsync,
                AddTaskEvent,
                cancellation.Token,
                AppRuntime.Settings.Language);

            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTaskResult(record, IsArabic));
            finalMood = record.Status switch
            {
                "Completed" => "success",
                "CompletedWithWarnings" or "Stopped" => "warning",
                "Cancelled" => "paused",
                _ => "error"
            };
            TaskResult.Foreground = record.Status == "Completed" ? ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen) :
                record.Status is "CompletedWithWarnings" or "Stopped" or "Cancelled" ? ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod) :
                ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
            RefreshRecentTasks(record.Id);
        }
        catch (OperationCanceledException)
        {
            SetAccessibleStatusText(TaskResult, IsArabic ? "تم إلغاء المهمة." : "Task was cancelled.");
            TaskResult.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            AddTaskEvent("Native task cancelled by the user.");
            finalMood = "paused";
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(TaskResult, UiLocalization.FormatTechnicalError("فشلت المهمة", "Task failed", ex, IsArabic));
            TaskResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
            AddTaskEvent("Native task failed: " + ex.Message);
            finalMood = "error";
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_agentTaskCancellation, cancellation))
                _agentTaskCancellation = null;
            RunAgentTaskButton.IsEnabled = true;
            CancelAgentTaskButton.IsEnabled = false;
            AppRuntime.MascotWindow?.SetTaskActive(false);
        }

        AppRuntime.MascotWindow?.SetMood(finalMood);
        NotifyLiveRegion(TaskEventsList);
    }

    private void CopyTaskResultButton_Click(object sender, RoutedEventArgs e) =>
        CopyTaskTextToClipboard(TaskResult.Text, IsArabic ? "النتيجة" : "task result");

    private void CopyTaskLogButton_Click(object sender, RoutedEventArgs e) =>
        CopyTaskTextToClipboard(string.Join(Environment.NewLine, TaskEvents), IsArabic ? "سجل التنفيذ" : "task log");

    private void CopyTaskTextToClipboard(string text, string label)
    {
        if (IsPrivateTaskWorkspaceAccessBlocked()) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetAccessibleStatusText(TaskCopyStatus, IsArabic ? "لا يوجد نص لنسخه." : "There is no text to copy.");
            TaskCopyStatus.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SetAccessibleStatusText(TaskCopyStatus, IsArabic ? $"تم نسخ {label} إلى الحافظة." : $"Copied {label} to the clipboard.");
            TaskCopyStatus.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(TaskCopyStatus, (IsArabic ? "تعذر النسخ: " : "Copy failed: ") + ex.Message);
            TaskCopyStatus.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private void CancelAgentTaskButton_Click(object sender, RoutedEventArgs e) => _agentTaskCancellation?.Cancel();

    private Task<bool> ConfirmToolActionAsync(string title, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localizedTitle = UiLocalization.LocalizeApprovalTitle(title, IsArabic);
        var localizedDetails = UiLocalization.LocalizeApprovalDetails(details, IsArabic);
        var dialog = new TaskApprovalWindow(
            IsArabic,
            localizedTitle,
            localizedDetails,
            () => _agentTaskCancellation?.Cancel())
        {
            Owner = this
        };

        // Escape inside this modal window cancels the task token, not just this one action.
        // The dialog also observes cancellation so other cancellation sources cannot leave it blocking the task.
        return Task.FromResult(dialog.ShowForApproval(cancellationToken));
    }

    private void AddTaskEvent(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddTaskEvent(text));
            return;
        }
        var message = text.Length >= 10 && text[2] == ':' && text[5] == ':' && text[8] == ' '
            ? text[10..]
            : text;
        UpdateTaskProgressFromEvent(message);
        _rawTaskEvents.Add(text);
        while (_rawTaskEvents.Count > 300) _rawTaskEvents.RemoveAt(0);
        TaskEvents.Add(UiLocalization.FormatTaskEvent(text, IsArabic));
        while (TaskEvents.Count > 300) TaskEvents.RemoveAt(0);
        if (TaskEventsList.Items.Count > 0) TaskEventsList.ScrollIntoView(TaskEventsList.Items[^1]);

        // Drive facial acting from actual engine events through a unit-testable resolver.
        var mascotMood = TaskMoodResolver.Resolve(text, TaskEvents.TakeLast(4));
        if (mascotMood is not null)
            AppRuntime.MascotWindow?.SetMood(mascotMood,
                showReaction: !text.Contains("Tool result recorded:", StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateTaskProgressFromEvent(string message)
    {
        if (TryParseProgressValues(message, "Tool batch planned: ", 2, out var batch))
        {
            var batchSize = Math.Clamp(batch[0], 1, 12);
            TaskProgressBar.Minimum = 0;
            TaskProgressBar.Maximum = batchSize;
            TaskProgressBar.Value = 0;
            TaskProgressStatus.Text = IsArabic
                ? $"دفعة جديدة: {batchSize} خطوة متاحة؛ أُنجز قبلها {batch[1]} خطوة."
                : $"New batch: {batchSize} tool step(s); {batch[1]} step(s) processed before this batch.";
            return;
        }

        if (TryParseProgressValues(message, "Tool step started: ", 3, out var started))
        {
            var step = Math.Clamp(started[0], 1, 12);
            var batchSize = Math.Clamp(started[1], 1, 12);
            TaskProgressStatus.Text = IsArabic
                ? $"جارٍ تنفيذ الخطوة {step} من {batchSize}؛ المتبقي في هذه الدفعة {Math.Max(0, batchSize - step)}؛ إجمالي الخطوات السابقة {started[2]}."
                : $"Executing step {step} of {batchSize}; {Math.Max(0, batchSize - step)} remaining in this batch; {started[2]} processed earlier.";
            return;
        }

        if (TryParseProgressValues(message, "Progress update: ", 4, out var progress))
        {
            var completed = Math.Clamp(progress[0], 0, 12);
            var batchSize = Math.Clamp(progress[1], 1, 12);
            var remaining = Math.Clamp(progress[2], 0, batchSize);
            TaskProgressBar.Maximum = batchSize;
            TaskProgressBar.Value = Math.Min(completed, batchSize);
            TaskProgressStatus.Text = IsArabic
                ? $"المنفذ في الدفعة الحالية: {completed} من {batchSize}؛ المتبقي: {remaining}؛ إجمالي الخطوات التي عولجت: {progress[3]}."
                : $"Current batch processed: {completed} of {batchSize}; remaining in batch: {remaining}; total steps processed: {progress[3]}.";
        }
    }

    private static bool TryParseProgressValues(string message, string prefix, int expectedCount, out int[] values)
    {
        values = Array.Empty<int>();
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var parts = message[prefix.Length..].Split('|');
        if (parts.Length != expectedCount)
            return false;

        var parsed = new int[expectedCount];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed[i]) || parsed[i] < 0)
                return false;
        }

        values = parsed;
        return true;
    }

}
