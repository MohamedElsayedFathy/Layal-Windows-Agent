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
        Assert.AreEqual("Arabic: مرحبًا\\nEnglish: hello", WindowsUiAutomationService.ValidateUiText("Arabic: مرحبًا\\nEnglish: hello"));
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

}
