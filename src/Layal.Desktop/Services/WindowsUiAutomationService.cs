using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Layal.Services;

/// <summary>
/// Read-only UI Automation for a specifically selected external top-level window.
/// This service deliberately never reads ValuePattern/TextPattern content or invokes controls.
/// </summary>
public static class WindowsUiAutomationService
{
    private const int MaxWindowTitleLength = 500;
    private const int MaxNodesExamined = 400;

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

/// <summary>Metadata-only UI control summary; intentionally has no property for a control's value.</summary>
public sealed record UiControlSummary(string ControlType, string Name, string AutomationId, bool IsEnabled);
