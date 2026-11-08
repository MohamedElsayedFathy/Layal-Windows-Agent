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
            ["Layal.Color.Background"] = dark ? "#111827" : "#F4F6FC",
            ["Layal.Color.BackgroundEnd"] = dark ? "#172033" : "#EEF1FA",
            ["Layal.Color.Surface"] = dark ? "#1B263A" : "#FFFFFF",
            ["Layal.Color.SurfaceRaised"] = dark ? "#23314A" : "#FCFCFF",
            ["Layal.Color.SurfaceHover"] = dark ? "#2C3B57" : "#F2F4FB",
            ["Layal.Color.Border"] = dark ? "#3A4964" : "#E2E7F1",
            ["Layal.Color.Cyan"] = dark ? "#83A3E9" : "#536BC7",
            ["Layal.Color.Purple"] = dark ? "#B8A6F1" : "#796BCB",
            ["Layal.Color.Text"] = dark ? "#EDF2FF" : "#25314A",
            ["Layal.Color.Muted"] = dark ? "#B5C0D6" : "#5F6B84",
            ["Layal.Color.ScrollThumb"] = dark ? "#7184AA" : "#A2B0C9",
            ["Layal.Color.Success"] = dark ? "#75D8A7" : "#187A59",
            ["Layal.Color.Warning"] = dark ? "#FFD17D" : "#855A0C",
            ["Layal.Color.Danger"] = dark ? "#FF98AD" : "#B33C5A",
            ["Layal.Color.Focus"] = dark ? "#A4B5F5" : "#B6C5F6",
            ["Layal.Color.HeaderStart"] = dark ? "#EF1B263B" : "#F1F4FD",
            ["Layal.Color.HeaderMiddle"] = dark ? "#F01A2438" : "#FCFDFF",
            ["Layal.Color.HeaderEnd"] = dark ? "#F0232441" : "#F3F0FD",
            ["Layal.Color.CardStart"] = dark ? "#F01E2940" : "#FFFFFFFF",
            ["Layal.Color.CardEnd"] = dark ? "#EE252D45" : "#FFFAFBFF",
            ["Layal.Color.ButtonStart"] = dark ? "#465B9B" : "#D7DFFA",
            ["Layal.Color.ButtonEnd"] = dark ? "#5D4E98" : "#E4DDF8",
            ["Layal.Color.BrandBorder"] = dark ? "#536A91" : "#D8DDF3",
            ["Layal.Color.BrandDot"] = dark ? "#E2BD7B" : "#D8B66D",
            ["Layal.Color.DangerSurface"] = dark ? "#472938" : "#FBECEF",
            ["Layal.Color.DangerBorder"] = dark ? "#7A4C5A" : "#E9C8D1",
            ["Layal.Color.NavSelected"] = dark ? "#2B385A" : "#EAEDFC",
            ["Layal.Color.NavSelectedBorder"] = dark ? "#5C6DA5" : "#D4DBF4",
            ["Layal.Color.NavHover"] = dark ? "#26344C" : "#F3F5FC",
            ["Layal.Color.ListHover"] = dark ? "#28364E" : "#F3F5FC",
            ["Layal.Color.ListSelected"] = dark ? "#2C3D60" : "#EBEEFC",
            ["Layal.Color.InputFocus"] = dark ? "#202E48" : "#FCFDFF",
            ["Layal.Color.HoverSurface"] = dark ? "#2A3B58" : "#EEF0FC",
            ["Layal.Color.MenuHover"] = dark ? "#2A3B58" : "#EEF0FC",
            ["Layal.Color.MenuHoverText"] = dark ? "#EDF2FF" : "#25314A",
            ["Layal.Color.Shadow"] = dark ? "#080D18" : "#AAB8D3",
            // Glass design: dusk backdrop, translucent panels and one accent used for selection and primary actions.
            ["Layal.Color.BackdropStart"] = dark ? "#27375A" : "#9CAAE0",
            ["Layal.Color.BackdropMiddle"] = dark ? "#403665" : "#B7A9E4",
            ["Layal.Color.BackdropEnd"] = dark ? "#5A4261" : "#E8C1D8",
            ["Layal.Color.Glass"] = dark ? "#F0172038" : "#F0FFFFFF",
            ["Layal.Color.GlassStrong"] = dark ? "#F51A223A" : "#F7FFFFFF",
            ["Layal.Color.GlassBorder"] = dark ? "#6671829C" : "#DCE3F0",
            ["Layal.Color.GlowBlue"] = dark ? "#4E6B89F0" : "#557E8FF0",
            ["Layal.Color.GlowViolet"] = dark ? "#4E9A7DE8" : "#4F9A7DE8",
            ["Layal.Color.GlowPink"] = dark ? "#3FD67AAE" : "#40D67AAE",
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
                    ["Layal.Color.HoverSurface"] = "#25485A", ["Layal.Color.BackdropStart"] = "#143955",
                    ["Layal.Color.BackdropMiddle"] = "#24556B", ["Layal.Color.BackdropEnd"] = "#36426D",
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
                    ["Layal.Color.HoverSurface"] = "#E2F3F8", ["Layal.Color.BackdropStart"] = "#75C8DD",
                    ["Layal.Color.BackdropMiddle"] = "#A2BFEF", ["Layal.Color.BackdropEnd"] = "#D6C9F4",
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
                    ["Layal.Color.HoverSurface"] = "#2B4736", ["Layal.Color.BackdropStart"] = "#244F3B",
                    ["Layal.Color.BackdropMiddle"] = "#37634E", ["Layal.Color.BackdropEnd"] = "#455A70",
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
                    ["Layal.Color.HoverSurface"] = "#E7F4E8", ["Layal.Color.BackdropStart"] = "#97D1AC",
                    ["Layal.Color.BackdropMiddle"] = "#B4DCC1", ["Layal.Color.BackdropEnd"] = "#C5D6F0",
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
