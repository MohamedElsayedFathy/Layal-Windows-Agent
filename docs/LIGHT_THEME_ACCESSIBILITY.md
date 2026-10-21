# Layal 0.5.0 — light theme contrast notes

The revised theme uses a low-glare cool-white background, white panels, blue-gray separators, dark slate text, and restrained cyan/lilac accents. The mascot keeps a dark face display so the eyes and expressions remain legible against its pearl shell and both light and dark desktop backgrounds.

## Main palette

| Purpose | Color | Intended use |
|---|---|---|
| Application background | `#F4F7FC` | Cool, low-glare window surface |
| Panel | `#FFFFFF` | Cards, content and inputs |
| Main text | `#1C2940` | Body text and headings |
| Muted text | `#63738A` | Helper text and labels |
| Cyan accent | `#087B8F` | Links, focus, selected markers |
| Purple accent | `#7563D6` | Secondary emphasis and mascot detail |
| Success | `#177A57` | Successful status text |
| Warning | `#946200` | Warning status text |
| Danger | `#BD3C55` | Error/destructive status text |
| Primary surface gradient | `#A3E3E7` → `#C8C0F7` | Primary controls with dark text |

## Calculated contrast checks

Contrast ratios were calculated from the listed sRGB colors using the WCAG relative-luminance formula. Approximate ratios:

- Main text `#1C2940` on background `#F4F7FC`: **13.58:1**.
- Main text `#1C2940` on white: **14.58:1**.
- Muted text `#63738A` on white: **4.83:1**.
- Cyan `#087B8F` on white: **4.95:1**.
- Success `#177A57` on white: **5.31:1**.
- Warning `#946200` on white: **5.24:1**.
- Danger `#BD3C55` on white: **5.33:1**.
- Main text on both ends of the primary gradient: **10.20:1** and **8.57:1**.

These are color-pair calculations, not a substitute for checking the rendered UI, disabled controls, thin focus indicators, icons, and text over gradients on a real Windows display.

## Mascot appearance

The mascot uses a pearl-white/light-lavender outer shell and layered cyan/violet modules around a dark high-contrast face display. The outer shell, aura, orbit dots, eye highlights, cheeks, side pods, status ring, and small feet are vector elements so facial states can continue to update in WPF. The redesign preserves the existing named controls expected by the state controller.
