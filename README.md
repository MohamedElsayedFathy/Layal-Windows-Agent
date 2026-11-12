# Layal | ليال — Windows AI Desktop Agent

**Source version 0.5.8.** The active text-chat and bounded Tasks paths now run through Layal's own deterministic native cognitive core. They do not call Ollama, Qwen, or a hosted conversational model and do not require a chat-model download, API key, or cloud AI endpoint. The initial symbolic core understands a defined set of Arabic/English commands, retrieves local memory, creates extractive document summaries, stores user-taught command aliases, and runs approved allow-listed tasks. Unsupported requests fail closed instead of falling back to another model. This is a bounded first milestone, not general-purpose open-ended reasoning or statistical self-training. Optional Whisper/Piper resources are separate local speech-input/output tools; they are not used for text reasoning or task planning. See docs/LOCAL_AI_SETUP_AR.md and docs/VOICE_REPLY_AR.md for the exact boundaries. Native WPF desktop shell, floating mascot, bilingual UI, approved browser/Windows automation components, encrypted local history/memory, and configurable REST/JSON connection catalog remain part of the source. 

This is a development milestone, not the complete Layal product. The independent cognitive route supports explicit sequences of up to six bounded commands after parsing the complete sequence before execution; consequential actions require separate approvals, and file creation is followed by a separately approved local read-back. Quick Ask reuses the same native conversation route, and saved routines pass only reviewed command lines (up to six) to the native planner; descriptive routine text remains in the confirmation UI and is never sent as a model prompt. It does not perform unrestricted natural-language planning. Document summaries are extractive, not generated. Image understanding is intentionally disabled in this route: selected images are not forwarded to a model. Local Whisper/Piper speech resources may still use trained speech models for transcription/synthesis only, and are not part of the reasoning engine. Generic app integration supports HTTP REST/JSON endpoints using no authentication, Bearer tokens, or custom authentication headers, but the native task planner does not currently route tasks through this catalog or send its responses to an AI model. A first browser automation milestone adds a dedicated Layal-controlled Microsoft Edge profile with approved navigation, bounded page reading, and selector-based click/text entry. Windows UI Automation supports metadata-only inspection and individually approved, uniquely targeted actions. Browser clicks can trigger site-defined handlers that navigate or modify remote data, and do not prove the target application succeeded; browser and native automation still require manual Windows acceptance. The Windows distribution pipeline includes an unsigned CI installer/package and supply-chain reports; final license review, signing, and real-desktop acceptance remain open.

Mascot: the official **Layal** character (Layal plus her robot assistant) uses artwork in `src/Layal.Desktop/Assets/Characters`; Layal's portrait is swapped per mood and the robot's face is drawn as WPF vector shapes so it can express real agent states. The earlier procedural 3D rig is kept behind the `UseSpriteCharacter` switch. See `docs/CHARACTER_DESIGN.md` for the mapping, limits and asset provenance (a release blocker until confirmed). `layal-mascot-modern-preview.png` is the older visual reference.

Version 0.5.0 replaced the previous dark palette with a light, low-glare visual system and refreshed the mascot shell and expressions. Version 0.5.1 added the master requirement register, repeatable Windows verification script, source/release hygiene checks, and Windows GitHub Actions build workflow. Version 0.5.2 adds an automated C# test project for settings normalization/persistence, skill ZIP validation and duplicate handling, DPAPI-backed memory behavior, tool permission/path boundaries, and OpenAI-compatible HTTP response parsing. The HTTP client now accepts an injectable client factory so provider behavior can be tested without calling a real service. Native compilation and test execution still require a Windows host/CI with the .NET 10 SDK.

The native cognitive core was merged to `main` in PR #154; the older Ollama/Qwen tool-calling PR #153 was closed without merging. The November 12 review fixes task verification evidence, bilingual status messages, and model-free local image/camera preview safeguards. See [the technical review](docs/TECHNICAL_REVIEW_2026-11-12_AR.md) and the updated [vision limitations guide](docs/VISION_AR.md). Windows CI on the main baseline passed 644/644 C# tests; review-branch changes must still pass their own Windows CI before merge.

See [README_AR.md](README_AR.md) for Arabic setup details and limitations. See [docs/REVIEW_AND_DESIGN_UPDATE.md](docs/REVIEW_AND_DESIGN_UPDATE.md) for the previous visual review and [docs/STATIC_CHECKS.md](docs/STATIC_CHECKS.md) for the exact verification boundary.

## Build

On Windows 10/11 x64 with the .NET 10 SDK:

- Run `BUILD-LAYAL.bat` to publish a Release build.
- Run `START-LAYAL.bat` to launch from source.
- New installs have no hardcoded AI-provider endpoint. The active text-chat and Tasks paths do not need a provider URL, model ID, API key, Ollama, or Qwen. Legacy provider settings remain for compatibility and are not called by those paths.
- To configure external application connections, open API integrations and add a required name and REST/JSON base URL, plus authentication as needed. Each supported API test/request requires local approval. In the native cognitive milestone, these connections are not used for text generation, task planning, or model tool-calling, and API responses are not forwarded to a conversational model. This catalog is a client-side connector, not a public API server hosted by Layal. See [the Arabic API catalog guide](docs/API_CATALOG_AR.md).

Layal agent file tools and dotnet build/test operations are limited to `%USERPROFILE%\Documents\LayalWorkspace`; build/test commands can still execute project-defined targets and should only be run on trusted projects. See README_AR.md for limits, safeguards, and data locations. The automatic local chat/memory retention limits are documented in [English](docs/LOCAL_DATA_RETENTION.md) and [Arabic](docs/LOCAL_DATA_RETENTION_AR.md).


- The current source snapshot is 0.5.8, but the release decision remains **NO-GO**. See the [release Go/No-Go approval gate](docs/RELEASE_APPROVAL_AR.md), [implementation status and pending evidence](docs/IMPLEMENTATION_STATUS.md), and [live Windows acceptance procedure](docs/WINDOWS_LIVE_ACCEPTANCE_REPORT_AR.md). Do not publish unsigned CI artifacts.

## Quality gates and traceability

- `docs/MASTER_REQUIREMENT_REGISTER_AR.md` is the source of truth for feature status and acceptance evidence.
- `docs/REQUIREMENT_TRACEABILITY.csv` is the machine-readable requirements register.
- `python tools/validate_project.py` checks XAML structure, resource references, event handlers, delimiters, and selected UI/project invariants.
- `python tools/validate_release.py` checks required source files, common secret patterns, and (when passed a ZIP path) archive integrity/content.
- `python tools/validate_test_results.py artifacts/test-results` ensures TRX reports exist, contain at least one discovered test, and report no failed outcomes.
- `python tools/package_source.py` creates a versioned source ZIP, excludes build/cache/user-artifact folders, and validates the exact archive before reporting success.
- On Windows, run `powershell -ExecutionPolicy Bypass -File .\tools\VERIFY-LAYAL.ps1` to execute the static checks, restore packages, and build Release. This does not replace manual acceptance tests.
- `.github/workflows/windows-build.yml` runs static validation, Windows Release build, and `dotnet test`; it validates non-empty TRX results and uploads build, test, and validated source artifacts.
- `tests/Layal.Tests/` contains the automated C# suite. The pre-audit main baseline (`8f0c9346992347faaaf3783e74510ad28605444a`) passed [Windows CI run 40683297849](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/40683297849). Always use the workflow run associated with the commit being reviewed for its exact test result; a passing automated suite does not replace manual Windows acceptance or external provider/device integration tests.
- `docs/MANUAL_ACCEPTANCE_AR.md` provides the manual Windows acceptance checklist for the browser and native UI automation features; `tools/Start-LayalUiAutomationSample.ps1` launches a disposable WPF sample for safe testing.


## Security, dependencies, and licensing

- tools/audit_nuget.py checks direct and transitive NuGet packages against current vulnerability advisories; CI fails on reported advisories or an unreadable audit report.
- tools/validate_licenses.py with --manifest-only checks the curated third-party notices; passing --sbom cross-checks the generated CycloneDX package inventory.
- [Workspace and skill threat model](docs/SECURITY_THREAT_MODEL_WORKSPACE_SKILLS_AR.md) records Phase 2 skill-import controls and Phase 3 workspace backup/restore path protections, mitigations, and residual risks. Phase 3 validates existing path ancestors before creating missing restore directories; manual Windows verification remains required.
- The security workflow runs Gitleaks on the selected commit range and also invokes an explicit full-history scan across all fetched refs, uploading a SARIF report. Dependabot proposes weekly NuGet and GitHub Actions updates; updates still require review.
- Third-party notices and known license texts are packaged with the installer and portable ZIP. Layal itself still has no declared project-wide license, and the source/redistribution rights for mascot/reference/tray assets remain unresolved. These are release blockers, not rights granted by third-party notices.


## Digital signatures and file integrity

The normal PR/CI build remains explicitly unsigned. A version tag matching `v*` enters a fail-closed release path that requires a trusted Authenticode Code Signing certificate configured through GitHub Actions repository secrets. That path signs `Layal.exe` and the setup EXE with SHA-256 and an RFC 3161 timestamp, verifies both signatures, and regenerates the SHA-256 manifest after signing. See `docs/CODE_SIGNING_AR.md` and `docs/DISTRIBUTION_AR.md`. A real signing certificate has not yet been provisioned; the release path will not publish an unsigned tag build.

## AYO-inspired capability parity

The AYO-inspired feature inventory and its evidence-based implementation status live in [docs/AYO_FEATURE_PARITY_AR.md](docs/AYO_FEATURE_PARITY_AR.md). This is a tracked roadmap, not a claim of feature-complete parity.
