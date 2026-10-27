using System;
using System.Collections.Generic;
using System.Windows.Media;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class DesignTokensTests
{
    private static readonly string[] GlassKeys =
    {
        "Layal.Color.BackdropStart", "Layal.Color.BackdropMiddle", "Layal.Color.BackdropEnd",
        "Layal.Color.Glass", "Layal.Color.GlassStrong", "Layal.Color.GlassBorder",
        "Layal.Color.AccentStart", "Layal.Color.AccentEnd", "Layal.Color.OnAccent"
    };

    [TestMethod]
    public void GlassTokensExistInEveryPalette()
    {
        foreach (var dark in new[] { false, true })
        foreach (var highContrast in new[] { false, true })
        {
            var palette = UiThemePalette.Create(dark, highContrast);
            foreach (var key in GlassKeys)
                Assert.IsTrue(palette.ContainsKey(key), $"{key} is missing (dark={dark}, highContrast={highContrast}).");
        }
    }

    [TestMethod]
    public void TextOnAccentAndOnGlassMeetsWcagAaInBothThemes()
    {
        foreach (var dark in new[] { false, true })
        {
            var palette = UiThemePalette.Create(dark, highContrast: false);
            var onAccent = Parse(palette["Layal.Color.OnAccent"]);
            foreach (var key in new[] { "Layal.Color.AccentStart", "Layal.Color.AccentEnd" })
                Assert.IsTrue(Contrast(onAccent, Opaque(Parse(palette[key]))) >= 4.5,
                    $"Text on {key} must reach 4.5:1 (dark={dark}).");

            foreach (var surfaceKey in new[] { "Layal.Color.Glass", "Layal.Color.GlassStrong" })
            foreach (var backdropKey in new[] { "Layal.Color.BackdropStart", "Layal.Color.BackdropMiddle", "Layal.Color.BackdropEnd" })
            foreach (var textKey in new[] { "Layal.Color.Text", "Layal.Color.Muted" })
            {
                var surface = Composite(Parse(palette[surfaceKey]), Opaque(Parse(palette[backdropKey])));
                var ratio = Contrast(Opaque(Parse(palette[textKey])), surface);
                Assert.IsTrue(ratio >= 4.5,
                    $"{textKey} on {surfaceKey} over {backdropKey} is {ratio:0.00}:1 (dark={dark}); WCAG AA needs 4.5:1.");
            }
        }
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value)!;
    private static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);

    private static Color Composite(Color foreground, Color background)
    {
        var a = foreground.A / 255d;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(foreground.R, background.R), Mix(foreground.G, background.G), Mix(foreground.B, background.B));
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}
