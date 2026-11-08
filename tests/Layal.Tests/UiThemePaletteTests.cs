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

        Assert.AreEqual(Color.FromRgb(244, 246, 252), ToColor(light, "Layal.Color.Background"));
        Assert.AreEqual(Color.FromRgb(17, 24, 39), ToColor(dark, "Layal.Color.Background"));
        Assert.AreNotEqual(ToColor(light, "Layal.Color.Background"), ToColor(dark, "Layal.Color.Background"));
        Assert.AreEqual("#536BC7", light["Layal.Color.Cyan"]);
        Assert.AreEqual("#83A3E9", dark["Layal.Color.Cyan"]);
        Assert.AreEqual("#5F6B84", light["Layal.Color.Muted"]);
        Assert.AreEqual("#9CAAE0", light["Layal.Color.BackdropStart"]);
        Assert.AreEqual("#557E8FF0", light["Layal.Color.GlowBlue"]);
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
    public void EveryNamedPresetMeetsTextAndScrollbarContrastTargetsOnRenderedSurfaces()
    {
        var textSurfaceKeys = new[]
        {
            "Layal.Color.Background",
            "Layal.Color.Surface",
            "Layal.Color.SurfaceRaised",
            "Layal.Color.SurfaceHover",
            "Layal.Color.InputFocus",
            "Layal.Color.NavSelected",
            "Layal.Color.ListHover",
            "Layal.Color.ListSelected"
        };

        foreach (var preset in new[] { "classic", "ocean", "forest", "sunset" })
        foreach (var dark in new[] { false, true })
        {
            var palette = UiThemePalette.Create(dark, highContrast: false, preset);
            var mode = dark ? "dark" : "light";
            var text = ToColor(palette, "Layal.Color.Text");
            var muted = ToColor(palette, "Layal.Color.Muted");

            foreach (var surfaceKey in textSurfaceKeys)
            {
                var surface = ToColor(palette, surfaceKey);
                Assert.IsTrue(ContrastRatio(text, surface) >= 7.0,
                    $"Primary text must be at least 7:1 against {surfaceKey} for {preset}/{mode}.");
                Assert.IsTrue(ContrastRatio(muted, surface) >= 4.5,
                    $"Muted text must be at least 4.5:1 against {surfaceKey} for {preset}/{mode}.");
            }

            // App.xaml styles the ScrollBar track with Layal.PanelAlt, which maps to SurfaceRaised.
            var scrollbarTrack = ToColor(palette, "Layal.Color.SurfaceRaised");
            var scrollbarThumb = ToColor(palette, "Layal.Color.ScrollThumb");
            Assert.IsTrue(ContrastRatio(scrollbarThumb, scrollbarTrack) >= 2.0,
                $"Scrollbar thumb must be at least 2:1 against its actual track for {preset}/{mode}.");

            foreach (var statusKey in new[] { "Layal.Color.Success", "Layal.Color.Warning", "Layal.Color.Danger" })
            foreach (var statusSurfaceKey in new[] { "Layal.Color.Background", "Layal.Color.Surface" })
            {
                Assert.IsTrue(ContrastRatio(ToColor(palette, statusKey), ToColor(palette, statusSurfaceKey)) >= 4.5,
                    $"{statusKey} must be at least 4.5:1 against {statusSurfaceKey} for {preset}/{mode}.");
            }
        }
    }

    [TestMethod]
    public void AccentAndPrimaryButtonGradientsKeepReadableForegroundInEveryPreset()
    {
        foreach (var preset in new[] { "classic", "ocean", "forest", "sunset" })
        foreach (var dark in new[] { false, true })
        {
            var palette = UiThemePalette.Create(dark, highContrast: false, preset);
            var mode = dark ? "dark" : "light";
            var accentForeground = ToColor(palette, "Layal.Color.OnAccent");
            var buttonForeground = ToColor(palette, "Layal.Color.Text");

            foreach (var accentStop in new[] { "Layal.Color.AccentStart", "Layal.Color.AccentEnd" })
                Assert.IsTrue(ContrastRatio(accentForeground, ToColor(palette, accentStop)) >= 4.5,
                    $"Accent foreground must be at least 4.5:1 against {accentStop} for {preset}/{mode}.");

            foreach (var buttonStop in new[] { "Layal.Color.ButtonStart", "Layal.Color.ButtonEnd" })
                Assert.IsTrue(ContrastRatio(buttonForeground, ToColor(palette, buttonStop)) >= 4.5,
                    $"Primary button text must be at least 4.5:1 against {buttonStop} for {preset}/{mode}.");
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
