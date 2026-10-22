using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Layal.Services;

/// <summary>
/// Bounded UI Automation for a specifically selected external top-level window.
/// Inspection never reads editable values; action support is limited to one approved InvokePattern call.
/// </summary>
public static class WindowsUiAutomationService
{
    private const int MaxWindowTitleLength = 500;
    private const int MaxNodesExamined = 400;
    private static readonly SemaphoreSlim AutomationGate = new(1, 1);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);

    private static readonly HashSet<string> AllowedControlTypes = new(StringComparer.Ordinal)
    {
        ControlType.Button.ProgrammaticName,
        ControlType.CheckBox.ProgrammaticName,
        ControlType.RadioButton.ProgrammaticName,
        ControlType.TabItem.ProgrammaticName,
        ControlType.MenuItem.ProgrammaticName,
        ControlType.Hyperlink.ProgrammaticName,
        ControlType.SplitButton.ProgrammaticName,
        ControlType.ComboBox.ProgrammaticName,
        ControlType.Edit.ProgrammaticName,
        ControlType.Slider.ProgrammaticName,
        ControlType.Spinner.ProgrammaticName,
        ControlType.Tab.ProgrammaticName,
        ControlType.List.ProgrammaticName,
        ControlType.Tree.ProgrammaticName
    };

    // Names on edit/selection controls may contain user data; return IDs and control types only for them.
    private static readonly HashSet<string> SafeNamedControlTypes = new(StringComparer.Ordinal)
    {
        ControlType.Button.ProgrammaticName,
        ControlType.CheckBox.ProgrammaticName,
        ControlType.RadioButton.ProgrammaticName,
        ControlType.TabItem.ProgrammaticName,
        ControlType.MenuItem.ProgrammaticName,
        ControlType.Hyperlink.ProgrammaticName,
        ControlType.SplitButton.ProgrammaticName
    };

    public static UiWindowTarget? FindVisibleWindowByExactTitle(string exactTitle)
    {
        if (string.IsNullOrWhiteSpace(exactTitle) || exactTitle.Length > 200)
            throw new ArgumentException("A non-empty exact window title up to 200 characters is required.", nameof(exactTitle));
        EnsureWindows();

        IntPtr match = IntPtr.Zero;
        uint matchedProcessId = 0;
        var matchedTitle = string.Empty;
        var ambiguous = false;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var length = GetWindowTextLength(hWnd);
            if (length <= 0 || length > MaxWindowTitleLength) return true;

            var buffer = new StringBuilder(length + 1);
            _ = GetWindowText(hWnd, buffer, buffer.Capacity);
            var candidateTitle = buffer.ToString().Trim();
            if (!string.Equals(candidateTitle, exactTitle, StringComparison.Ordinal)) return true;

            GetWindowThreadProcessId(hWnd, out var processId);
            if (processId == 0 || processId == Environment.ProcessId) return true;

            if (match != IntPtr.Zero)
            {
                ambiguous = true;
                return false;
            }

            match = hWnd;
            matchedProcessId = processId;
            matchedTitle = candidateTitle;
            return true;
        }, IntPtr.Zero);

        if (ambiguous)
            throw new InvalidOperationException("More than one visible external window has that exact title. Choose a window with a unique title.");
        return match == IntPtr.Zero ? null : new UiWindowTarget(match, matchedTitle, matchedProcessId);
    }

    /// <summary>Runs read-only inspection away from the WPF UI thread and bounds the caller wait.</summary>
    public static Task<IReadOnlyList<UiControlSummary>> ReadControlsAsync(
        UiWindowTarget target,
        int maxControls = 60,
        CancellationToken cancellationToken = default) =>
        RunBoundedAutomationAsync(
            () => ReadControls(target, maxControls),
            cancellationToken,
            "Windows UI Automation did not respond within 8 seconds. No UI action was performed.");

    /// <summary>Returns a local preview of an exact supported control, without invoking it.</summary>
    public static Task<UiInvokePreview> PreviewInvokeControlAsync(
        UiWindowTarget target,
        string controlType,
        string automationId,
        CancellationToken cancellationToken = default) =>
        RunBoundedAutomationAsync(
            () => PreviewInvokeControl(target, controlType, automationId),
            cancellationToken,
            "UI Automation did not identify the requested control within 8 seconds. No UI action was performed.");

    /// <summary>Invokes one exact control and performs a bounded, metadata-only follow-up observation.</summary>
    public static Task<UiInvokeOutcome> InvokeControlAsync(
        UiWindowTarget target,
        string controlType,
        string automationId,
        CancellationToken cancellationToken = default) =>
        RunBoundedAutomationAsync(
            () => InvokeControl(target, controlType, automationId),
            cancellationToken,
            "Waiting for the UI action exceeded 8 seconds. The action may have been delivered; inspect the target application before retrying. Layal will not queue a second UI Automation operation until the original operation exits.");

    /// <summary>Previews one editable non-password field without reading its current value.</summary>
    public static Task<UiTextEntryPreview> PreviewSetTextAsync(
        UiWindowTarget target,
        string automationId,
        CancellationToken cancellationToken = default) =>
        RunBoundedAutomationAsync(
            () => PreviewSetText(target, automationId),
            cancellationToken,
            "Windows UI Automation could not safely inspect the text field within 8 seconds. No UI action was performed.");

    /// <summary>Sets the value of the exact field approved by the user; it never reads or returns the old value.</summary>
    public static Task<UiTextEntryOutcome> SetTextAsync(
        UiWindowTarget target,
        UiTextEntryPreview preview,
        string value,
        CancellationToken cancellationToken = default) =>
        RunBoundedAutomationAsync(
            () => SetText(target, preview, value),
            cancellationToken,
            "Waiting for the UI text action exceeded 8 seconds. The value may have been set; inspect the target application before retrying. Layal will not queue another UI Automation operation until the original operation exits.");

    public static string ValidateUiText(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.Length > 10_000)
            throw new ArgumentException("Text entry is limited to 10,000 characters.", nameof(value));
        if (value.Contains('\0'))
            throw new ArgumentException("Text entry cannot contain a null character.", nameof(value));
        return value;
    }

    private static UiTextEntryPreview PreviewSetText(UiWindowTarget target, string automationId)
    {
        automationId = ValidateAutomationId(automationId);
        var element = FindUniqueEditableControl(target, automationId);
        var info = element.Current;
        if (info.IsPassword)
            throw new InvalidOperationException("Password fields are not supported; no value was set.");
        if (info.IsOffscreen)
            throw new InvalidOperationException("The selected field is offscreen. Bring it into view and inspect again.");
        if (!info.IsEnabled)
            throw new InvalidOperationException("The selected field is disabled.");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern valuePattern)
            throw new InvalidOperationException("The selected field does not expose ValuePattern. No value was set.");
        if (valuePattern.Current.IsReadOnly)
            throw new InvalidOperationException("The selected field is read-only. No value was set.");

        var runtimeId = string.Join(",", element.GetRuntimeId().Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (string.IsNullOrWhiteSpace(runtimeId))
            throw new InvalidOperationException("Windows UI Automation did not provide a stable runtime identifier for the selected field. No value was set.");
        return new UiTextEntryPreview(target.Title, target.ProcessId, automationId, runtimeId, info.IsEnabled, valuePattern.Current.IsReadOnly);
    }

    private static UiTextEntryOutcome SetText(UiWindowTarget target, UiTextEntryPreview preview, string value)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _ = ValidateUiText(value);
        if (!string.Equals(preview.WindowTitle, target.Title, StringComparison.Ordinal) ||
            preview.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The approved text-field target does not match the selected window. No value was set.");

        var element = FindUniqueEditableControl(target, preview.AutomationId);
        var info = element.Current;
        var currentRuntimeId = string.Join(",", element.GetRuntimeId().Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (string.IsNullOrWhiteSpace(currentRuntimeId) || string.IsNullOrWhiteSpace(preview.RuntimeId) ||
            !string.Equals(currentRuntimeId, preview.RuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException("The text field changed while awaiting approval. Inspect the application again; no value was set.");
        if (info.IsPassword || info.IsOffscreen || !info.IsEnabled)
            throw new InvalidOperationException("The field became protected, offscreen, or disabled while awaiting approval. No value was set.");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern valuePattern)
            throw new InvalidOperationException("The field no longer exposes ValuePattern. No value was set.");
        if (valuePattern.Current.IsReadOnly)
            throw new InvalidOperationException("The field became read-only while awaiting approval. No value was set.");

        valuePattern.SetValue(value);
        return new UiTextEntryOutcome(target.Title, target.ProcessId, preview.AutomationId, value.Length);
    }

    private static AutomationElement FindUniqueEditableControl(UiWindowTarget target, string automationId)
    {
        var root = GetRootForExternalWindow(target);
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<AutomationElement>();
        var matches = new List<AutomationElement>(2);
        var examined = 0;
        var treeIncomplete = false;

        try
        {
            var first = walker.GetFirstChild(root);
            if (first is not null) pending.Push(first);
        }
        catch (ElementNotAvailableException)
        {
            throw new InvalidOperationException("The target UI tree changed and could not be safely inspected. No text was entered.");
        }
        catch (COMException)
        {
            throw new InvalidOperationException("Windows UI Automation could not access the target UI tree. No text was entered.");
        }

        while (pending.Count > 0 && examined < MaxNodesExamined)
        {
            var element = pending.Pop();
            examined++;
            try
            {
                var sibling = walker.GetNextSibling(element);
                if (sibling is not null) pending.Push(sibling);
                var child = walker.GetFirstChild(element);
                if (child is not null) pending.Push(child);

                var info = element.Current;
                var actualType = info.ControlType?.ProgrammaticName ?? string.Empty;
                if (!info.IsControlElement || !string.Equals(actualType, ControlType.Edit.ProgrammaticName, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(info.AutomationId, automationId, StringComparison.Ordinal))
                    continue;

                // Keep password controls in the match set so a protected control with this ID
                // cannot be silently ignored in favor of another similarly named field.
                matches.Add(element);
                if (matches.Count > 1)
                    throw new InvalidOperationException("More than one text field has this exact AutomationId. No value was set.");
            }
            catch (InvalidOperationException) { throw; }
            catch (ElementNotAvailableException) { treeIncomplete = true; }
            catch (COMException) { treeIncomplete = true; }
        }

        if (pending.Count > 0 || examined >= MaxNodesExamined)
            throw new InvalidOperationException("The target UI tree exceeds the 400-element safety limit. Refusing to enter text without proving the target is unique.");
        if (treeIncomplete)
            throw new InvalidOperationException("The target UI tree changed or was partially unavailable. Refusing to enter text into a field whose uniqueness cannot be proven.");
        if (matches.Count == 0)
            throw new InvalidOperationException("No text field with this exact AutomationId was found in the selected window.");
        return matches[0];
    }

    public static string NormalizeInvokeControlType(string controlType)
    {
        if (string.IsNullOrWhiteSpace(controlType) || controlType.Length > 40)
            throw new ArgumentException("Choose a supported control type: Button, Hyperlink, MenuItem, or SplitButton.", nameof(controlType));

        return controlType.Trim() switch
        {
            "Button" => ControlType.Button.ProgrammaticName,
            "Hyperlink" => ControlType.Hyperlink.ProgrammaticName,
            "MenuItem" => ControlType.MenuItem.ProgrammaticName,
            "SplitButton" => ControlType.SplitButton.ProgrammaticName,
            _ => throw new ArgumentException("Only Button, Hyperlink, MenuItem, and SplitButton controls can be invoked.", nameof(controlType))
        };
    }

    public static string ValidateAutomationId(string automationId)
    {
        if (string.IsNullOrWhiteSpace(automationId))
            throw new ArgumentException("A non-empty exact AutomationId is required.", nameof(automationId));
        if (automationId.Length > 100)
            throw new ArgumentException("AutomationId must not exceed 100 characters.", nameof(automationId));
        if (automationId.Any(char.IsControl))
            throw new ArgumentException("AutomationId cannot contain control characters.", nameof(automationId));
        return automationId.Trim();
    }

    private static async Task<T> RunBoundedAutomationAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken,
        string timeoutMessage)
    {
        if (!await AutomationGate.WaitAsync(TimeSpan.Zero, cancellationToken))
            throw new InvalidOperationException("A previous Windows UI Automation operation is still running. Wait for it to finish before retrying.");

        Task<T> worker;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            worker = Task.Run(operation, CancellationToken.None);
        }
        catch
        {
            AutomationGate.Release();
            throw;
        }

        // Keep the gate held if native UI Automation is still blocked after the caller times out.
        // Observe late faults so they do not become unobserved task exceptions.
        _ = worker.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                AutomationGate.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            return await worker.WaitAsync(OperationTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    private static UiInvokePreview PreviewInvokeControl(UiWindowTarget target, string controlType, string automationId)
    {
        var programmaticType = NormalizeInvokeControlType(controlType);
        automationId = ValidateAutomationId(automationId);
        var element = FindUniqueActionControl(target, programmaticType, automationId);
        var info = element.Current;

        if (info.IsPassword)
            throw new InvalidOperationException("Password controls cannot be invoked through this tool.");
        if (info.IsOffscreen)
            throw new InvalidOperationException("The selected control is offscreen. Bring it into view and inspect again.");
        if (!info.IsEnabled)
            throw new InvalidOperationException("The selected control is disabled.");
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || pattern is not InvokePattern)
            throw new InvalidOperationException("The selected control does not expose the UI Automation InvokePattern. No action was performed.");

        return new UiInvokePreview(
            target.Title,
            target.ProcessId,
            DisplayControlType(programmaticType),
            SafeNamedControlTypesForActions.Contains(programmaticType) ? Sanitize(info.Name, 100) : string.Empty,
            automationId,
            info.IsEnabled);
    }

    private static UiInvokeOutcome InvokeControl(UiWindowTarget target, string controlType, string automationId)
    {
        var preview = PreviewInvokeControl(target, controlType, automationId);
        var programmaticType = NormalizeInvokeControlType(controlType);
        var element = FindUniqueActionControl(target, programmaticType, preview.AutomationId);
        var info = element.Current;

        if (!info.IsEnabled || info.IsOffscreen || info.IsPassword)
            throw new InvalidOperationException("The selected control became disabled, offscreen, or protected before invocation. No action was performed.");
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || pattern is not InvokePattern invokePattern)
            throw new InvalidOperationException("The selected control no longer exposes InvokePattern. No action was performed.");

        invokePattern.Invoke();
        Thread.Sleep(250);

        var windowStillVisible = IsTargetWindowStillVisible(target);
        bool? controlStillPresent = null;
        bool? controlStillEnabled = null;
        if (windowStillVisible)
        {
            try
            {
                var after = FindUniqueActionControl(target, programmaticType, preview.AutomationId);
                var afterInfo = after.Current;
                controlStillPresent = true;
                controlStillEnabled = afterInfo.IsEnabled;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("No matching UI control with this type and AutomationId was found", StringComparison.Ordinal))
            {
                controlStillPresent = false;
            }
            catch (InvalidOperationException)
            {
                // An ambiguous or incomplete UI tree is not evidence that the control disappeared.
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
        }

        return new UiInvokeOutcome(
            target.Title,
            target.ProcessId,
            preview.ControlType,
            preview.Name,
            preview.AutomationId,
            windowStillVisible,
            controlStillPresent,
            controlStillEnabled);
    }

    private static AutomationElement FindUniqueActionControl(
        UiWindowTarget target,
        string expectedProgrammaticType,
        string automationId)
    {
        var root = GetRootForExternalWindow(target);
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<AutomationElement>();
        var matches = new List<AutomationElement>(2);
        var examined = 0;
        var treeIncomplete = false;

        try
        {
            var first = walker.GetFirstChild(root);
            if (first is not null) pending.Push(first);
        }
        catch (ElementNotAvailableException)
        {
            throw new InvalidOperationException("The target UI tree changed and could not be safely inspected. No action was performed.");
        }
        catch (COMException)
        {
            throw new InvalidOperationException("Windows UI Automation could not access the target UI tree. No action was performed.");
        }

        while (pending.Count > 0 && examined < MaxNodesExamined)
        {
            var element = pending.Pop();
            examined++;

            try
            {
                var sibling = walker.GetNextSibling(element);
                if (sibling is not null) pending.Push(sibling);
                var child = walker.GetFirstChild(element);
                if (child is not null) pending.Push(child);

                var info = element.Current;
                if (!info.IsControlElement || info.IsPassword) continue;
                var actualType = info.ControlType?.ProgrammaticName ?? string.Empty;
                if (!string.Equals(actualType, expectedProgrammaticType, StringComparison.Ordinal)) continue;
                if (!string.Equals(info.AutomationId, automationId, StringComparison.Ordinal)) continue;

                matches.Add(element);
                if (matches.Count > 1)
                    throw new InvalidOperationException("More than one control has this exact AutomationId and type. No action was performed.");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (ElementNotAvailableException)
            {
                treeIncomplete = true;
            }
            catch (COMException)
            {
                treeIncomplete = true;
            }
        }

        if (pending.Count > 0 || examined >= MaxNodesExamined)
            throw new InvalidOperationException("The target UI tree exceeds the 400-element safety limit. Refusing to invoke a control without proving it is unique.");
        if (treeIncomplete)
            throw new InvalidOperationException("The target UI tree changed or was partially unavailable. Refusing to invoke a control whose uniqueness cannot be proven.");
        if (matches.Count == 0)
            throw new InvalidOperationException("No matching UI control with this type and AutomationId was found in the selected window.");

        return matches[0];
    }

    private static AutomationElement GetRootForExternalWindow(UiWindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureWindows();

        if (!IsWindow(target.Handle) || !IsWindowVisible(target.Handle))
            throw new InvalidOperationException("The selected external window has closed or is no longer visible.");

        GetWindowThreadProcessId(target.Handle, out var actualProcessId);
        if (actualProcessId == 0 || actualProcessId != target.ProcessId || actualProcessId == Environment.ProcessId)
            throw new InvalidOperationException("The selected window changed or is not an external application. Select it again.");

        var currentTitle = GetNativeWindowTitle(target.Handle);
        if (!string.Equals(currentTitle, target.Title, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected window title changed. Inspect the target again before acting.");

        return AutomationElement.FromHandle(target.Handle);
    }

    private static bool IsTargetWindowStillVisible(UiWindowTarget target)
    {
        if (!IsWindow(target.Handle) || !IsWindowVisible(target.Handle)) return false;
        GetWindowThreadProcessId(target.Handle, out var actualProcessId);
        if (actualProcessId == 0 || actualProcessId != target.ProcessId || actualProcessId == Environment.ProcessId) return false;
        return string.Equals(GetNativeWindowTitle(target.Handle), target.Title, StringComparison.Ordinal);
    }

    private static string GetNativeWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0 || length > MaxWindowTitleLength) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string DisplayControlType(string programmaticType) =>
        programmaticType.StartsWith("ControlType.", StringComparison.Ordinal)
            ? programmaticType["ControlType.".Length..]
            : programmaticType;

    public static IReadOnlyList<UiControlSummary> ReadControls(UiWindowTarget target, int maxControls = 60)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (maxControls is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxControls), "Control limit must be between 1 and 100.");
        EnsureWindows();

        if (!IsWindow(target.Handle) || !IsWindowVisible(target.Handle))
            throw new InvalidOperationException("The selected window has closed or is no longer visible.");

        GetWindowThreadProcessId(target.Handle, out var actualProcessId);
        if (actualProcessId == 0 || actualProcessId != target.ProcessId || actualProcessId == Environment.ProcessId)
            throw new InvalidOperationException("The selected window changed or is not an external application. Select it again.");

        var root = AutomationElement.FromHandle(target.Handle);
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<AutomationElement>();
        var controls = new List<UiControlSummary>(Math.Min(maxControls, 60));
        var examined = 0;

        try
        {
            var first = walker.GetFirstChild(root);
            if (first is not null) pending.Push(first);
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }

        while (pending.Count > 0 && examined < MaxNodesExamined && controls.Count < maxControls)
        {
            var element = pending.Pop();
            examined++;

            try
            {
                var sibling = walker.GetNextSibling(element);
                if (sibling is not null) pending.Push(sibling);
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }

            try
            {
                var child = walker.GetFirstChild(element);
                if (child is not null) pending.Push(child);
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }

            try
            {
                var info = element.Current;
                if (!info.IsControlElement || info.IsOffscreen || info.IsPassword) continue;

                var programmaticType = info.ControlType?.ProgrammaticName ?? string.Empty;
                if (!AllowedControlTypes.Contains(programmaticType)) continue;

                var name = SafeNamedControlTypes.Contains(programmaticType)
                    ? Sanitize(info.Name, 100)
                    : string.Empty;
                var automationId = Sanitize(info.AutomationId, 100);
                if (name.Length == 0 && automationId.Length == 0) continue;

                controls.Add(new UiControlSummary(
                    programmaticType.Replace("ControlType.", string.Empty, StringComparison.Ordinal),
                    name,
                    automationId,
                    info.IsEnabled));
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
        }

        return controls;
    }

    private static readonly HashSet<string> SafeNamedControlTypesForActions = new(StringComparer.Ordinal)
    {
        ControlType.Button.ProgrammaticName,
        ControlType.Hyperlink.ProgrammaticName,
        ControlType.MenuItem.ProgrammaticName,
        ControlType.SplitButton.ProgrammaticName
    };

    public static string FormatTextEntryReport(UiTextEntryOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var output = new StringBuilder();
        output.AppendLine($"Window: {Sanitize(outcome.WindowTitle, 200)} (PID {outcome.ProcessId})");
        output.AppendLine($"ValuePattern.SetValue returned for one approved Edit field (AutomationId: {Sanitize(outcome.AutomationId, 100)}).");
        output.AppendLine($"Characters requested: {outcome.CharacterCount}.");
        output.Append("The previous field value was not read or returned. This does not prove the application accepted the value or submitted a form; verify the result in the target application.");
        return output.ToString();
    }

    public static string FormatInvokeReport(UiInvokeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var output = new StringBuilder();
        output.AppendLine($"Window: {Sanitize(outcome.WindowTitle, 200)} (PID {outcome.ProcessId})");
        output.AppendLine($"Invoked one {Sanitize(outcome.ControlType, 40)} control (AutomationId: {Sanitize(outcome.AutomationId, 100)}).");
        output.AppendLine("The InvokePattern call returned without throwing; this does not by itself prove the application's requested operation succeeded.");

        if (!outcome.WindowStillVisible)
        {
            output.Append("Observed after 250 ms: the selected window was no longer visible with the same title and process. Check the resulting application state.");
        }
        else if (outcome.ControlStillPresent == false)
        {
            output.Append("Observed after 250 ms: the target control was no longer found in the accessible UI tree. The UI changed, but the intended business result is not proven.");
        }
        else if (outcome.ControlStillPresent == true)
        {
            output.Append("Observed after 250 ms: the target control remained in the accessible UI tree; enabled state after the action: ")
                .Append(outcome.ControlStillEnabled is true ? "enabled." : outcome.ControlStillEnabled is false ? "disabled." : "unknown.");
            output.Append(" Verify the application's result before continuing.");
        }
        else
        {
            output.Append("Observed after 250 ms: the target UI state could not be determined safely. Verify the application manually before continuing.");
        }

        return output.ToString();
    }

    public static string FormatReport(string windowTitle, uint processId, IReadOnlyList<UiControlSummary> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var output = new StringBuilder();
        output.AppendLine($"Window: {Sanitize(windowTitle, 200)} (PID {processId})");
        output.AppendLine("Read-only UI metadata. No editable field values, screenshots, clicks, or keystrokes are included.");

        if (controls.Count == 0)
        {
            output.Append("No eligible visible controls with accessible names or automation IDs were found.");
            return output.ToString();
        }

        foreach (var control in controls)
        {
            output.Append("- ").Append(Sanitize(control.ControlType, 40));
            if (!string.IsNullOrEmpty(Sanitize(control.Name, 100)))
                output.Append(" | Name: ").Append(Sanitize(control.Name, 100));
            if (!string.IsNullOrEmpty(Sanitize(control.AutomationId, 100)))
                output.Append(" | AutomationId: ").Append(Sanitize(control.AutomationId, 100));
            output.Append(" | Enabled: ").AppendLine(control.IsEnabled ? "yes" : "no");
        }

        return output.ToString().TrimEnd();
    }

    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows UI Automation is available only on Windows.");
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

public sealed record UiWindowTarget(IntPtr Handle, string Title, uint ProcessId);

public sealed record UiInvokePreview(
    string WindowTitle,
    uint ProcessId,
    string ControlType,
    string Name,
    string AutomationId,
    bool IsEnabled);

public sealed record UiInvokeOutcome(
    string WindowTitle,
    uint ProcessId,
    string ControlType,
    string Name,
    string AutomationId,
    bool WindowStillVisible,
    bool? ControlStillPresent,
    bool? ControlStillEnabled);

public sealed record UiTextEntryPreview(
    string WindowTitle,
    uint ProcessId,
    string AutomationId,
    string RuntimeId,
    bool IsEnabled,
    bool IsReadOnly);

public sealed record UiTextEntryOutcome(
    string WindowTitle,
    uint ProcessId,
    string AutomationId,
    int CharacterCount);

/// <summary>Metadata-only UI control summary; intentionally has no property for a control's value.</summary>
public sealed record UiControlSummary(string ControlType, string Name, string AutomationId, bool IsEnabled);
