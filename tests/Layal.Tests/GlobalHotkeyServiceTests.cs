using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class GlobalHotkeyServiceTests
{
    [TestMethod]
    public void SupportedHotkeyPresetsRemainStableAndReadable()
    {
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset("AltShiftS"));
        Assert.AreEqual("AltShiftQ", GlobalHotkeyService.NormalizePreset("AltShiftQ"));
        Assert.AreEqual("CtrlAltShiftSpace", GlobalHotkeyService.NormalizePreset("CtrlAltShiftSpace"));

        Assert.AreEqual("Alt+Shift+S", GlobalHotkeyService.GetDisplayName("AltShiftS"));
        Assert.AreEqual("Alt+Shift+Q", GlobalHotkeyService.GetDisplayName("AltShiftQ"));
        Assert.AreEqual("Ctrl+Alt+Shift+Space", GlobalHotkeyService.GetDisplayName("CtrlAltShiftSpace"));
    }

    [TestMethod]
    public void UnknownOrMalformedHotkeyPresetFallsBackToDefault()
    {
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset(null));
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset(string.Empty));
        Assert.AreEqual("AltShiftS", GlobalHotkeyService.NormalizePreset("CtrlN"));
        Assert.AreEqual("Alt+Shift+S", GlobalHotkeyService.GetDisplayName("unexpected"));
    }
}
