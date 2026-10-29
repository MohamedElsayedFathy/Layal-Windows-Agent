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
    public static Dictionary<string, string> Create(bool dark, bool highContrast, string? preset = "classic")
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
            ["Layal.Color.Muted"] = dark ? "#A8B4C9" : "#4D5C75",
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
            ["Layal.Color.BackdropStart"] = dark ? "#1F3463" : "#899DF4",
            ["Layal.Color.BackdropMiddle"] = dark ? "#3A2F6E" : "#B09AEF",
            ["Layal.Color.BackdropEnd"] = dark ? "#5B3A6C" : "#F1B4D2",
            ["Layal.Color.Glass"] = dark ? "#9E1B2240" : "#99FFFFFF",
            ["Layal.Color.GlassStrong"] = dark ? "#C7172040" : "#E0FFFFFF",
            ["Layal.Color.GlassBorder"] = dark ? "#66FFFFFF" : "#D0FFFFFF",
            ["Layal.Color.GlowBlue"] = dark ? "#A45D7BFF" : "#98637AFB",
            ["Layal.Color.GlowViolet"] = dark ? "#923BDFC8" : "#8E9A60F0",
            ["Layal.Color.GlowPink"] = dark ? "#99CB62C4" : "#88EF78B9",
            ["Layal.Color.AccentStart"] = "#4A66DE",
            ["Layal.Color.AccentEnd"] = "#7A50D4",
            ["Layal.Color.OnAccent"] = "#FFFFFF"
        };

        if (!highContrast)
        {
            ApplyPreset(palette, dark, preset);
            return palette;
        }

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
            ["Layal.Color.GlowBlue"] = "#00FFFFFF",
            ["Layal.Color.GlowViolet"] = "#00FFFFFF",
            ["Layal.Color.GlowPink"] = "#00FFFFFF",
            ["Layal.Color.AccentStart"] = highlight,
            ["Layal.Color.AccentEnd"] = highlight,
            ["Layal.Color.OnAccent"] = SystemColors.HighlightTextColor.ToString()
        };
    }

    private static void ApplyPreset(Dictionary<string, string> palette, bool dark, string? requestedPreset)
    {
        var preset = requestedPreset is "ocean" or "forest" or "sunset" ? requestedPreset : "classic";
        if (preset == "classic")
            return;

        // Theme presets deliberately change only presentational colors. System High Contrast
        // bypasses this method, and text/surface values remain paired for legibility.
        var accents = preset switch
        {
            "ocean" => dark
                ? new Dictionary<string, string>
                {
                    ["Layal.Color.Background"] = "#0D1B27", ["Layal.Color.BackgroundEnd"] = "#122A3A",
                    ["Layal.Color.Surface"] = "#142A3A", ["Layal.Color.SurfaceRaised"] = "#1D3849",
                    ["Layal.Color.SurfaceHover"] = "#29495D", ["Layal.Color.Border"] = "#355A6B",
                    ["Layal.Color.Cyan"] = "#6EDDF0", ["Layal.Color.Purple"] = "#9AB8FF",
                    ["Layal.Color.Text"] = "#EAF7FB", ["Layal.Color.Muted"] = "#A3C2CF",
                    ["Layal.Color.NavSelected"] = "#1D3C4C", ["Layal.Color.NavSelectedBorder"] = "#397C91",
                    ["Layal.Color.ListSelected"] = "#1D4253", ["Layal.Color.InputFocus"] = "#19394A",
                    ["Layal.Color.HoverSurface"] = "#25485A", ["Layal.Color.BackdropStart"] = "#143955",
                    ["Layal.Color.BackdropMiddle"] = "#24556B", ["Layal.Color.BackdropEnd"] = "#36426D",
                    ["Layal.Color.ButtonStart"] = "#246A7E", ["Layal.Color.ButtonEnd"] = "#4D5C9D",
                    ["Layal.Color.AccentStart"] = "#1F9DB8", ["Layal.Color.AccentEnd"] = "#536FD1"
                }
                : new Dictionary<string, string>
                {
                    ["Layal.Color.Background"] = "#F0F8FC", ["Layal.Color.BackgroundEnd"] = "#E7F4FA",
                    ["Layal.Color.Surface"] = "#FFFFFF", ["Layal.Color.SurfaceRaised"] = "#F6FBFE",
                    ["Layal.Color.SurfaceHover"] = "#E7F4F9", ["Layal.Color.Border"] = "#C9DFE9",
                    ["Layal.Color.Cyan"] = "#006C8C", ["Layal.Color.Purple"] = "#555EBC",
                    ["Layal.Color.Text"] = "#173344", ["Layal.Color.Muted"] = "#456779",
                    ["Layal.Color.NavSelected"] = "#D8EFF7", ["Layal.Color.NavSelectedBorder"] = "#9CCAD9",
                    ["Layal.Color.ListSelected"] = "#D8EFF7", ["Layal.Color.InputFocus"] = "#F4FBFF",
                    ["Layal.Color.HoverSurface"] = "#E2F3F8", ["Layal.Color.BackdropStart"] = "#75C8DD",
                    ["Layal.Color.BackdropMiddle"] = "#A2BFEF", ["Layal.Color.BackdropEnd"] = "#D6C9F4",
                    ["Layal.Color.ButtonStart"] = "#9BDCE9", ["Layal.Color.ButtonEnd"] = "#B7C9F4",
                    ["Layal.Color.AccentStart"] = "#1688A5", ["Layal.Color.AccentEnd"] = "#5A6BC6"
                },
            "forest" => dark
                ? new Dictionary<string, string>
                {
                    ["Layal.Color.Background"] = "#101C17", ["Layal.Color.BackgroundEnd"] = "#17271F",
                    ["Layal.Color.Surface"] = "#1A2B22", ["Layal.Color.SurfaceRaised"] = "#22382B",
                    ["Layal.Color.SurfaceHover"] = "#2C4536", ["Layal.Color.Border"] = "#425C49",
                    ["Layal.Color.Cyan"] = "#82D8AE", ["Layal.Color.Purple"] = "#C0B1E8",
                    ["Layal.Color.Text"] = "#ECF7EF", ["Layal.Color.Muted"] = "#B1C9B6",
                    ["Layal.Color.NavSelected"] = "#263F30", ["Layal.Color.NavSelectedBorder"] = "#548364",
                    ["Layal.Color.ListSelected"] = "#2D4937", ["Layal.Color.InputFocus"] = "#20392A",
                    ["Layal.Color.HoverSurface"] = "#2B4736", ["Layal.Color.BackdropStart"] = "#244F3B",
                    ["Layal.Color.BackdropMiddle"] = "#37634E", ["Layal.Color.BackdropEnd"] = "#455A70",
                    ["Layal.Color.ButtonStart"] = "#2C7354", ["Layal.Color.ButtonEnd"] = "#5B6A91",
                    ["Layal.Color.AccentStart"] = "#23865E", ["Layal.Color.AccentEnd"] = "#6378AB"
                }
                : new Dictionary<string, string>
                {
                    ["Layal.Color.Background"] = "#F2F8F1", ["Layal.Color.BackgroundEnd"] = "#E9F3E8",
                    ["Layal.Color.Surface"] = "#FFFFFF", ["Layal.Color.SurfaceRaised"] = "#F8FCF7",
                    ["Layal.Color.SurfaceHover"] = "#E8F2E7", ["Layal.Color.Border"] = "#CDDFCC",
                    ["Layal.Color.Cyan"] = "#216B50", ["Layal.Color.Purple"] = "#6B5D9A",
                    ["Layal.Color.Text"] = "#20392C", ["Layal.Color.Muted"] = "#50695A",
                    ["Layal.Color.NavSelected"] = "#DDEFE0", ["Layal.Color.NavSelectedBorder"] = "#B7D6BF",
                    ["Layal.Color.ListSelected"] = "#E0F1E2", ["Layal.Color.InputFocus"] = "#F8FCF7",
                    ["Layal.Color.HoverSurface"] = "#E7F4E8", ["Layal.Color.BackdropStart"] = "#97D1AC",
                    ["Layal.Color.BackdropMiddle"] = "#B4DCC1", ["Layal.Color.BackdropEnd"] = "#C5D6F0",
                    ["Layal.Color.ButtonStart"] = "#AFE0BC", ["Layal.Color.ButtonEnd"] = "#C7C5EB",
                    ["Layal.Color.AccentStart"] = "#25895B", ["Layal.Color.AccentEnd"] = "#6E69AB"
                },
            _ => dark
                ? new Dictionary<string, string>
                {
                    ["Layal.Color.Background"] = "#211722", ["Layal.Color.BackgroundEnd"] = "#2C1B2A",
                    ["Layal.Color.Surface"] = "#302131", ["Layal.Color.SurfaceRaised"] = "#3B293C",
                    ["Layal.Color.SurfaceHover"] = "#4B3348", ["Layal.Color.Border"] = "#68475F",
                    ["Layal.Color.Cyan"] = "#F0A8D7", ["Layal.Color.Purple"] = "#C5A5FF",
                    ["Layal.Color.Text"] = "#FFF0F7", ["Layal.Color.Muted"] = "#D3B8C9",
                    ["Layal.Color.NavSelected"] = "#4B2D48", ["Layal.Color.NavSelectedBorder"] = "#9B5D8B",
                    ["Layal.Color.ListSelected"] = "#50344E", ["Layal.Color.InputFocus"] = "#432B42",
                    ["Layal.Color.HoverSurface"] = "#493149", ["Layal.Color.BackdropStart"] = "#6C315C",
                    ["Layal.Color.BackdropMiddle"] = "#754A86", ["Layal.Color.BackdropEnd"] = "#9A566B",
                    ["Layal.Color.ButtonStart"] = "#A84B86", ["Layal.Color.ButtonEnd"] = "#7556B8",
                    ["Layal.Color.AccentStart"] = "#B94A88", ["Layal.Color.AccentEnd"] = "#845AC3"
                }
                : new Dictionary<string, string>
                {
                    ["Layal.Color.Background"] = "#FFF6F2", ["Layal.Color.BackgroundEnd"] = "#FBEFF6",
                    ["Layal.Color.Surface"] = "#FFFFFF", ["Layal.Color.SurfaceRaised"] = "#FFFAFC",
                    ["Layal.Color.SurfaceHover"] = "#F8EAF3", ["Layal.Color.Border"] = "#E8C9DA",
                    ["Layal.Color.Cyan"] = "#963E72", ["Layal.Color.Purple"] = "#7350A8",
                    ["Layal.Color.Text"] = "#3E2938", ["Layal.Color.Muted"] = "#725467",
                    ["Layal.Color.NavSelected"] = "#F4DDED", ["Layal.Color.NavSelectedBorder"] = "#E2BCD8",
                    ["Layal.Color.ListSelected"] = "#F6E2F0", ["Layal.Color.InputFocus"] = "#FFFAFD",
                    ["Layal.Color.HoverSurface"] = "#F8E6F2", ["Layal.Color.BackdropStart"] = "#E9A4C7",
                    ["Layal.Color.BackdropMiddle"] = "#C6A7EA", ["Layal.Color.BackdropEnd"] = "#F4C6B3",
                    ["Layal.Color.ButtonStart"] = "#E7AFD0", ["Layal.Color.ButtonEnd"] = "#C9B5F0",
                    ["Layal.Color.AccentStart"] = "#B54E87", ["Layal.Color.AccentEnd"] = "#8054B6"
                }
        };

        foreach (var (key, value) in accents)
            palette[key] = value;
    }
}
