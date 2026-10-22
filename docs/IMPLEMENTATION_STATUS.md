# Layal 0.5.2 — implementation status register

This matrix describes source-code presence and authored test coverage only; test pass status requires Windows CI evidence. Because the .NET SDK was unavailable in the authoring environment, Windows compilation/runtime behavior has not been verified. Version 0.5.0 updated presentation resources and mascot XAML. Version 0.5.1 added traceability and repeatable build checks. Version 0.5.2 adds a C# automated test project and Windows CI test execution. Treat the source as statically reviewed, not runtime-proven, until Windows CI passes.

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
| Read-only Windows UI Automation inspection | Implemented in source with consent, bounded traversal, single-flight background worker, and 8-second caller timeout; CI/runtime acceptance pending | `Services/WindowsUiAutomationService.cs`, `Services/AgentToolExecutor.cs` | Manual Windows acceptance required |
| Approved single-control UI action | Implemented in source for Button/Hyperlink/MenuItem/SplitButton with exact AutomationId, unique-match validation, per-action approval, and post-action observation; CI/runtime acceptance pending | `Services/WindowsUiAutomationService.cs`, `Services/AgentToolExecutor.cs` | Test against a disposable sample app; no general mouse/keyboard typing |
| General mouse/keyboard control and screen capture | Not implemented | — | Future milestone |
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


## Version 0.5.2 — automated regression test foundation

- Added MSTest-based suites for SettingsStore defaults/normalization/round-trip/corrupt-file recovery.
- Added skill ZIP tests for valid metadata, duplicate name policy, invalid front matter/name, missing SKILL.md, bounded Markdown reads, and invalid GitHub URL formats.
- Added agent-tool tests for unique tool definitions, approval denial side effects, workspace traversal rejection, create-file overwrite protection, and project-file validation before build approval.
- Added OpenAI-compatible HTTP client tests using an injectable HttpClient factory and fake HTTP handler, including authorization, endpoint construction, chat parsing, tool-call parsing, and invalid URL protection.
- Added Windows DPAPI-backed memory tests for save/search/update/delete persistence and JSON export; these are expected to run on Windows CI only.
- Expanded static validation to scan C# test sources and validate the test-project configuration.
- Updated Windows GitHub Actions to restore, build, run tests, and upload TRX results.
- **Execution status:** source written and statically checked; `dotnet test` has not run in the current authoring environment because .NET SDK is unavailable. Do not mark the test requirement verified until Windows CI shows a passing result.
