> **Current status notice — 13 November 2026:** This English roadmap is a historical milestone/backlog reference. Its older checkbox statuses and baseline are superseded. The authoritative live implementation status, completed work, pending acceptance gates, unimplemented capabilities, and next execution order are maintained in [the updated Arabic master roadmap execution log](MASTER_ROADMAP_EXECUTION_AR.md) at `main`, baseline commit `4df2a067c4c6df161d140e2f06d252745c1f8d3b`. Do not use the historical checkboxes below as proof that a feature is currently complete.
>
> The original user-provided roadmap remains preserved in `docs/reference` and is not replaced by this status update.

# Layal implementation roadmap

## Master development roadmap execution — 2026-11-12

The user-supplied Arabic master roadmap is tracked separately from this source-milestone roadmap. Its repository baseline was stale: current `main` is `6beb0c909ecab29171395c071012cbb863b4f182` (PR #155), not the November 9 commit named in that document.

| Package/status | Source | Automated verification | Practical acceptance | Documented acceptance |
|---|---|---|---|---|
| Phase -1 / UI-00: opaque Soft Aurora window, DWM backdrop disabled, theme contrast and glass opacity | Present on `main`; related changes were merged before the current baseline (including PRs #146–147). Do not reapply them without a newly verified gap. | Windows CI on the PR #155 source head passed Release checks and 652/652 C# tests; visual smoke covered automated RTL/LTR, theme, and scale scenarios. | Still pending on the target desktop: inspect the complete window in light/dark mode, resize/drag/title controls, and check real RTL/LTR and DPI behavior. CI screenshots are not physical-device acceptance. | Pending until the practical acceptance record and evidence are attached.
| Phase 0: refresh baseline and reconcile status | Baseline refreshed to `6beb0c909ecab29171395c071012cbb863b4f182`; PR #155 is merged. The repository audit is recorded in `docs/TECHNICAL_REVIEW_2026-11-12_AR.md`. | Windows CI: 652/652 C# tests; 18/18 Python checks; security, supply-chain, and automated acceptance runs passed on PR #155 source head `0a7832aeea87884647543b559e2a330dc33f0c24`, which is an ancestor of current `main`. | Hardware-dependent checks remain pending: Narrator/NVDA, Windows High Contrast, physical multi-monitor/DPI transitions, and live peripherals. | Updated source baseline and this roadmap status are recorded; manual evidence remains open.

### Architecture decision to resolve before model-runtime work

The supplied master roadmap describes a local model as Layal's primary cognitive engine. Current `main` intentionally has no conversational LLM in the text or task path and fails closed on unsupported requests. Reconcile the desired local-model behavior, model-independent interfaces, privacy controls, and approval policy before Phase 2 changes. Do not describe the future local-model architecture as current behavior.

### Next package gate

Close Phase -1/UI-00 practical acceptance on a real Windows desktop and record its environment, commit, RTL/LTR screenshots, supported window sizes, DPI, and retained interactions before starting the next package. Keep this work on a separate branch and leave `main` unchanged until the user approves a merge.

## 0.5.8 — Native model-independent cognitive core (merged to main)
## 0.5.8 — Native model-independent cognitive core (merged to main)
- [x] Route text chat and Quick Ask through `NativeConversationEngine`; no hosted/local chat model is called.
- [x] Route supported tasks and saved command routines through `NativeLanguageParser` and `NativeAgentOrchestrator`.
- [x] Bound plans to a maximum of six explicit command lines; parse the complete sequence before the first side effect and require fresh approval for consequential actions.
- [x] Verify created files by a separately approved local read-back and keep external launches/navigation unverified until observed.
- [x] Persist explicit user-taught aliases and bounded outcome signals locally; this is not model-weight training or open-ended self-learning.
- [x] Keep Whisper/Piper optional and restricted to local speech input/output; never use them for text reasoning or task planning.
- [x] Fail closed for unsupported intents and independent vision; no fallback to Ollama, Qwen, or a hosted chat model.
- [x] Merge PR #154 to `main` at `debf137129f6324b939f6c9100f7441d6d18a8d1`; close model-dependent PR #153 without merging.
- [x] Windows CI on main: Release build/static checks and 644/644 C# tests passed; security and supply-chain checks passed.
- [ ] Implement broader language understanding, adaptive planning/recovery, independent native vision, and evaluated statistical learning without an external cognitive-model dependency.
- [ ] Complete physical Windows accessibility (Narrator/NVDA and High Contrast), multi-monitor/DPI, signing, and asset/license acceptance before public release.


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
- [x] Folded (`>`, `>-`) and literal (`|`, `|-`) YAML description scalars covered by regression assertions; external GitHub import still needs manual acceptance.
- [x] Workspace-tool approval/path/overwrite tests.
- [x] OpenAI-compatible endpoint, authorization, response parsing, and tool-call parsing tests using a fake HTTP handler.
- [x] Windows DPAPI memory tests (to execute on Windows CI).
- [x] CI now runs `dotnet test`, validates non-empty TRX results, and stores test/source artifacts.
- [x] Historical CI checkpoint before the suite reached 64 tests: Release build and 61 tests passed; superseded by Windows CI run 39764756111, which passed the current reviewed head.

## 0.5.3 — Reactive mascot personality (this branch)
- [x] Official Layal character with robot assistant (artwork, mood-driven expressions, welcome banner). Source implemented; Windows build/CI and desktop acceptance pending; asset provenance unconfirmed.
- [x] Show lightweight animated task props (work sheet, pencil, holographic computer) for working moods in the floating mascot.
- [x] Add eight additional face-driven moods and matching non-repeating emoji reaction groups.
- [x] Map planning, research, reading, writing, coding, tool use, and validation events to distinct facial acting.
- [x] Extract task-stage routing into a testable resolver with coverage for failures and declined approvals.
- [x] Apply the selected interface language to Layal's response voice and test Arabic/English prompt selection.
- [x] Keep task props visible but static when decorative animation is disabled.
- [ ] Pass Windows Release build and automated regression tests on this branch.
- [ ] Manually verify click/drag/double-click, display scaling, task transitions, and reduced-motion behavior on Windows.
- [x] Replace the static shell group with separate 3D head, torso, arm and leg rig nodes that receive independent transforms.
- [x] Add 3D eye/iris/pupil, brow, cheek and mouth geometry; drive gaze, blink scaling and facial shapes from mood and pointer input.
- [x] Add lightweight animated heart, flower, sparkle and technology glyph particles chosen by the current mood/task stage.
- [x] Emphasize paper/pencil/hologram props by task phase and vary head/body/arm/leg motion with task context.
- [x] Add a soft background gradient and restrained translucent/glossy header and card surfaces for both themes.
- [ ] Visually verify the articulated avatar alignment, lighting, movement bounds and reduced-motion behavior on Windows.
- [ ] Redesign and visually validate every application section in both themes and at multiple Windows display scaling factors.

## 0.6 — Native automation and safety hardening
- [ ] Windows UI Automation adapter for approved apps.
- [ ] Explicit mouse/keyboard adapter, screen capture approvals, and outcome checks.

## Security hardening — Phase 2: skill package import hardening and workspace trust model

**Status: merged into `main` by PR #133 (merge commit `4dc13a4570de31e22cf50e7f57ce19c26005cd7a`).** Skill-package ingestion hardening and its adversarial tests are complete. Workspace restore implementation was not changed in Phase 2 and is being addressed separately in Phase 3.

- [x] Stage each selected skill ZIP once under the managed skill root, then validate and install those same staged bytes.
- [x] Validate every local ZIP member, not only `SKILL.md`: reject rooted/traversal/Windows-special paths, reparse/special entries, duplicate or case-colliding paths, file/directory conflicts, ambiguous multiple `SKILL.md` files, more than 200 entries, files above 5 MiB, and expanded packages above 10 MiB.
- [x] Revalidate managed package paths and their existing ancestor chain before reading skill content; reject index entries or package paths that escape the managed catalog or traverse reparse points.
- [x] Add adversarial skill-import regressions for archive traversal, reserved ZIP paths and reserved skill metadata names (including index and read-record validation), alternate streams, ZIP path collisions, symbolic links, ambiguous metadata, entry-count and expanded-size limits, catalog ancestor links, and redirected package paths.
- [x] Document trust boundaries, threats, mitigations, and residual risks in [the workspace/skills threat model](SECURITY_THREAT_MODEL_WORKSPACE_SKILLS_AR.md).
- [x] Pass Windows Release build, full C# suite (610 passed, 0 failed, 0 skipped), static project validation, and security/supply-chain workflows on PR #133 head `e5404ead4ebb03ee4a173f7172c6105727ba499d`; Windows build run [42466831918](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42466831918), final acceptance run [42466832904](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42466832904), security run [42466831916](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42466831916).
- [ ] Manually verify skill import/read and package replacement on a Windows desktop; manual screen-reader/High Contrast, multi-monitor DPI, signing, and legal gates remain independent.

This phase does not enable skill script execution, provide a sandbox, or close release signing/license/manual-acceptance gates.

## Security hardening — Phase 3: workspace backup and restore path safety

**Status: automated implementation and CI complete on PR #134 head `c6d61643a55b32c3d9282c681e7231ba4f7fc71c`; manual Windows acceptance remains open.** Phase 3 closes a restore-path gap left explicit by Phase 2: an existing symbolic-link/junction ancestor could be followed when `RestoreWorkspaceFiles` created a missing workspace root before checking its ancestors.

- [x] Remove the early workspace-root creation and validate every existing path component before creating the next directory component during restore.
- [x] Create missing restore directories one component at a time, validate the created component, and track created directories for compensation rollback.
- [x] Add Windows regression coverage proving restore refuses a linked ancestor before creating the missing workspace root, refuses a workspace root that is itself a reparse point (including a dangling directory symlink), still restores into a safe missing nested root, and removes newly created directories on a later-store rollback.
- [x] Pass Windows Release build, 615 C# tests (0 failed, 0 skipped), static validation, and security/supply-chain workflows on PR #134 head `c6d61643a55b32c3d9282c681e7231ba4f7fc71c`; [Windows build run 42469874123](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42469874123), [final acceptance run 42469874062](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42469874062), and [security run 42469874061](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42469874061) all succeeded.
- [ ] Add/record manual Windows evidence for a safe restore, refusal through a junction/symlink, no overwrite of existing files, and rollback on a failed store commit; multi-monitor/screen-reader acceptance, signing, and legal gates remain separate.

The path checks reduce accidental and adversarial link traversal before directory creation, but they are not handle-relative filesystem operations and do not eliminate every same-user TOCTOU race. Backup JSON containing embedded file bytes remains unencrypted; signing and legal/release gates are unaffected.

## 0.6 — Native automation, API integrations, and safety hardening
- [x] Read-only UI Automation inspection of a uniquely titled external window, with per-request approval, metadata-only output, a background worker, and an 8-second caller timeout; Windows runtime acceptance remains pending.
- [x] One-control UI Automation InvokePattern action for a unique exact AutomationId on an external window, fresh per-action approval, 8-second bound, and post-action metadata observation; real-app acceptance remains pending.
- [x] Generic user-configured REST/JSON app API connections with DPAPI-protected profiles, no-auth/Bearer/custom-header auth, bounded request and response sizes, and local per-request approval; Windows CI/runtime acceptance pending.
- [x] Bounded Windows UI Automation: read-only inspect, one approved InvokePattern action, one approved ValuePattern text entry into an exact unique non-password Edit field, and one approved TogglePattern state-setting action for an exact unique two-state CheckBox; manual Windows acceptance remains pending.
- [x] Verified API encoded-backslash guard and folded/literal skill-description parsing in Windows CI run 39765678941 on head fd2cb632: Release build passed and all 64 C# tests passed (0 failed, 0 skipped); this does not replace manual Windows acceptance.
- [ ] General mouse/keyboard input, coordinate actions, screen capture approvals, and evidence-based post-action outcome checks.
- [ ] More robust restricted process runner and recovery tests.
- [x] Phase 2 threat model and automated skill-import security tests; workspace restore path hardening continues in Phase 3.

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
- [ ] Independent native image understanding, still-frame analysis, scene descriptions, and automatic visual observations; the old provider-backed image route is disabled in the native-core milestone.
- [x] User-selected image, clipboard image, and screen-crop local preview without forwarding image bytes to any model.
- [x] Local webcam and ONVIF/RTSP/RTSPS preview using user-supplied authorized credentials, restricted to private IP endpoints; hardware acceptance remains pending.
- [ ] Raw-video inference, room mapping/3D scanning, object tracking, recording, event retention, and measured resource limits.
- [ ] Authorized hardware test suite.

## 0.11 — Memory/training
- [x] Basic encrypted user-managed memory notes.
- [ ] SQLite migration layer and richer project memory/RAG.
- [ ] Dataset manager, optional training workflow, evaluation, model versioning and rollback.

## 1.0 — Release
- [ ] Full tests and security review.
- [ ] Installer, update/recovery, and uninstall tests.
- [ ] Verified release artifacts and user documentation.


## Security hardening — Phase 4: immutable GitHub skill source and package integrity

**Scope:** close the source-revision gap recorded by the Phase 2 threat model without enabling script execution or claiming publisher identity.

- [x] Resolve a user-selected GitHub branch (or repository default branch) through GitHub's commit API and download the archive using the returned immutable 40-character commit SHA; fail closed instead of guessing `main` or `master`.
- [x] Persist the repository, requested ref, resolved commit SHA, selected skill directory, resolution timestamp, SHA-256 of the downloaded repository archive, and SHA-256 of the managed skill package.
- [x] Verify the managed package SHA-256 before reading `SKILL.md`; preserve compatibility with older local index records that predate digest fields.
- [x] Reject GitHub import URLs containing credentials, custom ports, query strings, or fragments. Regression tests cover commit resolution, default-branch selection, malformed commit SHA rejection, symlink rejection, provenance persistence, package tamper detection, legacy-index compatibility, and rejecting ambiguous repository-root imports unless an exact skill directory is supplied.
- [x] Pass Windows Release build, static validation, the full C# regression suite (**624 passed, 0 failed, 0 skipped**), native input/DPI/keyboard/GLB checks, package build, and installer lifecycle on the final code commit: [Windows build #1370](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42474157425).
- [x] Pass PowerShell final acceptance on the final code commit: [final acceptance #36](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42474157419). Final automated decision: PASS; manual/external gates remain open.
- [x] Pass Gitleaks full-history scan, NuGet vulnerability audit, and license inventory workflow: [security checks #1054](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/42474157414).
- [ ] Manually verify an import from a public GitHub repository and inspect the recorded commit/digest fields on a Windows desktop.

The Release build completed with **8 warnings and 0 errors**; the reported warnings are in `WorkspaceHubWindow.cs`, `SafeBrowserDownloadServiceTests.cs`, `LocalAiServiceTests.cs`, and `GmailReadOnlyServiceTests.cs`, none of which are changed by this phase. The generated CI distribution passed installer lifecycle checks and verified 15 SHA-256 manifest entries; it is explicitly **unsigned**. The automated acceptance report therefore retains the decision **AUTOMATED PASS — FINAL ACCEPTANCE PENDING MANUAL / EXTERNAL GATES**.

The commit SHA makes the fetched Git tree reproducible for that import; the recorded archive digest identifies the exact archive bytes received, and the package digest detects later local modification. These values are not a GitHub publisher signature, provenance attestation, or guarantee that the skill's instructions are safe. Skill Markdown remains untrusted input and skill scripts remain unexecuted.
