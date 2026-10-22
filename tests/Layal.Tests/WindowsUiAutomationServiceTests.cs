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
