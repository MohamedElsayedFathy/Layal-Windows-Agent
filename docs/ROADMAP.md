# Layal implementation roadmap

## 0.1 — Native foundation
- [x] WPF application skeleton and startup wiring.
- [x] Transparent floating mascot and tray actions.
- [x] Arabic/English interface foundation.
- [x] Settings and DPAPI-protected provider key.
- [x] OpenAI-compatible provider probe and chat request.
- [x] Limited Notepad launch.
- [x] ZIP skill metadata validation.
- [x] Windows Release build and automated C# tests on CI; manual runtime acceptance remains pending.

## 0.3 — Bounded agent runtime and local state (previous source milestone)
- [x] OpenAI-compatible tool/function-call request and response parsing.
- [x] Bounded agent loop (8 model rounds / 12 tool calls per task).
- [x] Approval callback for sensitive or external actions.
- [x] Task event log and DPAPI-encrypted task history.
- [x] Workspace-relative file list/read/create/exact-replace tools.
- [x] Restricted dotnet build/test tool with confirmation and timeout.
- [x] Optional window-title inspection and approved window activation.
- [x] Public GitHub skill importer for repository/tree/blob SKILL.md URLs.
- [x] Up to three selected skill references per task; scripts are not executed.
- [x] DPAPI-encrypted memory notes and conversation history.
- [x] Windows CI build and C# tests pass; [ ] manual end-to-end verification on a Windows desktop.

## 0.4.0 — UI refresh and reliability review (previous milestone)
- [x] Modernized dark Aurora theme and rounded input/button controls.
- [x] Left-side navigation rail and refined card surfaces.
- [x] More expressive mascot states, hover curiosity, gentle antenna/glow animation.
- [x] Viewbox-based mascot scaling and secondary-monitor position handling.
- [x] Branded tray icon and language updates for tray menu.
- [x] Close hidden windows on exit and allow active requests to cancel.
- [x] Mark final task status with warnings after tool failures or declined actions.
- [x] Add repeatable static XAML/resource/event checks.
- [x] Windows CI build and automated C# tests; [ ] manual runtime verification.

## 0.5.0 — Calm Light theme and mascot refresh (previous milestone)
- [x] Replaced the dark application palette with a light, low-glare palette and high-contrast text.
- [x] Updated button, input, list, and navigation hover/selection states for the light theme.
- [x] Added soft surface shadows to primary panels and content cards.
- [x] Redesigned the floating mascot shell with pearl materials, cyan/violet trim, orbital details, layered sensor pods, and a high-contrast face display.
- [x] Re-ran static XML/resource/name/event/delimiter checks.
- [ ] Compile and visually verify on Windows across DPI and monitor configurations.

## 0.5.2 — Automated test foundation
- [x] Settings defaults, normalization, persistence, and damaged-file recovery tests.
- [x] Skill package import/validation and duplicate-name tests.
- [x] Workspace-tool approval/path/overwrite tests.
- [x] OpenAI-compatible endpoint, authorization, response parsing, and tool-call parsing tests using a fake HTTP handler.
- [x] Windows DPAPI memory tests (to execute on Windows CI).
- [x] CI now runs `dotnet test`, validates non-empty TRX results, and stores test/source artifacts.
- [x] Historical CI checkpoint before the suite reached 64 tests: Release build and 61 tests passed; superseded by Windows CI run 39764756111, which passed the current reviewed head.

## 0.6 — Native automation, API integrations, and safety hardening
- [x] Read-only UI Automation inspection of a uniquely titled external window, with per-request approval, metadata-only output, a background worker, and an 8-second caller timeout; Windows runtime acceptance remains pending.
- [x] One-control UI Automation InvokePattern action for a unique exact AutomationId on an external window, fresh per-action approval, 8-second bound, and post-action metadata observation; real-app acceptance remains pending.
- [x] Generic user-configured REST/JSON app API connections with DPAPI-protected profiles, no-auth/Bearer/custom-header auth, bounded request and response sizes, and local per-request approval; Windows CI/runtime acceptance pending.
- [x] Bounded Windows UI Automation: read-only inspect, one approved InvokePattern action, one approved ValuePattern text entry into an exact unique non-password Edit field, and one approved TogglePattern state-setting action for an exact unique two-state CheckBox; manual Windows acceptance remains pending.
- [x] Verified current checkbox/browser/API path guard snapshot in Windows CI run 39764756111 on head ffb49438: Release build passed and all 64 C# tests passed (0 failed, 0 skipped); this does not replace manual Windows acceptance.
- [ ] General mouse/keyboard input, coordinate actions, screen capture approvals, and evidence-based post-action outcome checks.
- [ ] More robust restricted process runner and recovery tests.
- [ ] Threat model and automated security tests for workspace/skill handling.

## 0.7 — Voice and localization validation
- [ ] STT/TTS providers and audio device controls.
- [ ] Egyptian Arabic, MSA, English, mixed-language test corpus.
- [ ] Microphone permissions, mute, interruption, and privacy indicators.

## 0.8 — Browser/research
- [x] Initial dedicated Microsoft Edge/CDP adapter: approved navigation, bounded page text/links/buttons/fields, and approved selector click/fill. Manual Windows acceptance remains pending.
- [ ] Source-grounded research with citations, downloads/uploads, complex tab/frame handling, and provenance logs.

## 0.9 — Skills and MCP
- [ ] Dependency/license inspection and provenance reporting.
- [ ] Sandboxed skill script execution only if a real isolation boundary is verified.
- [ ] MCP client configuration, tool discovery, and permissions.

## 0.10 — Camera and vision
- [ ] RTSP/ONVIF device profiles and actual stream decoding.
- [ ] Authorized hardware test suite.
- [ ] Vision inference, event retention, and resource limits.

## 0.11 — Memory/training
- [x] Basic encrypted user-managed memory notes.
- [ ] SQLite migration layer and richer project memory/RAG.
- [ ] Dataset manager, optional training workflow, evaluation, model versioning and rollback.

## 1.0 — Release
- [ ] Full tests and security review.
- [ ] Installer, update/recovery, and uninstall tests.
- [ ] Verified release artifacts and user documentation.
