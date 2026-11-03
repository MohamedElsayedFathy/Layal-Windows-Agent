using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class UiThemePaletteTests
{
    [TestMethod]
    public void HighContrastPaletteUsesCurrentWindowsSystemColors()
    {
        var palette = UiThemePalette.Create(dark: false, highContrast: true);
        var darkPalette = UiThemePalette.Create(dark: true, highContrast: true);

        Assert.AreEqual(SystemColors.WindowColor, ToColor(palette, "Layal.Color.Background"));
        Assert.AreEqual(SystemColors.WindowColor, ToColor(palette, "Layal.Color.Surface"));
        Assert.AreEqual(SystemColors.WindowColor, ToColor(palette, "Layal.Color.HeaderStart"));
        Assert.AreEqual(SystemColors.WindowColor, ToColor(palette, "Layal.Color.CardEnd"));
        Assert.AreEqual(SystemColors.WindowColor, ToColor(palette, "Layal.Color.ButtonStart"));
        Assert.AreEqual(SystemColors.WindowTextColor, ToColor(palette, "Layal.Color.Text"));
        Assert.AreEqual(SystemColors.WindowTextColor, ToColor(palette, "Layal.Color.Muted"));
        Assert.AreEqual(SystemColors.HighlightColor, ToColor(palette, "Layal.Color.Focus"));
        Assert.AreEqual(SystemColors.HighlightColor, ToColor(palette, "Layal.Color.NavSelectedBorder"));
        Assert.AreEqual(SystemColors.HotTrackColor, ToColor(palette, "Layal.Color.Cyan"));
        CollectionAssert.AreEquivalent(palette.Keys.ToArray(), darkPalette.Keys.ToArray(),
            "Switching the app's light/dark preference must not change the High Contrast resource keys.");
        foreach (var key in new[] { "Layal.Color.Background", "Layal.Color.Surface", "Layal.Color.Text", "Layal.Color.Focus" })
            Assert.AreEqual(ToColor(palette, key), ToColor(darkPalette, key),
                $"Windows High Contrast must take precedence over the app theme for '{key}'.");
    }

    [TestMethod]
    public void StandardPalettesRetainTheirDistinctLightAndDarkDesigns()
    {
        var light = UiThemePalette.Create(dark: false, highContrast: false);
        var dark = UiThemePalette.Create(dark: true, highContrast: false);

        Assert.AreEqual(Color.FromRgb(244, 247, 252), ToColor(light, "Layal.Color.Background"));
        Assert.AreEqual(Color.FromRgb(17, 21, 33), ToColor(dark, "Layal.Color.Background"));
        Assert.AreNotEqual(ToColor(light, "Layal.Color.Background"), ToColor(dark, "Layal.Color.Background"));
        Assert.AreEqual("#087B8F", light["Layal.Color.Cyan"]);
        Assert.AreEqual("#76DFE8", dark["Layal.Color.Cyan"]);
        Assert.AreEqual("#4D5C75", light["Layal.Color.Muted"]);
        Assert.AreEqual("#899DF4", light["Layal.Color.BackdropStart"]);
        Assert.AreEqual("#98637AFB", light["Layal.Color.GlowBlue"]);
        Assert.AreEqual("#00FFFFFF", UiThemePalette.Create(dark: false, highContrast: true)["Layal.Color.GlowBlue"]);
        Assert.AreEqual("#00FFFFFF", UiThemePalette.Create(dark: true, highContrast: true)["Layal.Color.GlowPink"]);
    }

    [TestMethod]
    public void NamedPresetsChangeAccentAndSurfacesInBothLightAndDarkModes()
    {
        var classicLight = UiThemePalette.Create(dark: false, highContrast: false, preset: "classic");
        var oceanLight = UiThemePalette.Create(dark: false, highContrast: false, preset: "ocean");
        var forestLight = UiThemePalette.Create(dark: false, highContrast: false, preset: "forest");
        var sunsetLight = UiThemePalette.Create(dark: false, highContrast: false, preset: "sunset");
        var oceanDark = UiThemePalette.Create(dark: true, highContrast: false, preset: "ocean");
        var forestDark = UiThemePalette.Create(dark: true, highContrast: false, preset: "forest");
        var sunsetDark = UiThemePalette.Create(dark: true, highContrast: false, preset: "sunset");

        Assert.AreNotEqual(classicLight["Layal.Color.Cyan"], oceanLight["Layal.Color.Cyan"]);
        Assert.AreNotEqual(oceanLight["Layal.Color.Cyan"], forestLight["Layal.Color.Cyan"]);
        Assert.AreNotEqual(forestLight["Layal.Color.Cyan"], sunsetLight["Layal.Color.Cyan"]);
        Assert.AreNotEqual(classicLight["Layal.Color.Background"], oceanLight["Layal.Color.Background"]);
        Assert.AreNotEqual(oceanDark["Layal.Color.Background"], forestDark["Layal.Color.Background"]);
        Assert.AreNotEqual(forestDark["Layal.Color.Background"], sunsetDark["Layal.Color.Background"]);
        CollectionAssert.AreEquivalent(classicLight.Keys.ToArray(), sunsetLight.Keys.ToArray());
        CollectionAssert.AreEquivalent(classicLight.Keys.ToArray(), sunsetDark.Keys.ToArray());
    }

    [TestMethod]
    public void EveryNamedPresetMeetsPrimaryMutedAndScrollbarContrastTargets()
    {
        foreach (var preset in new[] { "classic", "ocean", "forest", "sunset" })
        foreach (var dark in new[] { false, true })
        {
            var palette = UiThemePalette.Create(dark, highContrast: false, preset);
            var mode = dark ? "dark" : "light";
            var background = ToColor(palette, "Layal.Color.Background");
            var text = ToColor(palette, "Layal.Color.Text");
            var muted = ToColor(palette, "Layal.Color.Muted");
            var scrollbar = ToColor(palette, "Layal.Color.ScrollThumb");

            Assert.IsTrue(ContrastRatio(text, background) >= 7.0,
                $"Primary text contrast must be at least 7:1 for {preset}/{mode}.");
            Assert.IsTrue(ContrastRatio(muted, background) >= 4.5,
                $"Muted text contrast must be at least 4.5:1 for {preset}/{mode}.");
            Assert.IsTrue(ContrastRatio(scrollbar, background) >= 2.0,
                $"Scrollbar control contrast must be at least 2:1 for {preset}/{mode}.");
        }
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) +
               0.7152 * Linear(color.G) +
               0.0722 * Linear(color.B);
    }

    [TestMethod]
    public void HighContrastOverridesEveryNamedPreset()
    {
        var classic = UiThemePalette.Create(dark: false, highContrast: true, preset: "classic");
        foreach (var preset in new[] { "ocean", "forest", "sunset" })
        {
            var themed = UiThemePalette.Create(dark: true, highContrast: true, preset: preset);
            CollectionAssert.AreEquivalent(classic.Keys.ToArray(), themed.Keys.ToArray());
            Assert.AreEqual(classic["Layal.Color.Background"], themed["Layal.Color.Background"]);
            Assert.AreEqual(classic["Layal.Color.Text"], themed["Layal.Color.Text"]);
            Assert.AreEqual(classic["Layal.Color.Cyan"], themed["Layal.Color.Cyan"]);
        }
    }

    private static Color ToColor(IReadOnlyDictionary<string, string> palette, string key) =>
        (Color)ColorConverter.ConvertFromString(palette[key])!;
}
