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
            ["Layal.Color.Background"] = dark ? "#101827" : "#F4F6FA",
            ["Layal.Color.BackgroundEnd"] = dark ? "#151F30" : "#EFF2F8",
            ["Layal.Color.Surface"] = dark ? "#182337" : "#FFFFFF",
            ["Layal.Color.SurfaceRaised"] = dark ? "#202D43" : "#F9FBFE",
            ["Layal.Color.SurfaceHover"] = dark ? "#293851" : "#EDF2F8",
            ["Layal.Color.Border"] = dark ? "#3B4B63" : "#D8E0EB",
            ["Layal.Color.Cyan"] = dark ? "#91B6FF" : "#315EA8",
            ["Layal.Color.Purple"] = dark ? "#C1AEFF" : "#6C56B3",
            ["Layal.Color.Text"] = dark ? "#F2F5FB" : "#18283F",
            ["Layal.Color.Muted"] = dark ? "#B6C2D4" : "#52637A",
            ["Layal.Color.ScrollThumb"] = dark ? "#8294AE" : "#8493A8",
            ["Layal.Color.Success"] = dark ? "#79D7A7" : "#176B4D",
            ["Layal.Color.Warning"] = dark ? "#F3CA78" : "#7A4D08",
            ["Layal.Color.Danger"] = dark ? "#F29BAB" : "#A52740",
            ["Layal.Color.Focus"] = dark ? "#9DB9FF" : "#2563EB",
            ["Layal.Color.HeaderStart"] = dark ? "#F019263B" : "#F7F9FC",
            ["Layal.Color.HeaderMiddle"] = dark ? "#F21C2A40" : "#FFFFFFFF",
            ["Layal.Color.HeaderEnd"] = dark ? "#F2223048" : "#EFF2F8",
            ["Layal.Color.CardStart"] = dark ? "#F21B2940" : "#FFFFFFFF",
            ["Layal.Color.CardEnd"] = dark ? "#F224334A" : "#FFF9FBFE",
            ["Layal.Color.ButtonStart"] = dark ? "#354766" : "#DCE7F8",
            ["Layal.Color.ButtonEnd"] = dark ? "#3D4662" : "#E5EAF6",
            ["Layal.Color.BrandBorder"] = dark ? "#526C91" : "#C5D5EC",
            ["Layal.Color.BrandDot"] = dark ? "#E4BF78" : "#A8792B",
            ["Layal.Color.DangerSurface"] = dark ? "#472632" : "#FCEDEF",
            ["Layal.Color.DangerBorder"] = dark ? "#784653" : "#E7B8C1",
            ["Layal.Color.NavSelected"] = dark ? "#263D61" : "#E2ECFC",
            ["Layal.Color.NavSelectedBorder"] = dark ? "#779AE0" : "#A3BCE8",
            ["Layal.Color.NavHover"] = dark ? "#21334D" : "#EBF2FD",
            ["Layal.Color.ListHover"] = dark ? "#243852" : "#EBF2FD",
            ["Layal.Color.ListSelected"] = dark ? "#2B4568" : "#DCE8FB",
            ["Layal.Color.InputFocus"] = dark ? "#1B2C45" : "#FFFFFF",
            ["Layal.Color.HoverSurface"] = dark ? "#293851" : "#EDF2F8",
            ["Layal.Color.MenuHover"] = dark ? "#2C4060" : "#E5EDFA",
            ["Layal.Color.MenuHoverText"] = dark ? "#F2F5FB" : "#18283F",
            ["Layal.Color.Shadow"] = dark ? "#070D17" : "#7A899E",
            // Glass design: dusk backdrop, translucent panels and one accent used for selection and primary actions.
            ["Layal.Color.BackdropStart"] = dark ? "#111C2D" : "#E8EEF7",
            ["Layal.Color.BackdropMiddle"] = dark ? "#191C31" : "#EEF0F8",
            ["Layal.Color.BackdropEnd"] = dark ? "#261F30" : "#F4EDF3",
            ["Layal.Color.Glass"] = dark ? "#F318263A" : "#F7FFFFFF",
            ["Layal.Color.GlassStrong"] = dark ? "#FC1B2940" : "#FCFFFFFF",
            ["Layal.Color.GlassBorder"] = dark ? "#526C91" : "#D6DFEA",
            ["Layal.Color.GlowBlue"] = dark ? "#365D84E8" : "#244B83D5",
            ["Layal.Color.GlowViolet"] = dark ? "#2F8D78D8" : "#1F8D78D0",
            ["Layal.Color.GlowPink"] = dark ? "#22C47CA5" : "#18C47CA5",
            ["Layal.Color.AccentStart"] = dark ? "#315EB8" : "#244F9C",
            ["Layal.Color.AccentEnd"] = dark ? "#5A63B2" : "#4B4E9F",
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
            ["Layal.Color.MenuHover"] = highlight,
            ["Layal.Color.MenuHoverText"] = SystemColors.HighlightTextColor.ToString(),
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
                    ["Layal.Color.HoverSurface"] = "#25485A", ["Layal.Color.BackdropStart"] = "#122B3A",
                    ["Layal.Color.BackdropMiddle"] = "#1B3445", ["Layal.Color.BackdropEnd"] = "#242D49",
                    ["Layal.Color.ButtonStart"] = "#246A7E", ["Layal.Color.ButtonEnd"] = "#4D5C9D",
                    ["Layal.Color.AccentStart"] = "#197B92", ["Layal.Color.AccentEnd"] = "#536FD1"
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
                    ["Layal.Color.HoverSurface"] = "#E2F3F8", ["Layal.Color.BackdropStart"] = "#DCEEF5",
                    ["Layal.Color.BackdropMiddle"] = "#E8F0F8", ["Layal.Color.BackdropEnd"] = "#EEF0F8",
                    ["Layal.Color.ButtonStart"] = "#9BDCE9", ["Layal.Color.ButtonEnd"] = "#B7C9F4",
                    ["Layal.Color.AccentStart"] = "#147B96", ["Layal.Color.AccentEnd"] = "#5A6BC6"
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
                    ["Layal.Color.HoverSurface"] = "#2B4736", ["Layal.Color.BackdropStart"] = "#1A3628",
                    ["Layal.Color.BackdropMiddle"] = "#243D30", ["Layal.Color.BackdropEnd"] = "#2B3A47",
                    ["Layal.Color.ButtonStart"] = "#2C7354", ["Layal.Color.ButtonEnd"] = "#5B6A91",
                    ["Layal.Color.AccentStart"] = "#227C59", ["Layal.Color.AccentEnd"] = "#606F9F"
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
                    ["Layal.Color.HoverSurface"] = "#E7F4E8", ["Layal.Color.BackdropStart"] = "#E4EEE2",
                    ["Layal.Color.BackdropMiddle"] = "#EAF1E8", ["Layal.Color.BackdropEnd"] = "#EEF0F5",
                    ["Layal.Color.ButtonStart"] = "#AFE0BC", ["Layal.Color.ButtonEnd"] = "#C7C5EB",
                    ["Layal.Color.AccentStart"] = "#247B53", ["Layal.Color.AccentEnd"] = "#6E69AB"
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
                    ["Layal.Color.HoverSurface"] = "#493149", ["Layal.Color.BackdropStart"] = "#3F263D",
                    ["Layal.Color.BackdropMiddle"] = "#4A3150", ["Layal.Color.BackdropEnd"] = "#463A50",
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
                    ["Layal.Color.HoverSurface"] = "#F8E6F2", ["Layal.Color.BackdropStart"] = "#F1DCE5",
                    ["Layal.Color.BackdropMiddle"] = "#EFE6F2", ["Layal.Color.BackdropEnd"] = "#F4E5DE",
                    ["Layal.Color.ButtonStart"] = "#E7AFD0", ["Layal.Color.ButtonEnd"] = "#C9B5F0",
                    ["Layal.Color.AccentStart"] = "#B54E87", ["Layal.Color.AccentEnd"] = "#8054B6"
                }
        };

        foreach (var (key, value) in accents)
            palette[key] = value;
    }
}
