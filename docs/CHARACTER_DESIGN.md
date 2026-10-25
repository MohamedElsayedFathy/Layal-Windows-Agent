# Layal character (official look)

The floating mascot shows **Layal** (blue-silver hair, crescent clip, white tech jacket) with her
**robot assistant** (pearl shell, dark glass screen, crescent on top). Artwork lives in
`src/Layal.Desktop/Assets/Characters` and is embedded as WPF resources (no network at runtime).

## How it works
- `Services/CharacterExpressionMap.cs` maps every runtime mood (the same names `TaskMoodResolver` and
  the UI produce) to a Layal portrait and a robot face. It is plain C# and covered by
  `CharacterExpressionMapTests`.
- `FloatingMascotWindow` swaps Layal's portrait and draws the robot's eyes/mouth as vector shapes over a
  blank copy of its screen, so the robot can express states the artwork never contained.
  Warning/error/waiting also add a glyph (`!`, `x`, `z`, `?`) so state never depends on colour alone.
- Motion: slow breathing, a drifting/tilting robot that moves closer while a task runs, robot blinking.
  Everything stops when "animations" is off in Layal or Windows' "Show animations" is off.
- The welcome banner on the Home tab shows Layal with the smiling robot (decorative: not focusable, not clickable and without an automation name, so nothing is announced for it).
- The previous procedural 3D rig is still in the project, collapsed. Set `UseSpriteCharacter` to `false`
  in `FloatingMascotWindow.xaml.cs` to bring it back; if the artwork cannot load it is restored automatically.

## Known limits (be honest about these)
- The concept sheet only contains six expressions plus the default pose (happy, curious, playful, focused,
  surprised, calm). Moods without art (sad, worried, error...) use the closest portrait while the robot's face
  carries the specific state. There is no sad/worried portrait of Layal yet.
- Layal's portraits have no blink/mouth frames, so Layal does not blink or lip-sync; only the robot blinks.
- Expression portraits were cut from 160 px tiles and are softer than the main pose, especially at high DPI
  or large mascot sizes. Provide higher-resolution renders to improve them.
- The single front/side/back full-body views from the sheet are not used.
- The source sheet now has embedded C2PA provenance evidence identifying ChatGPT / `gpt-image` / OpenAI Media Service API, but its manifest signature has not been independently validated and human rights review remains pending. The nine derivatives are marked `PROVENANCE_EVIDENCED_RIGHTS_REVIEW_REQUIRED`; the source evidence does not authorize release by itself. See `docs/ASSET_PROVENANCE_FINDINGS_AR.md`.
