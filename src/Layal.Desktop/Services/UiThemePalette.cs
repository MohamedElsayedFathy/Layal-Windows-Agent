using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Layal.Services;

/// <summary>
/// Provides the presentation palette for the application.
/// Windows High Contrast takes precedence over the app's preferred light/dark theme.
/// </summary>
public static class UiThemePalette
{
    public static Dictionary<string, string> Create(bool dark, bool highContrast)
    {
        var palette = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["Layal.Color.Background"] = dark ? "#111521" : "#F4F7FC",
            ["Layal.Color.BackgroundEnd"] = dark ? "#171B2B" : "#EEF3FB",
            ["Layal.Color.Surface"] = dark ? "#1B2233" : "#FFFFFF",
            ["Layal.Color.SurfaceRaised"] = dark ? "#232B3D" : "#FAFBFF",
            ["Layal.Color.SurfaceHover"] = dark ? "#2B354A" : "#EDF3FA",
            ["Layal.Color.Border"] = dark ? "#3A465E" : "#DCE5F0",
            ["Layal.Color.Cyan"] = dark ? "#76DFE8" : "#087B8F",
            ["Layal.Color.Purple"] = dark ? "#B6A5FF" : "#7563D6",
            ["Layal.Color.Text"] = dark ? "#EDF2FF" : "#1C2940",
            ["Layal.Color.Muted"] = dark ? "#A8B4C9" : "#5D6D84",
            ["Layal.Color.ScrollThumb"] = dark ? "#50617A" : "#9BAEC7",
            ["Layal.Color.Success"] = dark ? "#73DEAA" : "#177A57",
            ["Layal.Color.Warning"] = dark ? "#FFD17D" : "#946200",
            ["Layal.Color.Danger"] = dark ? "#FF91A8" : "#BD3C55",
            ["Layal.Color.Focus"] = dark ? "#7C93D5" : "#AFC8F7",
            ["Layal.Color.HeaderStart"] = dark ? "#E6202D43" : "#EAF0F7FF",
            ["Layal.Color.HeaderMiddle"] = dark ? "#DE1B2233" : "#FAFFFFFF",
            ["Layal.Color.HeaderEnd"] = dark ? "#E42C2542" : "#EAF3EEFF",
            ["Layal.Color.CardStart"] = dark ? "#E5222B3F" : "#F4FFFFFF",
            ["Layal.Color.CardEnd"] = dark ? "#DE1B2335" : "#EEF8FAFF",
            ["Layal.Color.ButtonStart"] = dark ? "#326B78" : "#A3E3E7",
            ["Layal.Color.ButtonEnd"] = dark ? "#594C84" : "#C8C0F7",
            ["Layal.Color.BrandBorder"] = dark ? "#4C8F9B" : "#B9E6EB",
            ["Layal.Color.BrandDot"] = dark ? "#FFD36E" : "#E8B451",
            ["Layal.Color.DangerSurface"] = dark ? "#452735" : "#FBECEF",
            ["Layal.Color.DangerBorder"] = dark ? "#794154" : "#E9C3CC",
            ["Layal.Color.NavSelected"] = dark ? "#283B50" : "#E2F2F6",
            ["Layal.Color.NavSelectedBorder"] = dark ? "#4F8190" : "#C6E8ED",
            ["Layal.Color.NavHover"] = dark ? "#242F43" : "#F0F5FC",
            ["Layal.Color.ListHover"] = dark ? "#263247" : "#F0F6FC",
            ["Layal.Color.ListSelected"] = dark ? "#2C4155" : "#E3F3F6",
            ["Layal.Color.InputFocus"] = dark ? "#222E43" : "#FBFDFF",
            ["Layal.Color.HoverSurface"] = dark ? "#2C3B50" : "#E8F5F7",
            ["Layal.Color.Shadow"] = dark ? "#080B13" : "#A8B9D6",
            // Glass design: dusk backdrop, translucent panels and one accent used for selection and primary actions.
            ["Layal.Color.BackdropStart"] = dark ? "#1F3463" : "#C9DBF7",
            ["Layal.Color.BackdropMiddle"] = dark ? "#3A2F6E" : "#D8D0F4",
            ["Layal.Color.BackdropEnd"] = dark ? "#5B3A6C" : "#F2D8E4",
            ["Layal.Color.Glass"] = dark ? "#9E1B2240" : "#A6FFFFFF",
            ["Layal.Color.GlassStrong"] = dark ? "#C7172040" : "#D9FFFFFF",
            ["Layal.Color.GlassBorder"] = dark ? "#40FFFFFF" : "#B8FFFFFF",
            ["Layal.Color.AccentStart"] = "#4A66DE",
            ["Layal.Color.AccentEnd"] = "#7A50D4",
            ["Layal.Color.OnAccent"] = "#FFFFFF"
        };

        if (!highContrast)
            return palette;

        // Use the active Windows accessibility palette, not app-owned accent colors.
        // Solid window surfaces plus system-colored borders keep information legible
        // when the user switches to a High Contrast theme while Layal is running.
        var window = SystemColors.WindowColor.ToString();
        var text = SystemColors.WindowTextColor.ToString();
        var highlight = SystemColors.HighlightColor.ToString();
        var hotTrack = SystemColors.HotTrackColor.ToString();

        return new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["Layal.Color.Background"] = window,
            ["Layal.Color.BackgroundEnd"] = window,
            ["Layal.Color.Surface"] = window,
            ["Layal.Color.SurfaceRaised"] = window,
            ["Layal.Color.SurfaceHover"] = window,
            ["Layal.Color.Border"] = text,
            ["Layal.Color.Cyan"] = hotTrack,
            ["Layal.Color.Purple"] = text,
            ["Layal.Color.Text"] = text,
            ["Layal.Color.Muted"] = text,
            ["Layal.Color.ScrollThumb"] = text,
            ["Layal.Color.Success"] = text,
            ["Layal.Color.Warning"] = text,
            ["Layal.Color.Danger"] = text,
            ["Layal.Color.Focus"] = highlight,
            ["Layal.Color.HeaderStart"] = window,
            ["Layal.Color.HeaderMiddle"] = window,
            ["Layal.Color.HeaderEnd"] = window,
            ["Layal.Color.CardStart"] = window,
            ["Layal.Color.CardEnd"] = window,
            ["Layal.Color.ButtonStart"] = window,
            ["Layal.Color.ButtonEnd"] = window,
            ["Layal.Color.BrandBorder"] = highlight,
            ["Layal.Color.BrandDot"] = text,
            ["Layal.Color.DangerSurface"] = window,
            ["Layal.Color.DangerBorder"] = highlight,
            ["Layal.Color.NavSelected"] = window,
            ["Layal.Color.NavSelectedBorder"] = highlight,
            ["Layal.Color.NavHover"] = window,
            ["Layal.Color.ListHover"] = window,
            ["Layal.Color.ListSelected"] = window,
            ["Layal.Color.InputFocus"] = window,
            ["Layal.Color.HoverSurface"] = window,
            ["Layal.Color.Shadow"] = window,
            ["Layal.Color.BackdropStart"] = window,
            ["Layal.Color.BackdropMiddle"] = window,
            ["Layal.Color.BackdropEnd"] = window,
            ["Layal.Color.Glass"] = window,
            ["Layal.Color.GlassStrong"] = window,
            ["Layal.Color.GlassBorder"] = text,
            ["Layal.Color.AccentStart"] = highlight,
            ["Layal.Color.AccentEnd"] = highlight,
            ["Layal.Color.OnAccent"] = SystemColors.HighlightTextColor.ToString()
        };
    }
}
