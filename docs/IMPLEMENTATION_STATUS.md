# Layal 0.5.2 — implementation status register

This matrix separates source presence, automated Windows CI evidence, and live interactive acceptance. The authoring environment itself lacks the .NET SDK, but Windows CI run [39825663174](${runUrl}) successfully built the current PR head with 0 warnings/0 errors and ran 73 passing tests. These results prove the CI build/tests, not live input or physical monitor-DPI behavior. Version 0.5.0 updated presentation resources and mascot XAML. Version 0.5.1 added traceability and repeatable build checks. Version 0.5.2 adds automated C# tests and Windows CI execution.

| Requirement | Status in source | Relevant files | Verification |
|---|---|---|---|
| Native WPF application and refreshed floating mascot | Implemented in source, unverified on Windows | `App.xaml`, `Views/FloatingMascotWindow.*` | Needs Windows build/manual test |
| Tray actions, branded icon, live language switching, show/hide and exit | Implemented in source, unverified on Windows | `Services/TrayService.cs`, `App.xaml.cs` | Needs Windows test |
| Arabic/English UI foundation | Implemented, partially localized | `Views/MainWindow.xaml.cs` | Manual RTL/LTR and translation checks needed |
| DPAPI provider key | Implemented in source | `Services/DpapiSecretStore.cs` | Windows DPAPI test needed |
| OpenAI-compatible model probe/chat | Implemented in source | `Services/OpenAiCompatibleClient.cs` | Requires real configured endpoint/key |
| Tool calling client | Implemented in source | `OpenAiCompatibleClient.cs` | Model/provider tool-call compatibility unverified |
| Bounded multi-step agent loop with warning state for tool failure/declined actions | Implemented in source | `Services/AgentOrchestrator.cs`, `Views/MainWindow.xaml.cs` | Integration tests needed |
| Current time/system/workspace tools | Implemented in source | `Services/AgentToolExecutor.cs` | Requires runtime tests |
| File listing/reading/writing/replacement | Implemented with workspace limits and approvals | `AgentToolExecutor.cs` | Security and Windows tests needed |
| .NET build/test tools inside workspace | Implemented with confirmation and timeout | `AgentToolExecutor.cs` | Must test only using trusted projects |
| Window title list/activation | Implemented, opt-in plus confirmation | `AgentToolExecutor.cs`, `UserSettings.cs` | Requires Windows test |
| ZIP skill validation | Implemented in source | `Services/SkillPackageService.cs` | Needs valid/invalid package tests |
| Public GitHub skill directory import | Implemented in source with size limits | `SkillPackageService.cs`, Skills tab | Live GitHub integration unverified |
| Selected skills as untrusted references | Implemented in source, max 3 | `AgentOrchestrator.cs`, Skills tab | Tool/provider integration test needed |
| Encrypted chat/task/memory persistence | Implemented with DPAPI-backed JSON | `ProtectedJsonStore.cs`, memory/task services | Windows account/profile test needed |
| Memory notes CRUD/search/export | Implemented in source | `MemoryService.cs`, Memory tab | UI/runtime tests needed; JSON export is plaintext |
| Actual voice STT/TTS | Not implemented | — | Future milestone |
| General mouse/keyboard/UI Automation | Not implemented | — | Future milestone |
| Screen capture/visual UI control | Not implemented | — | Future milestone |
| Browser DOM automation/research | Not implemented; URL launching exists only | `AgentToolExecutor.cs` | Future milestone |
| RTSP/ONVIF video and computer vision | Not implemented | Camera tab reports limitation | Future milestone |
| MCP protocol integration | Not implemented | — | Future milestone |
| SQLite, vector memory/RAG, model training | Not implemented | — | Future milestone |
| Installer/updater/signing | Not implemented | Publish script only | Future milestone |
| Automated C# test project | Added in 0.5.2, tests not run in authoring environment | `tests/Layal.Tests/` | Must pass Windows CI; actual WPF/voice/camera acceptance remains separate |

## Design/reliability changes carried forward from 0.4.0

- Previous release introduced a dark Aurora system; 0.5.0 replaces it with a soft light palette, white/pastel surfaces, readable dark text, gentler hover states, and subtle shadows.
- Mascot refreshed with a pearl-white shell, layered cyan/violet side modules, orbit details, eye highlights, and a dark high-contrast face display; state-driven expressions and gentle hover/idle animation remain.
- Saved mascot position can include negative coordinates for secondary monitors and is clamped to the virtual desktop.
- Exiting from the tray closes hidden windows so active tasks can receive cancellation.
- Tool failures or declined actions prevent the task from being shown as a clean success.
- Static XAML/resource/event validation script added under `tools/validate_project.py`.

## Required next verification

1. Run `BUILD-LAYAL.bat` on Windows with .NET 10 SDK and fix all compile errors.
2. Test the floating window, tray, language switch, DPAPI storage, and app restart behavior.
3. Verify provider tool calling with a model known to support it; `auto` routing may select a model with different capabilities.
4. Exercise each tool using a disposable test workspace and verify that denied approvals cause no side effects.
5. Test GitHub import with a valid skill folder, invalid YAML, duplicate name, large archive, missing `SKILL.md`, and path/symlink edge cases.
6. Test memory save/edit/search/delete, chat/task history persistence, and plaintext JSON export warnings.
7. Run `dotnet test` on Windows CI and fix any failures before broad use. Current authoring environment lacks .NET SDK, so these tests have not been executed here.


The authoritative current ledger is `MASTER_REQUIREMENT_REGISTER_AR.md`, with a CSV copy in `REQUIREMENT_TRACEABILITY.csv`. Check that ledger before every milestone/release.


## Phased visual-personality work — Stages 1–2 and Stage 3 Part 1 implemented; Part 2 automation in progress, live acceptance pending
- **Stage 1 — articulated 3D avatar:** separate WPF 3D nodes represent the head, torso, upper arms, forearms, hands, thighs, shins and feet. Independent joint transforms support posing; the 3D face includes eye whites, irises, pupils, brows, cheeks and mouth.
- **Stage 2 — cinematic motion and task-synchronized effects:** `MascotEffectStyleResolver` selects distinct motion styles for affection, task-orbit, speech, waiting, celebration, alerts, quiet states and default idle. Task symbols, colors and motion cadence vary by planning/research/reading/writing/coding/tool/validation/waiting/success/error state.
- Celebration particles use staggered eased bursts that fade out rather than repeating endlessly; hearts/flowers drift, task glyphs orbit, speech symbols wave, and warning/error glyphs pulse. Head, torso and joint pose values use damped transitions.
- **Stage 3 Part 1 — automated visual review:** the source now has a theme-aware ComboBox/ComboBoxItem template, a themed scrollbar track/thumb, a themed GroupBox template, stronger muted-text contrast, Latin isolation in Arabic UI text, checks for actually rendered button labels, and a smaller 900×560 DIP minimum window.
- The WPF visual regression test asserts the seven expected tabs exist in the right order and have non-empty viewports; it checks light/dark palette contrast, the chat-history ComboBox, Arabic/English button labels, LTR handling for URLs/API keys/model IDs, and the themed task GroupBox border.
- Latest automated visual package contains **208 PNGs** plus a manifest: 112 standard app renders (7 sections × 2 themes × 2 languages × 4 render scales), 28 compact-window renders (900×560 DIPs), and 68 mascot renders (17 moods × 4 render scales). The application screenshots are composited over the active theme backdrop; mascot-background transparency remains intentional.
- **Stage 3 Part 1 CI verification:** [Windows CI run 39825663174](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39825663174), commit `ea7a7cc62c484f04d8c6086f69c8afaa89c89e74`: Release build **0 warnings / 0 errors**; **73 passed, 0 failed, 0 skipped**. Source/test/visual/release artifacts were uploaded.
- **Stage 3 Part 2 — DPI and interaction acceptance:** implementation and automated test work have started after user approval. The desktop app now declares PerMonitorV2 DPI awareness and enables WPF re-layout after DPI changes; mascot hit-region math is isolated and regression-tested, and settings round-trip coverage includes persisted theme/language and mascot placement/options. Windows CI is required to validate these changes.
- **Live hardware gate remains pending:** actual monitor scale changes at 100/125/150/175%, secondary-monitor movement, real input/click-through behavior, keyboard focus, and restart persistence must still be exercised and recorded on an interactive Windows desktop. RenderTargetBitmap density captures do not simulate physical monitor DPI.
- The avatar remains procedural WPF 3D geometry/scene nodes, not an imported artist-authored mesh or DCC bone rig. The theme-aware gradients/cards provide a restrained glass-like finish, not true desktop acrylic blur.
- Plan: `docs/PHASED_EXECUTION_AR.md`. Manual checklist: `docs/MANUAL_ACCEPTANCE_AR.md`. PR #6 remains draft and unmerged; Part 2 must wait for the user's explicit approval.
## Version 0.5.2 — automated regression test foundation

- Added MSTest-based suites for SettingsStore defaults/normalization/round-trip/corrupt-file recovery.
- Added skill ZIP tests for valid metadata, duplicate name policy, invalid front matter/name, missing SKILL.md, bounded Markdown reads, and invalid GitHub URL formats.
- Added agent-tool tests for unique tool definitions, approval denial side effects, workspace traversal rejection, create-file overwrite protection, and project-file validation before build approval.
- Added OpenAI-compatible HTTP client tests using an injectable HttpClient factory and fake HTTP handler, including authorization, endpoint construction, chat parsing, tool-call parsing, and invalid URL protection.
- Added Windows DPAPI-backed memory tests for save/search/update/delete persistence and JSON export; these are expected to run on Windows CI only.
- Expanded static validation to scan C# test sources and validate the test-project configuration.
- Updated Windows GitHub Actions to restore, build, run tests, and upload TRX results.
- **Execution status:** source written and statically checked; `dotnet test` has not run in the current authoring environment because .NET SDK is unavailable. Do not mark the test requirement verified until Windows CI shows a passing result.
