# Layal development preview — implementation status register

This matrix separates source presence, automated Windows CI evidence, and live interactive acceptance. The authoring environment lacks the .NET SDK. Windows CI run [39863364728](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39863364728) on code commit `034fa598d8ec219e6c59019984013ac649055e5b` built Release with 0 warnings/0 errors, passed 77/77 C# tests, passed native transparent-padding click-through 4/4 and drag/save/restore 6/6, and recorded 11 display/DPI checks. Its desktop runner exposed one display at 96 DPI (100%); physical multi-monitor movement and real Windows scale transitions remain unverified on the target PC. Version 0.5.0 updated presentation resources and mascot XAML. Version 0.5.1 added traceability and repeatable build checks. Version 0.5.2 adds automated C# tests and Windows CI execution.

| Requirement | Status in source | Relevant files | Verification |
|---|---|---|---|
| Native WPF application and refreshed floating mascot | Release build and limited native input behavior verified in Windows CI; target-desktop visual acceptance pending | `App.xaml`, `Views/FloatingMascotWindow.*` | CI run 39863364728 plus manual Windows checklist |
| Tray actions, branded icon, live language switching, show/hide and exit | Implemented in source; tray-specific live hide/show, restart, and exit acceptance still pending | `Services/TrayService.cs`, `App.xaml.cs` | Manual target-desktop acceptance |
| Arabic/English UI foundation | Implemented, partially localized | `Views/MainWindow.xaml.cs` | Manual RTL/LTR and translation checks needed |
| DPAPI provider key | Implemented in source | `Services/DpapiSecretStore.cs` | Windows DPAPI test needed |
| OpenAI-compatible model probe/chat | Implemented in source; no hardcoded FreeLLM default on new install | `Services/OpenAiCompatibleClient.cs`, `Models/UserSettings.cs` | Requires user-configured compatible endpoint/key |
| External application REST/JSON APIs | Implemented in source: named connections, None/Bearer/custom-header auth, encrypted profile store, request test, `call_app_api` with explicit per-request consent and response-size limits; runtime acceptance pending | `Services/ApiConnectionService.cs`, `Models/ApiConnectionProfile.cs`, API integrations tab, `AgentToolExecutor.cs` | Windows CI and manual API test required; interactive OAuth and non-HTTP protocols are not supported by this generic connector |
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
| Read-only Windows UI Automation inspection | Implemented in source with consent, bounded traversal, single-flight background worker, and 8-second caller timeout; Windows CI build/tests pass, manual runtime acceptance pending | `Services/WindowsUiAutomationService.cs`, `Services/AgentToolExecutor.cs` | Manual Windows acceptance required |
| Approved native UI actions and text entry | Implemented in source: Button/Hyperlink/MenuItem/SplitButton InvokePattern, ValuePattern.SetValue on one uniquely identified visible non-password Edit field, and TogglePattern state-setting on one unique visible two-state CheckBox; fresh local approval and runtime-id/state revalidation, bounded traversal, no field-value read-back; runtime acceptance pending | `Services/WindowsUiAutomationService.cs`, `Services/AgentToolExecutor.cs` | Test on a disposable Windows sample app; no general mouse/keyboard, coordinates, screenshots, form submission, or application-success claim |
| General mouse/keyboard control and screen capture | Not implemented | — | Future milestone |
| Screen capture/visual UI control | Not implemented | — | Future milestone |
| Browser automation (initial milestone) | Implemented in source: separate Edge profile, loopback DevTools, approved navigation/page disclosure, unique CSS selector click/fill, revalidation after consent, password/file fields blocked; Windows CI build/tests pass, manual runtime acceptance pending | `Services/BrowserAutomationService.cs`, `AgentToolExecutor.cs` | Windows manual acceptance required; advanced research/download/upload and normal browser profile reuse remain unimplemented |
| RTSP/ONVIF video and computer vision | Not implemented | Camera tab reports limitation | Future milestone |
| MCP protocol integration | Not implemented | — | Future milestone |
| SQLite, vector memory/RAG, model training | Not implemented | — | Future milestone |
| Installer/updater/signing | Not implemented | Publish script only | Future milestone |
| Automated C# test project | Windows CI passed 77/77 tests for code commit `034fa598d8ec219e6c59019984013ac649055e5b`; not run in the SDK-less authoring environment | `tests/Layal.Tests/` | CI run 39863364728; broader manual/integration acceptance remains separate |

## Design/reliability changes carried forward from 0.4.0

- Previous release introduced a dark Aurora system; 0.5.0 replaces it with a soft light palette, white/pastel surfaces, readable dark text, gentler hover states, and subtle shadows.
- Mascot refreshed with a pearl-white shell, layered cyan/violet side modules, orbit details, eye highlights, and a dark high-contrast face display; state-driven expressions and gentle hover/idle animation remain.
- Saved mascot position can include negative coordinates for secondary monitors and is clamped to the virtual desktop.
- Exiting from the tray closes hidden windows so active tasks can receive cancellation.
- Tool failures or declined actions prevent the task from being shown as a clean success.
- Static XAML/resource/event validation script added under `tools/validate_project.py`.

## المرحلة الخامسة — القبول النهائي على Windows فعلي — 23 أكتوبر 2026

**الحالة: PENDING — بوابة القبول الحي لم تُغلق.** لا تتوفر في جلسة العمل هذه جلسة سطح مكتب Windows فعلية متصلة بجهاز الهدف؛ لذلك لم يتم تنفيذ اختبار يدوي على جهاز المستخدم، ولا يجوز وصف القبول الحقيقي بأنه ناجح.

- سُجلت أدلة CI للتفاعل وDPI بصفتها اختبارات داخل عامل Windows، لا بديلًا عن شاشة/ماوس الجهاز المستهدف. أحدث تشغيل للفرع هو [Windows build run 39982626010](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39982626010) على commit `53f31318051ddaef914da70804656bc25b782cfc`؛ كانت خطوات النقر الأصلي والسحب وفحص العرض/DPI ورفع أدلتها ناجحة، بينما ظل التشغيل نفسه قيد التنفيذ في خطوة حزمة التوزيع عند التحقق الأخير.
- أُضيف [سجل القبول الحي على Windows](WINDOWS_LIVE_ACCEPTANCE_REPORT_AR.md) ويتضمن مصفوفة حالات قابلة للتسجيل، وبيانات بيئة الاختبار، وأوامر تشخيص الشاشة/DPI وتمرير النقرات، وشروط PASS/FAIL/BLOCKED/NOT TESTED.
- لإغلاق البوابة، يجب تنفيذ القائمة على جهاز Windows تفاعلي باستخدام commit وartifact محددين؛ وحفظ الصور والسجلات وSHA-256؛ ومراجعة كل حالات الفشل/الحظر قبل إصدار قرار. لا تُعد نتائج RenderTargetBitmap أو عامل CI شهادة بتغيّر DPI الفعلي أو التعدد الفيزيائي للشاشات.
- تظل بوابتا التوقيع بشهادة Code Signing موثوقة ومراجعة تراخيص المشروع/الأصول منفصلتين عن القبول الحي، وتظل PR #6 مسودة وغير مدمجة.

## المرحلة السادسة — اعتماد الإصدار والإعداد للنشر — 23 أكتوبر 2026

**قرار الاعتماد الحالي: NO-GO — لا يُنشر إصدار عام الآن.** إعدادات الإصدار والمستندات جاهزة للمراجعة، لكن البوابات التقنية والقانونية والقبول الحي لم تُغلق كلها.

- أحدث Windows CI على commit `c5138a4ccae9b4b66215be03b8cd2bc4e1cce7ea` نجح: [run 39983593458](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458). سجل 77/77 اختبارًا ناجحًا، و4/4 اختبارات تمرير نقرات، و6/6 اختبارات سحب/استعادة، و11 تحققًا آليًا للشاشة/DPI، ودورة حياة المثبّت والتحقق من 10 بصمات SHA-256. فحص الشاشة رصد شاشة واحدة فقط عند 96 DPI/100%، وأكد صراحةً أن اعتماد الشاشات/DPI غير مكتمل وأن القبول البصري اليدوي ما زال pending.
- نجحت فحوص الأمان/سلسلة التوريد في [run 39983593523](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593523)، بما فيها Gitleaks وNuGet vulnerability audit. جرد الرخص نفسه يسجل **PASS WITH OPEN LEGAL GATES** وليس اعتمادًا قانونيًا نهائيًا.
- أُضيف [سجل قرار الإصدار Go/No-Go](RELEASE_APPROVAL_AR.md) و[مسودة ملاحظات الإصدار 0.5.2](RELEASE_NOTES_0.5.2_AR.md). وتُميز الوثيقتان بين أدلة CI ومتطلبات النشر الفعلية.
- حزمة التوزيع المرجعية من CI [artifact 12514233645](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458/artifacts/12514233645) غير موقعة ومحدودة الاحتفاظ؛ لا تُستخدم بدلًا من مخرجات tag إصدار موثوق.
- ما زال القبول على جهاز Windows فعلي والتوقيع الموثوق ومراجعة رخصة المشروع وحقوق الأصول ودمج معتمد على `main` وتشغيل tag موقّع ونشر GitHub Release بوابات مفتوحة. لم تُنشأ tag أو صفحة إصدار خلال هذه المرحلة؛ PR #6 ما زال Draft وغير مدمج.

## Remaining acceptance and integration gates

1. Execute the [manual Windows acceptance checklist](MANUAL_ACCEPTANCE_AR.md) on the target PC: tray/hide/show/exit, single/double/context clicks, keyboard focus, and settings persistence after a real restart.
2. Test actual Windows scaling at 100%, 125%, 150%, and 175%. On a two-monitor setup with different scale factors, run the DPI harness for 90 seconds while dragging Layal between displays and retain JSON/CSV evidence plus screenshots.
3. Verify real provider connection and tool-calling behavior with an explicitly configured compatible model; mocked HTTP tests do not establish external-service availability.
4. Exercise consequential tool approvals and denial paths with a disposable trusted workspace; confirm rejected approvals produce no side effects.
5. Complete live GitHub skill-import checks and Windows DPAPI persistence/export-warning checks. Camera, voice, MCP, browser DOM automation, installer/updater, and other features marked not implemented remain outside the verified 0.5.2 scope.
6. Run `VERIFY-LAYAL.ps1` on the target Windows machine if that machine is to be the acceptance environment, and retain the complete logs. Windows CI already passed the automated test gate for the code commit cited above; it does not close these remaining live-acceptance gates.


The authoritative current ledger is `MASTER_REQUIREMENT_REGISTER_AR.md`, with a CSV copy in `REQUIREMENT_TRACEABILITY.csv`. Check that ledger before every milestone/release.


## Phased visual-personality work — Stages 1–2 and Stage 3 Part 1 implemented; Part 2 automation verified in CI, live acceptance pending
- **Stage 1 — articulated 3D avatar:** separate WPF 3D nodes represent the head, torso, upper arms, forearms, hands, thighs, shins and feet. Independent joint transforms support posing; the 3D face includes eye whites, irises, pupils, brows, cheeks and mouth.
- **Stage 2 — cinematic motion and task-synchronized effects:** `MascotEffectStyleResolver` selects distinct motion styles for affection, task-orbit, speech, waiting, celebration, alerts, quiet states and default idle. Task symbols, colors and motion cadence vary by planning/research/reading/writing/coding/tool/validation/waiting/success/error state.
- Celebration particles use staggered eased bursts that fade out rather than repeating endlessly; hearts/flowers drift, task glyphs orbit, speech symbols wave, and warning/error glyphs pulse. Head, torso and joint pose values use damped transitions.
- **Stage 3 Part 1 — automated visual review:** the source now has a theme-aware ComboBox/ComboBoxItem template, a themed scrollbar track/thumb, a themed GroupBox template, stronger muted-text contrast, Latin isolation in Arabic UI text, checks for actually rendered button labels, and a smaller 900×560 DIP minimum window.
- The WPF visual regression test asserts the seven expected tabs exist in the right order and have non-empty viewports; it checks light/dark palette contrast, the chat-history ComboBox, Arabic/English button labels, LTR handling for URLs/API keys/model IDs, and the themed task GroupBox border.
- Latest automated visual package contains **208 PNGs** plus a manifest: 112 standard app renders (7 sections × 2 themes × 2 languages × 4 render scales), 28 compact-window renders (900×560 DIPs), and 68 mascot renders (17 moods × 4 render scales). The application screenshots are composited over the active theme backdrop; mascot-background transparency remains intentional.
- **Stage 3 Part 1 CI verification:** [Windows CI run 39825663174](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39825663174), commit `ea7a7cc62c484f04d8c6086f69c8afaa89c89e74`: Release build **0 warnings / 0 errors**; **73 passed, 0 failed, 0 skipped**. Source/test/visual/release artifacts were uploaded.
- **Stage 3 Part 2 — DPI and interaction acceptance:** the desktop app declares PerMonitorV2 DPI awareness, carries the WPF DPI-change compatibility switch, and isolates mascot hit-region geometry for tests. Its layered window now dynamically toggles `WS_EX_TRANSPARENT` based on pointer location to pass clicks from empty padding to other applications while keeping the mascot and visible task props interactive. Settings round-trip coverage includes persisted theme/language and mascot placement/options. [Windows CI run 39833062473](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39833062473) built Release with **0 warnings / 0 errors** and ran **77 passing tests, 0 failed, 0 skipped**.
- **Live hardware gate remains pending:** actual monitor scale changes at 100/125/150/175%, secondary-monitor movement, first-click and drag behavior around the click-through boundary, keyboard focus, and restart persistence still require recorded checks on an interactive Windows desktop. RenderTargetBitmap density captures do not simulate physical monitor DPI.
- The avatar remains procedural WPF 3D geometry/scene nodes, not an imported artist-authored mesh or DCC bone rig. The theme-aware gradients/cards provide a restrained glass-like finish, not true desktop acrylic blur.
- Plan: `docs/PHASED_EXECUTION_AR.md`. Manual checklist: `docs/MANUAL_ACCEPTANCE_AR.md`. PR #6 remains draft and unmerged; Live Windows hardware acceptance remains pending.
## Version 0.5.2 — automated regression test foundation

- Added MSTest-based suites for SettingsStore defaults/normalization/round-trip/corrupt-file recovery.
- Added skill ZIP tests for valid metadata, duplicate name policy, invalid front matter/name, missing SKILL.md, bounded Markdown reads, and invalid GitHub URL formats.
- Added agent-tool tests for unique tool definitions, approval denial side effects, workspace traversal rejection, create-file overwrite protection, and project-file validation before build approval.
- Added OpenAI-compatible HTTP client tests using an injectable HttpClient factory and fake HTTP handler, including authorization, endpoint construction, chat parsing, tool-call parsing, and invalid URL protection.
- Added Windows DPAPI-backed memory tests for save/search/update/delete persistence and JSON export; these are expected to run on Windows CI only.
- Expanded static validation to scan C# test sources and validate the test-project configuration.
- Updated Windows GitHub Actions to restore, build, run tests, and upload TRX results.
- **Execution status:** the current authoring environment lacks the .NET SDK, so `dotnet test` was not run locally. Windows CI later executed the suite successfully: 77 passed, 0 failed, 0 skipped in [run 39863364728](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39863364728) for code commit `034fa598d8ec219e6c59019984013ac649055e5b`. This verifies the automated-test gate for that commit; live acceptance remains separate.

## Final native input verification and anti-regression gate — 22 October 2026

Windows CI run [39852788070](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39852788070) completed successfully. It verified the Release build, native click-through and drag integration runs, all 77 automated tests, and evidence upload. The recorded native evidence reports:

- Transparent-padding click-through: **4 passed, 0 failed** — underlying baseline click, transparent upper-left pass-through, interactive mascot core without click leakage, and transparent bottom pass-through.
- Native drag/restart acceptance: **6 passed, 0 failed** — three drag vectors in differing directions, no leaked clicks during drag, position written to settings.json, and position restored after restarting Layal.exe.
- Automated C# tests: **77 passed, 0 failed, 0 skipped**.

The source validator now guards the native test modes and expected check counts, requires both dedicated CI steps, and rejects reintroducing WPF DragMove() in the mascot's drag path. The native drag suite also checks movement, click-through isolation, settings persistence, and position restoration after restart.

**Scope limit:** this is successful CI-desktop native input verification, not certification on a physical multi-monitor setup. Changing real Windows DPI scales (100/125/150/175%), moving between physical monitors, keyboard-focus acceptance, and visual manual acceptance remain separate live-Windows checks. PR #6 remains a draft and unmerged.


## Final verification and dark-mode regression prevention — 23 October 2026

Windows CI run [39857814475](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39857814475) completed successfully on commit `edee14701ed613fd9af320772ce1ec8ce537bba2`. The Release build reported **0 warnings / 0 errors**; static source validation, PowerShell parse, source hygiene, package validation, restore, build, native interaction checks, automated tests, test report validation, and artifact uploads all passed.

- Native transparent-padding click-through: **4 passed, 0 failed**.
- Native drag/restart acceptance: **6 passed, 0 failed**, including three directional drag cycles, no leaked clicks, position saved to `settings.json`, and position restored after restarting Layal.exe.
- Automated C# tests: **77 passed, 0 failed, 0 skipped**.
- New dark-mode regression gate in `MainWindowVisualSmokeTests.cs` invokes the actual `ThemeButton_Click` path for Light → Dark → Light and asserts live resource colors, button labels, control-surface/contrast checks, and persisted settings after reload. `validate_project.py` fails CI if the UI binding, save path, or regression coverage is removed.

Artifacts uploaded by this run include the Windows Release build, interactive-acceptance evidence, WPF visual-smoke snapshots, source package, and TRX test results.

**Remaining manual acceptance gate:** the above is CI-desktop native interaction verification, not validation on the user's physical display. Real Windows scale changes at 100/125/150/175%, secondary-monitor movement and DPI transition, keyboard focus, and visual clipping/overlap must still be recorded in the [manual acceptance checklist](MANUAL_ACCEPTANCE_AR.md). PR #6 remains a draft and unmerged.


## Display resolution, DPI, and multi-monitor acceptance — in progress

- Added `tools/Test-LayalDisplayDpi.ps1` to enumerate active monitors and record physical-pixel bounds/work areas, effective DPI, the floating Layal HWND bounds, its current monitor, `GetDpiForWindow`, and the window's DPI-awareness level.
- The same harness supports timed sampling for a manual cross-monitor drag. Run `-RequireMultipleMonitors -RequireMonitorTransition -RequireDpiTransition -ObserveSeconds 90` on a real PC with two displays configured at different Windows scaling levels; it saves JSON and CSV evidence.
- Windows CI runs a three-second topology smoke check and uploads `display-dpi-summary.json` and `display-dpi-samples.csv`. The CI runner's topology must not be interpreted as proof of a physical multi-monitor or 100/125/150/175% scale change.
- **Physical display acceptance remains pending** until a run on the target device records each monitor's resolution/scaling, confirms Layal crosses between displays without losing position or input behavior, and checks visual clipping/overlap. PR #6 remains draft and unmerged.


## Interaction and settings verification — automation added; live acceptance pending

- The WPF visual-smoke suite now exercises the real language-toggle button in both directions, the home shortcuts to Chat and Settings, immediate checkbox persistence for always-on-top and animations, the opt-in window-title inspection flag, mascot scale/opacity application to the live WPF window, provider URL/model validation without network access, and the show-mascot button after hiding the real window.
- SettingsStoreTests now asserts safe defaults, rejects an invalid theme name by normalizing to light mode, and round-trips AllowWindowTitleInspection.
- validate_project.py guards key XAML event wiring and the language/settings/provider save handlers, as well as the new end-to-end WPF regression assertions.
- **Windows CI verification passed:** [run 39862792692](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39862792692), commit `e1fe6f990aa95684bda0925a6f95885023073d5b`: Release build 0 warnings / 0 errors; 77 passed, 0 failed, 0 skipped; native click-through 4/4; drag/restart 6/6; display/DPI topology smoke 11 checks; artifacts uploaded.
- **Live Windows acceptance remains pending:** manually verify single/double click behavior, context-menu hide/show, restart persistence, and all configuration options in a user desktop session. The CI environment does not replace actual mouse interaction and restart testing on the target Windows machine.


## مطابقة المتطلبات وقبول حزمة الإصدار — 23 أكتوبر 2026

أُعيدت مطابقة سجل المتطلبات ومصفوفة `REQUIREMENT_TRACEABILITY.csv` بالأدلة الفعلية لحزمة 0.5.2. Windows CI الحالي [39863364728](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39863364728) على رأس الفرع `034fa598d8ec219e6c59019984013ac649055e5b` اجتاز Release build (**0 تحذيرات / 0 أخطاء**)، والاختبارات (**77/77**)، وتمرير النقرات (**4/4**)، والسحب/حفظ/استعادة الموضع (**6/6**)، وتشخيص DPI (**11 تحققًا**)، والتحقق من حزمة المصدر ورفع artifacts.

تم تحديث حالة المتطلبات التي لها دليل آلي محدد فقط. لم تُحوّل واجهات أو تكاملات غير مختبرة إلى «متحققة». لا تزال بوابات القبول اليدوي على Windows فعلية مطلوبة، خصوصًا tray والإخفاء/الإظهار، والنقر المفرد والمزدوج والقائمة السياقية، وإعادة التشغيل، ومقاييس 100/125/150/175%، وعبور شاشتين مختلفتي DPI. كشف CI شاشة واحدة عند 96 DPI / 100% فقط. كما أن Gate E غير مكتمل: المثبّت والتحديث والتراجع والإزالة والتوقيع الرقمي ومراجعة التراخيص/SBOM ليست مثبتة كجزء من حزمة الإصدار الحالية.


## المرحلة الرابعة — قرار سابق قبل استكمال مسار التوزيع — 23 أكتوبر 2026

> هذه لقطة قرار سابقة على استكمال حزمة التوزيع الذاتية واختبارات دورة حياة المثبّت. راجع القسم الأحدث في نهاية الملف للحصول على حالة Gate E المحدثة.

**القرار: صالح للاختبار الداخلي/التجريبي فقط؛ غير معتمد للدمج أو التوزيع النهائي حتى إغلاق بوابات القبول اليدوي.**

- اجتاز Windows CI run [39863364728](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39863364728) جميع خطوات workflow لشفرة الإصدار 0.5.2: فحص المصدر، والتحقق من ZIP، وRestore، وبناء Release، والاختبارات، ورفع الأدلة.
- تقرير TRX المحفوظ في artifact يثبت **77/77 اختبارًا ناجحًا**، دون فشل أو أخطاء أو timeouts. تقرير تمرير النقرات يثبت **4/4**، وتقرير السحب/الحفظ/الاستعادة بعد إعادة التشغيل يثبت **6/6**.
- اجتاز تشخيص العرض داخل CI، لكنه سجّل شاشة واحدة فقط عند **96 DPI / 100%** ولم يرصد انتقال شاشة أو DPI. لذلك تظل مقاييس Windows الحقيقية وتعدد الشاشات غير معتمدين.
- فُحصت سلامة ZIP لأرشيفات الأدلة والحزمة المستخرجة؛ حزمة Windows الحالية تحتوي `Layal.exe` وملفات DLL و`Layal.runtimeconfig.json` الذي يطلب `Microsoft.NETCore.App 10.0.0` و`Microsoft.WindowsDesktop.App 10.0.0`. هذه حزمة تشغيل معتمدة على تثبيت .NET 10، وليست مثبتًا ذاتي الاحتواء.
- **قرار PR #6: إبقاؤه Draft وعدم دمجه** حتى توثيق القبول الحي للماوس وTray وإعادة التشغيل واللغة/الثيم والشاشات وDPI على جهاز Windows الهدف. ولا تُعد النسخة منتجًا نهائيًا قابلًا للتوزيع؛ فالمثبت والتحديث والتراجع والإزالة والتوقيع ومراجعة SBOM/التراخيص ما زالت خارج الحزمة المتحققة.

## بدء معالجة الجاهزية للتوزيع النهائي — 23 أكتوبر 2026

أُضيف إلى الفرع مسار تجهيز للتوزيع بدل الاكتفاء بمجلد build معتمد على Runtime خارجي:

- installer/Layal.iss: مثبّت Inno Setup لكل مستخدم في %LOCALAPPDATA%\Programs\Layal، مع اختصارات وقيد إغلاق التطبيق أثناء الترقية. مجلد بيانات المستخدم %LOCALAPPDATA%\Layal منفصل ولا يحذفه المثبّت عند الإزالة.
- tools/Build-LayalDistribution.ps1 وBUILD-DISTRIBUTION.bat: نشر ذاتي التشغيل لـ Windows x64، وحزمة ZIP محمولة، وCycloneDX SBOM، وقائمة تبعيات، وبصمات SHA-256، وملف يعلن بوابات الإصدار المفتوحة.
- tools/Test-LayalInstaller.ps1: اختبار Windows آلي لمسار التثبيت والترقية والتراجع والإزالة مع تشغيل التطبيق وفحص بقاء ملف بيانات اختبار داخل مجلد بيانات المستخدم.
- tools/validate_distribution.py وWindows CI: فحص إعدادات المصدر والمخرجات، وسلامة ZIP وSBOM، وصحة البصمات؛ ورفع حزمة التوزيع كـ artifact.

**حالة التحقق الآلي:** اجتاز Windows CI دورة حياة المثبّت الموسعة في [run 39962310797](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39962310797): تثبيت بالمسار الافتراضي، فحص إصدار Registry واختصار Start، تشغيل التطبيق، ترقية، رجوع، وإزالة الملفات والاختصار وسجل الإزالة مع بقاء محتوى بيانات المستخدم. اختبارات C# الآلية في التشغيل نفسه اجتازت **77/77**.

**القيود المتبقية:** يستخدم الرجوع fixture اصطناعيًا من المصدر الحالي مع رقم أقدم، فلا يثبت توافق binary تاريخي حقيقي أو ترحيل بيانات قديمة. الحزمة غير موقعة، ومراجعة الترخيص/SBOM وإشعارات الأصول ما زالت مفتوحة، والقبول اليدوي على جهاز Windows الهدف مطلوب، خصوصًا Tray واللغة/الثيم وتغيّر DPI والشاشات المتعددة.

لذلك يظل القرار **حزمة تجريبية قابلة للاختبار، وليست إصدارًا عامًا معتمدًا** حتى إغلاق التوقيع الرقمي والتراخيص والقبول اليدوي وقرار الإصدار.


## المرحلة الثالثة — الأمان والتبعيات والتراخيص — 23 أكتوبر 2026

أُضيف إلى الفرع مسار قابل لإعادة الإنتاج لتدقيق سلسلة التوريد:

- Directory.Build.props يفعّل NuGet Audit على الاعتماديات المباشرة والمتعدية بمستوى low، ويوقف البناء إذا تعذّر جلب قاعدة الاستشارات الأمنية.
- tools/audit_nuget.py يصدر تقرير JSON ويوقف CI إذا وجد استشارات ثغرات أو تقريرًا لا يمكن تفسيره. يراجع هذا التدقيق ملف الحل، بما فيه حزم الاختبارات، وليس مشروع الواجهة وحده.
- Workflow أمني مستقل يشغّل Gitleaks على تاريخ Git كامل؛ أضيف Dependabot لتحديث NuGet وإجراءات GitHub أسبوعيًا.
- أضيفت مراجعة SBOM مقابل قائمة مكونات/رخص، ونصوص WTFPL وMIT وإشعارات الطرف الثالث إلى المثبت والحزمة المحمولة.

المتبقي قانونيًا: رخصة Layal نفسها ما زالت غير معلنة، ولم يوثّق المستودع بعد مصدر وحقوق إعادة توزيع أصول الشخصية/المرجع/أيقونة Tray. لذلك لا تمنح إشعارات الطرف الثالث رخصة للمشروع، وتبقى المراجعة القانونية والقبول اليدوي والتوقيع الرقمي مفتوحة. حالة النجاح/الفشل الآلية لهذه الإضافات تتحدد بنتيجة CI للكوميت الجديد.


### المرحلة الرابعة — التوقيع الرقمي وسلامة الملفات — 23 أكتوبر 2026

مسار التوقيع والتحقق مضاف إلى الفرع: `tools/Sign-LayalFile.ps1` يوقّع التطبيق والمُثبّت عبر Authenticode/SHA-256 وختم RFC 3161، بينما `tools/Verify-LayalDistributionIntegrity.ps1` يطابق كل مخرجات التوزيع مع `SHA256SUMS.txt` ويتحقق من توقيعي التطبيق والمُثبّت عند استخدام مسار إصدار موقّع. الـ workflow يمنع رفع artifacts إصدار عند غياب أسرار الشهادة أو فشل التوقيع/التحقق.

**الدليل الفعلي المتاح الآن:** [Windows CI run 39976840663](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39976840663) على commit `19fd097d5f3232158c70858c3a570ca54c6fa0c8` اجتاز البناء وفحص الحزمة. تحقق من 10 بصمات SHA-256، وأكد حالة التوقيع غير الموقّع صراحةً، ثم انتهى التحقق النهائي `PASS`. هذا اختبار سلامة الحزمة غير الموقعة وليس اختبارًا لشهادة موثوقة.

**ما زال مفتوحًا:** لم تُضبط أسرار شهادة Code Signing فعلية، ولم يُنفذ إصدار tag ناجح موقّع مع timestamp. لا تعتمد الإصدار على أنه موقّع قبل اجتياز هذه البوابة؛ وتبقى قواعد حماية إنشاء tags مسؤولية إعدادات المستودع.


مراجعة إضافية لمرحلة 4: تم توسيع التوقيع والتحقق ليشملا Layal.dll أيضًا، لأنها تجميعة .NET التي تحتوي شفرة التطبيق وليس Layal.exe وحده. عند توفر شهادة موثوقة، يجب أن يُوقَّع الملفان قبل إنشاء ZIP، ثم يتحقق الفاحص من توقيع الاثنين بالإضافة إلى المُثبّت. ويظل تشغيل PR CI الحالي غير موقّع لأن مسار PR لا يستخدم أسرار الشهادة.


دليل CI لتوسيع سلامة التوزيع: [run 39981225471](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39981225471) على commit `216b16df00a6bf8719bb4560306f144a72956555` اجتاز جميع خطوات Windows CI. أظهر سجل البناء أن `Layal.exe` و`Layal.dll` والمُثبّت غير موقعة لعدم وجود أسرار الشهادة، وأن مدقق التوزيع تحقق من 10 بصمات SHA-256 وأعاد `Generated package validation: PASS`. هذا دليل على سلامة الحزمة غير الموقعة، وليس اعتماد هوية ناشرها.
