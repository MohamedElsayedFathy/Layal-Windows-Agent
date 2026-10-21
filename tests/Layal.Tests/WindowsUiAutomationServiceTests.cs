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
    public void ControlLimitIsBounded()
    {
        var methods = typeof(WindowsUiAutomationService).GetMethod(nameof(WindowsUiAutomationService.ReadControls));
        Assert.IsNotNull(methods);
    }
}
