using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Executes the explicitly scoped tools that inspect or interact with visible external Windows applications.
/// Approval and audit callbacks are supplied by AgentToolExecutor so the existing permission boundary is preserved.
/// </summary>
internal sealed class ExternalWindowToolExecutor
{
    internal async Task<string> GetWindowTitlesAsync(Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Ask the user to enable it first.";
        var titles = WindowInventory.GetVisibleWindowTitles();
        if (titles.Count == 0) return "No titled visible windows were found.";
        var content = string.Join(Environment.NewLine, titles.Take(60).Select(x => $"{x.Title} (PID {x.ProcessId})"));
        if (!await confirm("Share open-window titles", "The following window titles and process IDs will be sent to the configured AI provider. They may reveal private information. Continue?\n\n" + content, token))
            return "User declined to share open-window titles.";
        token.ThrowIfCancellationRequested();
        return content;
    }

    internal async Task<string> ActivateWindowAsync(string title, Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window interaction is disabled. Enable window and UI inspection in Settings first.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible external window title up to 200 characters.");
        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found.");
        if (!await confirm("Activate window",
                $"Bring this exact visible window to the foreground?\nTitle: {target.Title}\nPID: {target.ProcessId}\nHWND: 0x{target.Handle.ToInt64():X}",
                token))
            return "User declined window activation.";
        token.ThrowIfCancellationRequested();
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null || !WindowInventory.IsSameWindowTarget(target, confirmedTarget))
            throw new InvalidOperationException("The approved window changed or became ambiguous while awaiting approval. No window was activated.");
        token.ThrowIfCancellationRequested();
        return WindowInventory.ActivateWindowTarget(target)
            ? "Windows accepted the request to activate the exact approved window. Verify the foreground state manually if important."
            : "The exact approved window no longer exists or no longer matches its PID/title; no replacement window was activated.";
    }

    internal async Task<string> InspectWindowControlsAsync(
        string title,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Ask the user to enable it first.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible window title up to 200 characters.");

        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles to review available windows.");
        var approvalText = $"Read the accessible control labels and automation identifiers in '{target.Title}' (PID {target.ProcessId})? This is read-only. Editable field values and screenshots are not read, and no click or keystroke will be sent. The selected metadata will be shared with the configured AI provider and may still reveal private information.";
        if (!await confirm("Inspect application controls", approvalText, token))
            return "User declined UI inspection. No controls were read or shared.";

        token.ThrowIfCancellationRequested();
        // The target may have closed or been replaced while the user reviewed the disclosure prompt.
        // Do not read or share controls from a different window that happens to reuse the same title.
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null ||
            confirmedTarget.Handle != target.Handle ||
            confirmedTarget.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The selected window changed while awaiting approval. No controls were read or shared; inspect the target again.");

        token.ThrowIfCancellationRequested();
        var controls = await WindowsUiAutomationService.ReadControlsAsync(confirmedTarget, maxControls: 60, cancellationToken: token);
        report($"Read-only UI inspection completed for an external window (PID {target.ProcessId}); {controls.Count} eligible controls returned.");
        return WindowsUiAutomationService.FormatReport(target.Title, target.ProcessId, controls);
    }

    internal async Task<string> InvokeWindowControlAsync(
        string title,
        string controlType,
        string automationId,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Ask the user to enable it first.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible window title up to 200 characters.");

        _ = WindowsUiAutomationService.NormalizeInvokeControlType(controlType);
        automationId = WindowsUiAutomationService.ValidateAutomationId(automationId);

        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles first.");

        var preview = await WindowsUiAutomationService.PreviewInvokeControlAsync(target, controlType, automationId, token);
        var accessibleName = string.IsNullOrWhiteSpace(preview.Name) ? "(no accessible label)" : $"'{preview.Name}'";
        var approvalText =
            $"Perform exactly one UI Automation Invoke action in external window '{target.Title}' (PID {target.ProcessId})?\n" +
            $"Control: {preview.ControlType}; accessible label: {accessibleName}; AutomationId: '{preview.AutomationId}'.\n\n" +
            "This may activate a button, link, menu item, submit a form, navigate away, or change data in the target app. Confirm that the window and control are correct. " +
            "Layal will send no coordinates, typed text, or additional UI actions. A separate action requires another approval.";
        if (!await confirm("Invoke one application control", approvalText, token))
            return "User declined the UI action. No control was invoked.";

        token.ThrowIfCancellationRequested();

        // The user may have changed focus or closed a window while reviewing the approval.
        // Re-resolve the exact title and require the same native handle and process ID.
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null ||
            confirmedTarget.Handle != target.Handle ||
            confirmedTarget.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The selected window changed while awaiting approval. No UI action was performed; inspect the target again.");

        token.ThrowIfCancellationRequested();
        var outcome = await WindowsUiAutomationService.InvokeControlAsync(confirmedTarget, preview, token);
        report($"One approved UI Automation action was issued to external PID {confirmedTarget.ProcessId}; application-level success still requires observing the app.");
        return WindowsUiAutomationService.FormatInvokeReport(outcome);
    }

    internal async Task<string> FillUiTextAsync(
        string title,
        string automationId,
        string text,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Enable it in Settings before interacting with external applications.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible external window title up to 200 characters.");

        automationId = WindowsUiAutomationService.ValidateAutomationId(automationId);
        _ = WindowsUiAutomationService.ValidateUiText(text);

        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles and inspect_window_controls first.");

        var preview = await WindowsUiAutomationService.PreviewSetTextAsync(target, automationId, token);
        var nl = Environment.NewLine;
        var approvalText =
            $"Set the value of exactly one editable field in external window '{target.Title}' (PID {target.ProcessId})?{nl}" +
            $"Control type: Edit; AutomationId: '{preview.AutomationId}'; characters: {text.Length}.{nl}{nl}" +
            $"Exact text to enter (may be sensitive):{nl}{text}{nl}{nl}" +
            "Layal will not read back the existing field value, will not use password fields, and will not press Enter or submit the form. The field must remain the same unique, enabled, visible, writable control while you review this approval.";

        if (!await confirm("Enter text in application field", approvalText, token))
            return "User declined the Windows text-entry action. No field value was changed.";

        token.ThrowIfCancellationRequested();

        // The user may have closed the app or changed the selected window while reading the prompt.
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null ||
            confirmedTarget.Handle != target.Handle ||
            confirmedTarget.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The selected window changed while awaiting approval. No text was entered; inspect the target again.");

        token.ThrowIfCancellationRequested();
        var outcome = await WindowsUiAutomationService.SetTextAsync(confirmedTarget, preview, text, token);
        report($"One approved UI Automation ValuePattern.SetValue call was issued to external PID {outcome.ProcessId}; the application-level result is not confirmed.");
        return WindowsUiAutomationService.FormatTextEntryReport(outcome);
    }

    internal async Task<string> SetUiCheckboxAsync(
        string title,
        string automationId,
        bool shouldBeChecked,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Enable it in Settings before interacting with external applications.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible external window title up to 200 characters.");

        automationId = WindowsUiAutomationService.ValidateAutomationId(automationId);
        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles and inspect_window_controls first.");

        var preview = await WindowsUiAutomationService.PreviewSetCheckBoxAsync(target, automationId, token);
        var requested = shouldBeChecked ? "checked (On)" : "unchecked (Off)";
        var current = preview.IsChecked ? "checked (On)" : "unchecked (Off)";
        var approvalText =
            $"Set one checkbox in external window '{target.Title}' (PID {target.ProcessId}) to {requested}?\n" +
            $"AutomationId: '{preview.AutomationId}'. Current observed state: {current}.\n\n" +
            "Layal will target only this exact CheckBox. It will reject ambiguous, hidden, disabled, changed, or indeterminate controls. It will not send coordinates or keyboard input. If the checkbox already has the requested state, no toggle is needed.";

        if (!await confirm("Set application checkbox", approvalText, token))
            return "User declined the Windows checkbox action. No state change was requested.";

        token.ThrowIfCancellationRequested();
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null || confirmedTarget.Handle != target.Handle || confirmedTarget.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The selected window changed while awaiting approval. No checkbox action was performed; inspect the target again.");

        token.ThrowIfCancellationRequested();
        var outcome = await WindowsUiAutomationService.SetCheckBoxAsync(confirmedTarget, preview, shouldBeChecked, token);
        report($"One approved TogglePattern state-setting action was issued to external PID {outcome.ProcessId}; verify the application's resulting state.");
        return WindowsUiAutomationService.FormatCheckBoxReport(outcome);
    }
}
