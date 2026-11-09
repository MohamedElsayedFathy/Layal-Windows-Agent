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
            ["Layal.Color.Background"] = dark ? "#263451" : "#F3F6FF",
            ["Layal.Color.BackgroundEnd"] = dark ? "#2D3D60" : "#E8EEFC",
            ["Layal.Color.Surface"] = dark ? "#2D3B5B" : "#FFFFFF",
            ["Layal.Color.SurfaceRaised"] = dark ? "#354564" : "#FAFBFF",
            ["Layal.Color.SurfaceHover"] = dark ? "#3E5072" : "#EEF2FF",
            ["Layal.Color.Border"] = dark ? "#5B6D8F" : "#D5DCF0",
            ["Layal.Color.Cyan"] = dark ? "#9CCBFF" : "#315FC1",
            ["Layal.Color.Purple"] = dark ? "#C6ACFF" : "#7351B8",
            ["Layal.Color.Text"] = dark ? "#F2F5FF" : "#1D2B49",
            ["Layal.Color.Muted"] = dark ? "#C7D2E8" : "#53627D",
            ["Layal.Color.ScrollThumb"] = dark ? "#A2B2D1" : "#8291AF",
            ["Layal.Color.Success"] = dark ? "#8EE0B2" : "#17734F",
            ["Layal.Color.Warning"] = dark ? "#FFD184" : "#8A5500",
            ["Layal.Color.Danger"] = dark ? "#FFADB7" : "#A8324A",
            ["Layal.Color.Focus"] = dark ? "#B5C7FF" : "#4368D2",
            ["Layal.Color.HeaderStart"] = dark ? "#E5293A65" : "#FFF8FB",
            ["Layal.Color.HeaderMiddle"] = dark ? "#E6334070" : "#F6F3FF",
            ["Layal.Color.HeaderEnd"] = dark ? "#E83F3E70" : "#EDF4FF",
            ["Layal.Color.CardStart"] = dark ? "#E82F416F" : "#FFFFFFFF",
            ["Layal.Color.CardEnd"] = dark ? "#E839416C" : "#F5F5FF",
            ["Layal.Color.ButtonStart"] = dark ? "#415A85" : "#DCE7FF",
            ["Layal.Color.ButtonEnd"] = dark ? "#604C83" : "#E9E0FB",
            ["Layal.Color.BrandBorder"] = dark ? "#7489B5" : "#BCCBF0",
            ["Layal.Color.BrandDot"] = dark ? "#FFD184" : "#D59A45",
            ["Layal.Color.DangerSurface"] = dark ? "#59313E" : "#FCECEF",
            ["Layal.Color.DangerBorder"] = dark ? "#8B5061" : "#E7BBC4",
            ["Layal.Color.NavSelected"] = dark ? "#3B4A73" : "#E2E9FF",
            ["Layal.Color.NavSelectedBorder"] = dark ? "#8C9EF3" : "#B6C4F0",
            ["Layal.Color.NavHover"] = dark ? "#334766" : "#ECF0FF",
            ["Layal.Color.ListHover"] = dark ? "#334766" : "#ECF0FF",
            ["Layal.Color.ListSelected"] = dark ? "#3B4972" : "#DBE5FF",
            ["Layal.Color.InputFocus"] = dark ? "#2E4165" : "#FBFCFF",
            ["Layal.Color.HoverSurface"] = dark ? "#3E5072" : "#EEF2FF",
            ["Layal.Color.MenuHover"] = dark ? "#405175" : "#E9EDFF",
            ["Layal.Color.MenuHoverText"] = dark ? "#F2F5FF" : "#1D2B49",
            ["Layal.Color.Shadow"] = dark ? "#202D49" : "#7A86A2",
            // A colored aurora of sapphire, violet and teal; the dark theme stays visibly blue, never black.
            ["Layal.Color.BackdropStart"] = dark ? "#263C67" : "#E3EEFF",
            ["Layal.Color.BackdropMiddle"] = dark ? "#403C70" : "#EFE9FF",
            ["Layal.Color.BackdropEnd"] = dark ? "#523B66" : "#E8F6F4",
            ["Layal.Color.Glass"] = dark ? "#E5293A65" : "#F5FFFFFF",
            ["Layal.Color.GlassStrong"] = dark ? "#F32D3B65" : "#FCFFFFFF",
            ["Layal.Color.GlassBorder"] = dark ? "#657BA9" : "#D0D9EF",
            ["Layal.Color.GlowBlue"] = dark ? "#4A5D91F2" : "#30487AE5",
            ["Layal.Color.GlowViolet"] = dark ? "#408C65E8" : "#258E62E6",
            ["Layal.Color.GlowPink"] = dark ? "#34D67DB0" : "#20DA8EA7",
            ["Layal.Color.AccentStart"] = dark ? "#507AE0" : "#3763C9",
            ["Layal.Color.AccentEnd"] = dark ? "#8A65DB" : "#7857C6",
            ["Layal.Color.OnAccent"] = "#FFFFFF",
            ["Layal.Color.IconChatBackground"] = dark ? "#354875" : "#E4EBFF",
            ["Layal.Color.IconChatForeground"] = dark ? "#AFC5FF" : "#315EC3",
            ["Layal.Color.IconOverviewBackground"] = dark ? "#28564E" : "#DDF5EF",
            ["Layal.Color.IconOverviewForeground"] = dark ? "#9CE6D7" : "#197A68",
            ["Layal.Color.IconTasksBackground"] = dark ? "#614A31" : "#FFF0D8",
            ["Layal.Color.IconTasksForeground"] = dark ? "#FFD08B" : "#A85D0A",
            ["Layal.Color.IconSkillsBackground"] = dark ? "#4D3F6B" : "#F0E8FF",
            ["Layal.Color.IconSkillsForeground"] = dark ? "#D2B8FF" : "#7651B9",
            ["Layal.Color.IconCamerasBackground"] = dark ? "#623B4A" : "#FFE7E9",
            ["Layal.Color.IconCamerasForeground"] = dark ? "#FFB5B9" : "#B34855",
            ["Layal.Color.IconMemoryBackground"] = dark ? "#2E526C" : "#DCF4FF",
            ["Layal.Color.IconMemoryForeground"] = dark ? "#9EDFFF" : "#136B91",
            ["Layal.Color.IconApiBackground"] = dark ? "#4C5A34" : "#EDF5D9",
            ["Layal.Color.IconApiForeground"] = dark ? "#D2E99A" : "#4D6B23",
            ["Layal.Color.IconSettingsBackground"] = dark ? "#46536B" : "#E8EDF5",
            ["Layal.Color.IconSettingsForeground"] = dark ? "#C1D1E8" : "#52617B"
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
            ["Layal.Color.OnAccent"] = SystemColors.HighlightTextColor.ToString(),
            ["Layal.Color.IconChatBackground"] = window,
            ["Layal.Color.IconChatForeground"] = hotTrack,
            ["Layal.Color.IconOverviewBackground"] = window,
            ["Layal.Color.IconOverviewForeground"] = hotTrack,
            ["Layal.Color.IconTasksBackground"] = window,
            ["Layal.Color.IconTasksForeground"] = hotTrack,
            ["Layal.Color.IconSkillsBackground"] = window,
            ["Layal.Color.IconSkillsForeground"] = hotTrack,
            ["Layal.Color.IconCamerasBackground"] = window,
            ["Layal.Color.IconCamerasForeground"] = hotTrack,
            ["Layal.Color.IconMemoryBackground"] = window,
            ["Layal.Color.IconMemoryForeground"] = hotTrack,
            ["Layal.Color.IconApiBackground"] = window,
            ["Layal.Color.IconApiForeground"] = hotTrack,
            ["Layal.Color.IconSettingsBackground"] = window,
            ["Layal.Color.IconSettingsForeground"] = hotTrack
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
