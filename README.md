# Layal | ليال — Windows AI Desktop Agent

**Source version 0.5.2.** Native WPF application foundation with a redesigned Calm Light UI with soft blue-lavender surfaces and darker readable text, left navigation rail, refined pearl-shell floating mascot with layered cyan/violet hardware details and expressive mood/hover animation states, custom tray icon, Arabic/English UI, OpenAI-compatible chat and tool-calling client, bounded multi-step agent orchestration, explicit approvals for consequential tools, generic configurable REST/JSON application API connections (Bearer/custom-header/no-auth), GitHub/ZIP skill import, and locally DPAPI-encrypted chat history, task history, memory notes, and API connection profiles. The task runner now marks final answers as CompletedWithWarnings when a tool fails or an approved action is declined; exiting through the tray also closes hidden windows and triggers cancellation.

This is a development milestone, not the complete Layal product. Generic app integration supports HTTP REST/JSON endpoints using no authentication, Bearer tokens, or custom authentication headers. A first browser automation milestone adds a dedicated Layal-controlled Microsoft Edge profile with approved navigation, bounded page reading, and selector-based click/text entry. Windows UI Automation supports metadata-only inspection and individually approved, uniquely targeted actions. These actions do not submit forms or prove the target application succeeded; browser and native automation still require manual Windows acceptance. The Windows distribution pipeline now includes an unsigned CI installer/package and supply-chain reports; final license review, signing, and real-desktop acceptance remain open.

Mascot: the official **Layal** character (Layal plus her robot assistant) uses artwork in `src/Layal.Desktop/Assets/Characters`; Layal's portrait is swapped per mood and the robot's face is drawn as WPF vector shapes so it can express real agent states. The earlier procedural 3D rig is kept behind the `UseSpriteCharacter` switch. See `docs/CHARACTER_DESIGN.md` for the mapping, limits and asset provenance (a release blocker until confirmed). `layal-mascot-modern-preview.png` is the older visual reference.

Version 0.5.0 replaced the previous dark palette with a light, low-glare visual system and refreshed the mascot shell and expressions. Version 0.5.1 added the master requirement register, repeatable Windows verification script, source/release hygiene checks, and Windows GitHub Actions build workflow. Version 0.5.2 adds an automated C# test project for settings normalization/persistence, skill ZIP validation and duplicate handling, DPAPI-backed memory behavior, tool permission/path boundaries, and OpenAI-compatible HTTP response parsing. The HTTP client now accepts an injectable client factory so provider behavior can be tested without calling a real service. Native compilation and test execution still require a Windows host/CI with the .NET 10 SDK.

See [README_AR.md](README_AR.md) for Arabic setup details and limitations. See [docs/REVIEW_AND_DESIGN_UPDATE.md](docs/REVIEW_AND_DESIGN_UPDATE.md) for the review summary and [docs/STATIC_CHECKS.md](docs/STATIC_CHECKS.md) for the exact verification boundary.

## Build

On Windows 10/11 x64 with the .NET 10 SDK:

- Run `BUILD-LAYAL.bat` to publish a Release build.
- Run `START-LAYAL.bat` to launch from source.
- New installs have no hardcoded AI-provider endpoint; configure an OpenAI-compatible model API URL, model ID, and key in Settings. Existing saved endpoints are preserved.
- To connect external applications, open API integrations, add its REST/JSON base URL and authentication, enter optional endpoint notes, test the connection, and then invoke it from the Tasks tab. Each request requires local approval and its response is shared with the AI provider.

Layal agent file tools and dotnet build/test operations are limited to `%USERPROFILE%\Documents\LayalWorkspace`; build/test commands can still execute project-defined targets and should only be run on trusted projects. See README_AR.md for limits, safeguards, and data locations.


- Release preparation: [release Go/No-Go approval gate](docs/RELEASE_APPROVAL_AR.md) and [unpublished 0.5.2 candidate notes](docs/RELEASE_NOTES_0.5.2_AR.md). Current decision is **NO-GO**; do not publish unsigned CI artifacts.

## Quality gates and traceability

- `docs/MASTER_REQUIREMENT_REGISTER_AR.md` is the source of truth for feature status and acceptance evidence.
- `docs/REQUIREMENT_TRACEABILITY.csv` is the machine-readable requirements register.
- `python tools/validate_project.py` checks XAML structure, resource references, event handlers, delimiters, and selected UI/project invariants.
- `python tools/validate_release.py` checks required source files, common secret patterns, and (when passed a ZIP path) archive integrity/content.
- `python tools/validate_test_results.py artifacts/test-results` ensures TRX reports exist, contain at least one discovered test, and report no failed outcomes.
- `python tools/package_source.py` creates a versioned source ZIP, excludes build/cache/user-artifact folders, and validates the exact archive before reporting success.
- On Windows, run `powershell -ExecutionPolicy Bypass -File .\tools\VERIFY-LAYAL.ps1` to execute the static checks, restore packages, and build Release. This does not replace manual acceptance tests.
- `.github/workflows/windows-build.yml` runs static validation, Windows Release build, and `dotnet test`; it validates non-empty TRX results and uploads build, test, and validated source artifacts.
- `tests/Layal.Tests/` contains automated tests. The authoring environment lacks the .NET SDK, but Windows CI run [39863364728](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39863364728) executed the suite successfully for code commit `034fa598d8ec219e6c59019984013ac649055e5b`: **77 passed, 0 failed, 0 skipped**. PR #3 adds regression cases for encoded/double-encoded path traversal and folded/literal YAML descriptions; rerun CI on the combined head to confirm the combined test count. The automated-test gate passed for that code commit; manual Windows acceptance and external provider/device integration tests remain separate.
- `docs/MANUAL_ACCEPTANCE_AR.md` provides the manual Windows acceptance checklist for the browser and native UI automation features; `tools/Start-LayalUiAutomationSample.ps1` launches a disposable WPF sample for safe testing.


## Security, dependencies, and licensing

- tools/audit_nuget.py checks direct and transitive NuGet packages against current vulnerability advisories; CI fails on reported advisories or an unreadable audit report.
- tools/validate_licenses.py with --manifest-only checks the curated third-party notices; passing --sbom cross-checks the generated CycloneDX package inventory.
- The security workflow runs Gitleaks on the selected commit range and also invokes an explicit full-history scan across all fetched refs, uploading a SARIF report. Dependabot proposes weekly NuGet and GitHub Actions updates; updates still require review.
- Third-party notices and known license texts are packaged with the installer and portable ZIP. Layal itself still has no declared project-wide license, and the source/redistribution rights for mascot/reference/tray assets remain unresolved. These are release blockers, not rights granted by third-party notices.


## Digital signatures and file integrity

The normal PR/CI build remains explicitly unsigned. A version tag matching `v*` enters a fail-closed release path that requires a trusted Authenticode Code Signing certificate configured through GitHub Actions repository secrets. That path signs `Layal.exe` and the setup EXE with SHA-256 and an RFC 3161 timestamp, verifies both signatures, and regenerates the SHA-256 manifest after signing. See `docs/CODE_SIGNING_AR.md` and `docs/DISTRIBUTION_AR.md`. A real signing certificate has not yet been provisioned; the release path will not publish an unsigned tag build.
