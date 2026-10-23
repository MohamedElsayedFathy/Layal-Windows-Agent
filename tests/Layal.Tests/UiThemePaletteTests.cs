using System.Collections.Generic;
using System.Linq;
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
    }

    private static Color ToColor(IReadOnlyDictionary<string, string> palette, string key) =>
        (Color)ColorConverter.ConvertFromString(palette[key])!;
}
