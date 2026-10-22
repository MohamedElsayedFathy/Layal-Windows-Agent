# Layal | ليال — Windows AI Desktop Agent

**Source version 0.5.2.** Native WPF application foundation with a redesigned Calm Light UI with soft blue-lavender surfaces and darker readable text, left navigation rail, refined pearl-shell floating mascot with layered cyan/violet hardware details and expressive mood/hover animation states, custom tray icon, Arabic/English UI, OpenAI-compatible chat and tool-calling client, bounded multi-step agent orchestration, explicit approvals for consequential tools, generic configurable REST/JSON application API connections (Bearer/custom-header/no-auth), GitHub/ZIP skill import, and locally DPAPI-encrypted chat history, task history, memory notes, and API connection profiles. The task runner now marks final answers as CompletedWithWarnings when a tool fails or an approved action is declined; exiting through the tray also closes hidden windows and triggers cancellation.

This is a development milestone, not the complete Layal product. Generic app integration supports HTTP REST/JSON endpoints with no auth, bearer tokens, custom auth headers, and optional non-secret headers; interactive OAuth flows and non-HTTP protocols need dedicated adapters. A first browser automation milestone adds a separate Layal-controlled Microsoft Edge profile with approved navigation, bounded page reading, and selector-based click/text entry. Windows UI Automation now supports read-only inspection, one-control InvokePattern actions, and approved ValuePattern entry into one uniquely identified non-password Edit field; these controls do not submit forms or prove application success, and both browser/native automation still need manual acceptance. Advanced web research/download/upload, voice STT/TTS, general mouse/keyboard automation and screen capture, RTSP/ONVIF video processing, MCP execution, model training, and installer/updater remain incomplete.

Mascot preview: `src/Layal.Desktop/Assets/layal-mascot-modern-preview.png`. This is a visual reference; the in-app mascot is rendered by WPF vector elements so its expressions can change.

Version 0.5.0 replaced the previous dark palette with a light, low-glare visual system and refreshed the mascot shell and expressions. Version 0.5.1 added the master requirement register, repeatable Windows verification script, source/release hygiene checks, and Windows GitHub Actions build workflow. Version 0.5.2 adds an automated C# test project for settings normalization/persistence, skill ZIP validation and duplicate handling, DPAPI-backed memory behavior, tool permission/path boundaries, and OpenAI-compatible HTTP response parsing. The HTTP client now accepts an injectable client factory so provider behavior can be tested without calling a real service. Native compilation and test execution still require a Windows host/CI with the .NET 10 SDK.

See [README_AR.md](README_AR.md) for Arabic setup details and limitations. See [docs/REVIEW_AND_DESIGN_UPDATE.md](docs/REVIEW_AND_DESIGN_UPDATE.md) for the review summary and [docs/STATIC_CHECKS.md](docs/STATIC_CHECKS.md) for the exact verification boundary.

## Build

On Windows 10/11 x64 with the .NET 10 SDK:

- Run `BUILD-LAYAL.bat` to publish a Release build.
- Run `START-LAYAL.bat` to launch from source.
- New installs have no hardcoded AI-provider endpoint; configure an OpenAI-compatible model API URL, model ID, and key in Settings. Existing saved endpoints are preserved.
- To connect external applications, open API integrations, add its REST/JSON base URL and authentication, enter optional endpoint notes, test the connection, and then invoke it from the Tasks tab. Each request requires local approval and its response is shared with the AI provider.

Layal agent file tools and dotnet build/test operations are limited to `%USERPROFILE%\Documents\LayalWorkspace`; build/test commands can still execute project-defined targets and should only be run on trusted projects. See README_AR.md for limits, safeguards, and data locations.


## Quality gates and traceability

- `docs/MASTER_REQUIREMENT_REGISTER_AR.md` is the source of truth for feature status and acceptance evidence.
- `docs/REQUIREMENT_TRACEABILITY.csv` is the machine-readable requirements register.
- `python tools/validate_project.py` checks XAML structure, resource references, event handlers, delimiters, and selected UI/project invariants.
- `python tools/validate_release.py` checks required source files, common secret patterns, and (when passed a ZIP path) archive integrity/content.
- `python tools/validate_test_results.py artifacts/test-results` ensures TRX reports exist, contain at least one discovered test, and report no failed outcomes.
- `python tools/package_source.py` creates a versioned source ZIP, excludes build/cache/user-artifact folders, and validates the exact archive before reporting success.
- On Windows, run `powershell -ExecutionPolicy Bypass -File .\tools\VERIFY-LAYAL.ps1` to execute the static checks, restore packages, and build Release. This does not replace manual acceptance tests.
- `.github/workflows/windows-build.yml` runs static validation, Windows Release build, and `dotnet test`; it validates non-empty TRX results and uploads build, test, and validated source artifacts.
- `tests/Layal.Tests/` contains the automated C# suite. A confirmed Windows CI baseline (run [39760491462](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39760491462), source commit `c1556493`) passed the Release build and all 64 C# tests (0 failed, 0 skipped). The current branch adds regression cases for percent-encoded backslash traversal; check the latest run for the exact branch head before relying on that change. The local authoring environment does not have the .NET SDK. Manual acceptance is still required for interactive Windows UI Automation, real websites, and external API connections.
- `docs/MANUAL_ACCEPTANCE_AR.md` provides a Windows acceptance checklist for the browser and UI Automation milestones; `tools/Start-LayalUiAutomationSample.ps1` creates a disposable local window to test safe field entry and a single approved button action.
