using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class WindowsUiAutomationServiceTests
{
    [TestMethod]
    public void ReportContainsOnlyControlMetadataAndExplicitReadOnlyLimitations()
    {
        var controls = new[]
        {
            new UiControlSummary("Button", "Save", "saveButton", true),
            new UiControlSummary("Edit", string.Empty, "searchInput", true),
            new UiControlSummary("CheckBox", "Enable option", "enableOption", false)
        };

        var report = WindowsUiAutomationService.FormatReport("Test Editor", 4242, controls);

        StringAssert.Contains(report, "Window: Test Editor (PID 4242)");
        StringAssert.Contains(report, "Button | Name: Save | AutomationId: saveButton | Enabled: yes");
        StringAssert.Contains(report, "Edit | AutomationId: searchInput | Enabled: yes");
        StringAssert.Contains(report, "CheckBox | Name: Enable option | AutomationId: enableOption | Enabled: no");
        StringAssert.Contains(report, "No editable field values");
        Assert.IsFalse(report.Contains("private entered text", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReportSanitizesControlMetadataToPreventForgedLines()
    {
        var report = WindowsUiAutomationService.FormatReport(
            "Editor\nForged title",
            11,
            new[] { new UiControlSummary("Button\r\nForged type", "Save\nForged name", "save\rForgedId", true) });

        Assert.IsFalse(report.Contains(Environment.NewLine + "Forged", StringComparison.Ordinal));
        StringAssert.Contains(report, "EditorForged title");
        StringAssert.Contains(report, "ButtonForged type");
        StringAssert.Contains(report, "SaveForged name");
        StringAssert.Contains(report, "saveForgedId");
    }

    [TestMethod]
    public void UiTextValidationAllowsOrdinaryAndMultilineTextWithoutReadingAnyField()
    {
        Assert.AreEqual("Layal test", WindowsUiAutomationService.ValidateUiText("Layal test"));
        Assert.AreEqual("Arabic: مرحبًا\nEnglish: hello", WindowsUiAutomationService.ValidateUiText("Arabic: مرحبًا\nEnglish: hello"));
        Assert.AreEqual(string.Empty, WindowsUiAutomationService.ValidateUiText(string.Empty));
        Assert.AreEqual(10_000, WindowsUiAutomationService.ValidateUiText(new string('x', 10_000)).Length);
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.ValidateUiText(new string('x', 10_001)));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.ValidateUiText("bad\0value"));
    }

    [TestMethod]
    public void CheckBoxReportStatesObservedStateWithoutClaimingApplicationSuccess()
    {
        var report = WindowsUiAutomationService.FormatCheckBoxReport(
            new UiCheckBoxOutcome("Test App", 4242, "enableOption", WasChanged: true, IsCheckedAfter: true));

        StringAssert.Contains(report, "Checkbox AutomationId: enableOption");
        StringAssert.Contains(report, "One TogglePattern action was issued");
        StringAssert.Contains(report, "checked (On)");
        StringAssert.Contains(report, "does not prove that the application completed");
        Assert.IsFalse(report.Contains("application operation succeeded", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TextEntryReportDoesNotExposeTheEnteredValueOrClaimApplicationSuccess()
    {
        const string sensitiveValue = "private-value-should-not-be-returned";
        var outcome = new UiTextEntryOutcome("Test Editor", 4242, "searchInput", sensitiveValue.Length);

        var report = WindowsUiAutomationService.FormatTextEntryReport(outcome);

        StringAssert.Contains(report, "AutomationId: searchInput");
        StringAssert.Contains(report, $"Characters requested: {sensitiveValue.Length}");
        StringAssert.Contains(report, "previous field value was not read or returned");
        StringAssert.Contains(report, "does not prove the application accepted the value");
        Assert.IsFalse(report.Contains(sensitiveValue, StringComparison.Ordinal));
        Assert.IsFalse(report.Contains("application operation succeeded", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ReadControlsRejectsOutOfRangeLimitsBeforeNativeCalls()
    {
        var target = new UiWindowTarget(IntPtr.Zero, "unused", 1);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            WindowsUiAutomationService.ReadControls(target, maxControls: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            WindowsUiAutomationService.ReadControls(target, maxControls: 101));
    }

    [TestMethod]
    public void WindowLookupRejectsEmptyTitleBeforeNativeCalls()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WindowsUiAutomationService.FindVisibleWindowByExactTitle(" "));
    }
    [TestMethod]
    public void InvokeControlSupportsOnlyExplicitlyAllowedActionTypes()
    {
        Assert.AreEqual("ControlType.Button", WindowsUiAutomationService.NormalizeInvokeControlType("Button"));
        Assert.AreEqual("ControlType.Hyperlink", WindowsUiAutomationService.NormalizeInvokeControlType("Hyperlink"));
        Assert.AreEqual("ControlType.MenuItem", WindowsUiAutomationService.NormalizeInvokeControlType("MenuItem"));
        Assert.AreEqual("ControlType.SplitButton", WindowsUiAutomationService.NormalizeInvokeControlType("SplitButton"));

        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.NormalizeInvokeControlType("Edit"));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.NormalizeInvokeControlType("Text"));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.NormalizeInvokeControlType("CheckBox"));
    }


    [TestMethod]
    public void ApprovedInvokeTargetMustMatchTheSameWindowControlAndRuntimeId()
    {
        var window = new UiWindowTarget(new IntPtr(123), "Approved App", 4242);
        var preview = new UiInvokePreview(
            new IntPtr(123), "Approved App", 4242, "Button", "Save", "saveButton", true, "7,8,9");

        Assert.IsTrue(WindowsUiAutomationService.MatchesApprovedInvokeTarget(
            preview, window, "7,8,9", "Save", isEnabled: true));
        Assert.IsFalse(WindowsUiAutomationService.MatchesApprovedInvokeTarget(
            preview, new UiWindowTarget(new IntPtr(124), "Approved App", 4242), "7,8,9", "Save", isEnabled: true),
            "A different HWND in the same process/title must not inherit approval.");
        Assert.IsTrue(WindowsUiAutomationService.MatchesApprovedWindowTarget(
            "Approved App", 4242, new IntPtr(123), window));
        Assert.IsFalse(WindowsUiAutomationService.MatchesApprovedWindowTarget(
            "Approved App", 4242, new IntPtr(124), window));
        Assert.IsFalse(WindowsUiAutomationService.MatchesApprovedInvokeTarget(
            preview, window, "7,8,10", "Save", isEnabled: true),
            "A replacement control with the same AutomationId must not inherit approval.");
        Assert.IsFalse(WindowsUiAutomationService.MatchesApprovedInvokeTarget(
            preview, new UiWindowTarget(new IntPtr(124), "Approved App", 4242), "7,8,9", "Save", isEnabled: true));
        Assert.IsFalse(WindowsUiAutomationService.MatchesApprovedInvokeTarget(
            preview, new UiWindowTarget(new IntPtr(123), "Approved App", 5000), "7,8,9", "Save", isEnabled: true));
        Assert.IsFalse(WindowsUiAutomationService.MatchesApprovedInvokeTarget(
            preview, window, "7,8,9", "Delete", isEnabled: true));
        Assert.IsFalse(WindowsUiAutomationService.MatchesApprovedInvokeTarget(
            preview, window, "7,8,9", "Save", isEnabled: false));
    }

    [TestMethod]
    public void ApprovedWindowIdentityRequiresSameHandleProcessAndTitle()
    {
        var approved = new UiWindowTarget(new IntPtr(100), "Test Window", 4242);
        Assert.IsTrue(WindowInventory.IsSameWindowTarget(
            approved, new UiWindowTarget(new IntPtr(100), "Test Window", 4242)));
        Assert.IsFalse(WindowInventory.IsSameWindowTarget(
            approved, new UiWindowTarget(new IntPtr(101), "Test Window", 4242)));
        Assert.IsFalse(WindowInventory.IsSameWindowTarget(
            approved, new UiWindowTarget(new IntPtr(100), "Test Window", 4243)));
        Assert.IsFalse(WindowInventory.IsSameWindowTarget(
            approved, new UiWindowTarget(new IntPtr(100), "Different Window", 4242)));
    }

    [TestMethod]
    public void AutomationIdMustBeNonEmptyBoundedAndFreeOfControlCharacters()
    {
        Assert.AreEqual("saveButton", WindowsUiAutomationService.ValidateAutomationId("saveButton"));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.ValidateAutomationId(" "));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.ValidateAutomationId(new string('x', 101)));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.ValidateAutomationId("save\nButton"));
    }

    [TestMethod]
    public void InvokeReportNeverClaimsApplicationLevelSuccessFromInvocationAlone()
    {
        var report = WindowsUiAutomationService.FormatInvokeReport(new UiInvokeOutcome(
            "Test App", 4242, "Button", "Save", "saveButton", true, true, true));

        StringAssert.Contains(report, "InvokePattern call returned without throwing");
        StringAssert.Contains(report, "does not by itself prove");
        StringAssert.Contains(report, "Verify the application's result");
        Assert.IsFalse(report.Contains("Application operation succeeded", StringComparison.Ordinal));
    }


    [TestMethod]
    public async Task HoverInspectionHonorsPreCancelledTokenBeforeNativeUiAutomation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var wasCancelled = false;
        try
        {
            await WindowsUiAutomationService.InspectElementAtPointAsync(
                screenX: 0,
                screenY: 0,
                excludedProcessId: Environment.ProcessId,
                cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }

        Assert.IsTrue(wasCancelled, "A pre-cancelled hover inspection must stop before querying Windows UI Automation.");
    }

    [TestMethod]
    public void HoverInspectionExposesNamesOnlyForNonValueBearingActionControls()
    {
        Assert.IsTrue(WindowsUiAutomationService.CanExposeElementName("ControlType.Button", isPassword: false));
        Assert.IsTrue(WindowsUiAutomationService.CanExposeElementName("Hyperlink", isPassword: false));
        Assert.IsTrue(WindowsUiAutomationService.CanExposeElementName("ControlType.CheckBox", isPassword: false));
        Assert.IsFalse(WindowsUiAutomationService.CanExposeElementName("ControlType.Edit", isPassword: false));
        Assert.IsFalse(WindowsUiAutomationService.CanExposeElementName("ControlType.Text", isPassword: false));
        Assert.IsFalse(WindowsUiAutomationService.CanExposeElementName("ControlType.Document", isPassword: false));
        Assert.IsFalse(WindowsUiAutomationService.CanExposeElementName("ControlType.ListItem", isPassword: false));
        Assert.IsFalse(WindowsUiAutomationService.CanExposeElementName("ControlType.Button", isPassword: true));
        Assert.IsFalse(WindowsUiAutomationService.CanExposeElementName(" ", isPassword: false));
    }

    [TestMethod]
    public void HoverInspectionDraftContainsOnlyBoundedMetadataAndSafeActionName()
    {
        var inspection = new UiElementInspection(
            "ControlType.Button", "button", "Save", "saveButton",
            IsEnabled: true, IsPassword: false,
            X: -1920, Y: 40, Width: 120, Height: 32);

        var report = WindowsUiAutomationService.FormatHoveredElementDraft(inspection, isArabic: false);

        StringAssert.Contains(report, "Control type: button");
        StringAssert.Contains(report, "Name: Save");
        StringAssert.Contains(report, "AutomationId: saveButton");
        StringAssert.Contains(report, "Enabled: yes");
        StringAssert.Contains(report, "Screen bounds (pixels): -1920,40 120×32");
        StringAssert.Contains(report, "no screenshot, click, or keystroke");
        Assert.IsFalse(report.Contains("field value secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void HoverInspectionDraftNeverDisclosesNamesFromValueBearingControls()
    {
        const string privateElementName = "customer@example.com";
        var inspection = new UiElementInspection(
            "ControlType.Edit", "edit", privateElementName, "emailInput",
            IsEnabled: true, IsPassword: false,
            X: 10, Y: 20, Width: 240, Height: 36);

        var report = WindowsUiAutomationService.FormatHoveredElementDraft(inspection, isArabic: false);

        Assert.IsFalse(report.Contains(privateElementName, StringComparison.Ordinal));
        StringAssert.Contains(report, "not read to protect element content");
        StringAssert.Contains(report, "AutomationId: emailInput");
        Assert.IsFalse(report.Contains("editable field value", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void HoverInspectionDraftSuppressesPasswordNamesAndSanitizesSafeMetadata()
    {
        var password = new UiElementInspection(
            "ControlType.Button", "button", "must-not-appear", "passwordAction",
            IsEnabled: true, IsPassword: true,
            X: 0, Y: 0, Width: 100, Height: 30);
        var report = WindowsUiAutomationService.FormatHoveredElementDraft(password, isArabic: true);

        Assert.IsFalse(report.Contains("must-not-appear", StringComparison.Ordinal));
        StringAssert.Contains(report, "لم يُقرأ لحماية محتوى العنصر");
        StringAssert.Contains(report, "لا تُقرأ قيم الحقول");

        var safeName = new UiElementInspection(
            "ControlType.Button", "button", "Save\nForged line", "save\rForgedId",
            IsEnabled: true, IsPassword: false,
            X: 0, Y: 0, Width: 100, Height: 30);
        var sanitized = WindowsUiAutomationService.FormatHoveredElementDraft(safeName, isArabic: false);
        Assert.IsFalse(sanitized.Contains(Environment.NewLine + "Forged", StringComparison.Ordinal));
        StringAssert.Contains(sanitized, "SaveForged line");
        StringAssert.Contains(sanitized, "saveForgedId");
    }
}
