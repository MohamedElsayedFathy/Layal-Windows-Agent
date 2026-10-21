# Layal 0.5.0 — source review and light-theme update

## Scope

Reviewed the 0.4.0 source archive, especially App.xaml styles, all explicit colors in MainWindow.xaml, mascot XAML/name dependencies, and status-brush usage in code-behind. Applied a light-theme and mascot update, then re-ran portable static checks. No Windows build is claimed because the .NET SDK is unavailable here.

## Changes made

### Visual design

- Replaced the dark palette with a calm light palette: cool off-white background, white content surfaces, light blue-gray borders, dark slate text, and restrained cyan/purple accents.
- Updated status colors for legibility on light surfaces; warning and success text now use deeper tones instead of pale yellow/green. Calculated key foreground/background contrast ratios are documented in `docs/LIGHT_THEME_ACCESSIBILITY.md`.
- Replaced the horizontal tab strip with a fixed left navigation rail and selected-state marker.
- Updated button templates for rounded corners and hover/pressed/disabled states.
- Updated text/password input templates with rounded surfaces and focus indication.
- Improved content cards and message/task list surfaces with soft gradients, subtle shared shadows, borders, and larger corner radii.
- Increased the default main window size and improved spacing/readability for desktop use.

### Floating mascot

- Refreshed the native mascot drawing with a pearl-white outer shell, cyan/violet gradient trim, a high-contrast dark face display, orbital marks, layered sensor pods, soft halo, brighter eye highlights, cheeks, and refined feet. Regenerated the tray icon and added a PNG visual reference (`Assets/layal-mascot-modern-preview.png`).
- Added state-specific facial expressions for listening, transcribing, understanding, curious, thinking/planning/researching/reading/coding, tool use, waiting, speaking, success, warning, error, paused/interrupted, offline, privacy, reconnecting/recovering, camera processing, sleepy, and excited states.
- Added gentle antenna/glow motion and mouth changes while the app's mood is `speaking`.
- Added a subtle curious expression on hover when the agent is idle.
- Switched to a `Viewbox` for mascot scaling so the design scales once instead of applying both a larger window and a second layout transform.
- Improved restoration/clamping of stored mascot position across the virtual desktop, including negative coordinates commonly used by secondary monitors.
- Updated the system-tray icon to use an ICO generated from the supplied mascot reference and synchronized its menu language when the UI language changes.

### Reliability and state reporting

- App exit now closes the main window even if it had previously been hidden; the existing close handler can then cancel active requests.
- Tool errors and user-declined tool actions are now surfaced as `CompletedWithWarnings` when the model later returns a final answer, instead of reporting a clean `Completed` state.
- Kept the existing tool allow-list, user confirmations, bounded task rounds/tool calls, DPAPI-backed local state, and refusal to execute imported skill scripts.

## Important existing limitations retained and made explicit

- The app has a native WPF shell, but there is still no general Windows UI Automation/mouse/keyboard controller or screen-capture tool.
- Voice STT/TTS, browser DOM automation/research, RTSP/ONVIF camera streams and video inference, MCP protocol execution, SQLite/RAG, model training, and installer/updater are not implemented in this milestone.
- The AI task runner depends on a provider/model that genuinely supports OpenAI-compatible function/tool calling. A successful `/models` probe does not prove that tool calling works.
- No independent C# automated test project is included yet.

## Remaining verification

The source received static XAML/resource/event/delimiter checks. The environment has no .NET SDK, so no compilation, runtime launch, Windows API test, provider test, or installer test was possible here. Run `BUILD-LAYAL.bat` on Windows and resolve compiler errors before relying on the new UI or agent functionality.
