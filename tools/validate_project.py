#!/usr/bin/env python3
"""Portable static checks for Layal WPF/XAML wiring; does not replace dotnet build."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
ERRORS: list[str] = []


def check_csharp_delimiters(path: Path) -> None:
    """Conservative delimiter scan that skips comments and string/char literals."""
    source = path.read_text(encoding="utf-8")
    stack: list[tuple[str, int]] = []
    pairs = {")": "(", "]": "[", "}": "{"}
    i = 0
    state = "code"
    while i < len(source):
        c = source[i]
        n = source[i + 1] if i + 1 < len(source) else ""
        if state == "code":
            if c == "/" and n == "/":
                state = "line_comment"; i += 2; continue
            if c == "/" and n == "*":
                state = "block_comment"; i += 2; continue
            if source.startswith('"""', i):
                state = "raw_string"; i += 3; continue
            if c == "@" and n == '"':
                state = "verbatim_string"; i += 2; continue
            if c == "$" and n == "@" and i + 2 < len(source) and source[i + 2] == '"':
                state = "verbatim_string"; i += 3; continue
            if c == "@" and n == "$" and i + 2 < len(source) and source[i + 2] == '"':
                state = "verbatim_string"; i += 3; continue
            if c == '"':
                state = "string"; i += 1; continue
            if c == "'":
                state = "char"; i += 1; continue
            if c in "([{":
                stack.append((c, i))
            elif c in ")]}":
                if not stack or stack[-1][0] != pairs[c]:
                    ERRORS.append(f"Delimiter mismatch in {path.relative_to(ROOT)} at offset {i}: {c}")
                    return
                stack.pop()
            i += 1
        elif state == "line_comment":
            if c == "\n": state = "code"
            i += 1
        elif state == "block_comment":
            if c == "*" and n == "/": state = "code"; i += 2
            else: i += 1
        elif state == "raw_string":
            if source.startswith('"""', i): state = "code"; i += 3
            else: i += 1
        elif state == "string":
            if c == "\\": i += 2
            elif c == '"': state = "code"; i += 1
            else: i += 1
        elif state == "verbatim_string":
            if c == '"' and n == '"': i += 2
            elif c == '"': state = "code"; i += 1
            else: i += 1
        elif state == "char":
            if c == "\\": i += 2
            elif c == "'": state = "code"; i += 1
            else: i += 1
    if stack:
        ERRORS.append(f"Unclosed delimiters in {path.relative_to(ROOT)}: {stack[-5:]}")
    if state not in ("code", "line_comment"):
        ERRORS.append(f"Unterminated lexical state '{state}' in {path.relative_to(ROOT)}")


# Parse XAML, check duplicate element names within a XAML file and resolve routed handler names.
xaml_files = sorted(ROOT.rglob("*.xaml"))
all_names: dict[str, set[str]] = {}
for xaml_path in xaml_files:
    try:
        tree = ET.parse(xaml_path)
    except Exception as exc:
        ERRORS.append(f"XAML parse error in {xaml_path.relative_to(ROOT)}: {exc}")
        continue
    names: list[str] = []
    handlers: list[str] = []
    for element in tree.iter():
        for attr, value in element.attrib.items():
            if attr.endswith("}Name"):
                names.append(value)
            if attr in {
                "Click", "Checked", "Unchecked", "SelectionChanged", "KeyDown", "Closing",
                "MouseLeftButtonDown", "MouseRightButtonUp", "MouseEnter", "MouseLeave",
                "Loaded", "Unloaded", "Opened", "TextChanged", "ValueChanged", "LostFocus"
            }:
                handlers.append(value)
    # App.xaml contains multiple separate ControlTemplate name scopes, each legitimately uses PART_ContentHost.
    duplicates = sorted({name for name in names if names.count(name) > 1})
    if duplicates and xaml_path.name != "App.xaml":
        ERRORS.append(f"Duplicate x:Name in {xaml_path.relative_to(ROOT)}: {duplicates}")
    all_names[xaml_path.relative_to(ROOT).as_posix()] = set(names)

    # Every x:Class should map to an existing code-behind file. WPF compiler validation still
    # remains necessary because this check cannot resolve generated fields/types.
    xclass = next((value for attr, value in tree.getroot().attrib.items() if attr.endswith("}Class")), None)
    if xclass:
        code_path = xaml_path.with_name(xaml_path.name + ".cs")
        if not code_path.exists():
            ERRORS.append(f"Missing code-behind file for x:Class '{xclass}' in {xaml_path.relative_to(ROOT)}")

    if xaml_path.name in {"MainWindow.xaml", "FloatingMascotWindow.xaml", "VisionCameraWindow.xaml"}:
        code_path = xaml_path.with_name(xaml_path.name + ".cs")
        if code_path.exists():
            code = code_path.read_text(encoding="utf-8")
            for handler in handlers:
                if not re.search(r"\b" + re.escape(handler) + r"\s*\(", code):
                    ERRORS.append(f"Missing XAML event handler '{handler}' for {xaml_path.relative_to(ROOT)}")

# Check Layal.* StaticResource/DynamicResource references against App.xaml resource keys.
app_path = ROOT / "src/Layal.Desktop/App.xaml"
app_text = app_path.read_text(encoding="utf-8")
resource_keys = set(re.findall(r'x:Key="([^"]+)"', app_text))
for required_resource in (
    "Layal.Background", "Layal.Panel", "Layal.Text", "Layal.Muted",
    "Layal.CardShadow", "Layal.MascotShellGradient", "Layal.MascotFaceGradient",
):
    if required_resource not in resource_keys:
        ERRORS.append(f"Required design resource '{required_resource}' is missing from App.xaml")

background_match = re.search(r'<Color\s+x:Key="Layal\.Color\.Background">(#[A-Fa-f0-9]{6})</Color>', app_text)
if not background_match:
    ERRORS.append("Could not determine Layal background color")
else:
    hex_value = background_match.group(1).lstrip("#")
    red, green, blue = (int(hex_value[i:i + 2], 16) for i in (0, 2, 4))
    if (red + green + blue) / 3 < 220:
        ERRORS.append(f"Main application background does not appear light: {background_match.group(1)}")

main_window_xaml = ROOT / "src/Layal.Desktop/Views/MainWindow.xaml"
if main_window_xaml.exists():
    main_text = main_window_xaml.read_text(encoding="utf-8")
    legacy_dark_surfaces = (
        "#0A0F1E", "#0D1526", "#11192B", "#17243A", "#18243D",
        "#18263D", "#121B2E", "#20314B", "#20324C", "#192941", "#182842", "#293A58"
    )
    for old_color in legacy_dark_surfaces:
        if old_color.lower() in main_text.lower():
            ERRORS.append(f"Legacy dark surface {old_color} remains in MainWindow.xaml")

# Guard the exact dark-mode regression path: the visible button, resource replacement,
# live control surfaces, and durable settings must stay tested end-to-end.
main_window_theme_text = main_window_xaml.read_text(encoding="utf-8") if main_window_xaml.is_file() else ""
if not re.search(r'<Button[^>]*x:Name="ThemeButton"[^>]*Click="ThemeButton_Click"', main_window_theme_text):
    ERRORS.append("ThemeButton must remain wired to the real ThemeButton_Click event handler")

main_window_code_path = ROOT / "src/Layal.Desktop/Views/MainWindow.xaml.cs"
main_window_code_text = main_window_code_path.read_text(encoding="utf-8") if main_window_code_path.is_file() else ""
theme_handler_match = re.search(r"private void ThemeButton_Click\(.*?\n    \}", main_window_code_text, re.DOTALL)
if not theme_handler_match:
    ERRORS.append("Dark-mode toggle handler ThemeButton_Click is missing")
else:
    for fragment in (
        'AppRuntime.Settings.ThemeMode = IsDarkTheme ? "light" : "dark";',
        "ApplyTheme();",
        "ApplyLanguage();",
        "AppRuntime.SettingsStore.Save(AppRuntime.Settings);",
    ):
        if fragment not in theme_handler_match.group(0):
            ERRORS.append(f"ThemeButton_Click lost required toggle/persistence behavior: {fragment}")

theme_regression_test_path = ROOT / "tests/Layal.Tests/MainWindowVisualSmokeTests.cs"
theme_regression_test = theme_regression_test_path.read_text(encoding="utf-8") if theme_regression_test_path.is_file() else ""
if not theme_regression_test_path.is_file():
    ERRORS.append("Dark-mode UI regression test is missing from MainWindowVisualSmokeTests.cs")
else:
    for fragment in (
        "AssertThemeTogglePersistsLivePalette(window, historySelector);",
        "private static void AssertThemeTogglePersistsLivePalette(",
        "ThemeButton_Click",
        "Color.FromRgb(17, 21, 33)",
        "Color.FromRgb(244, 247, 252)",
        'Assert.AreEqual("dark", AppRuntime.SettingsStore.Load().ThemeMode,',
        'Assert.AreEqual("light", AppRuntime.SettingsStore.Load().ThemeMode,',
        'AssertThemeContrastAndControlSurfaces("dark", "ar", historySelector);',
        'AssertThemeContrastAndControlSurfaces("light", "ar", historySelector);',
    ):
        if fragment not in theme_regression_test:
            ERRORS.append(f"Dark-mode UI regression test lost required coverage: {fragment}")

# Accessibility regression gate: interactive controls must not suppress keyboard focus feedback.
if 'x:Key="Layal.KeyboardFocusVisual"' not in app_text:
    ERRORS.append("Shared Layal keyboard-focus visual resource is missing")
if app_text.count('<Setter Property="FocusVisualStyle" Value="{x:Null}" />') != 1:
    ERRORS.append("FocusVisualStyle must not be suppressed on interactive controls; only the non-focusable GroupBox style may opt out")
if 'KeyboardNavigation.DirectionalNavigation="Cycle"' not in app_text:
    ERRORS.append("Tab-strip directional keyboard navigation must cycle between navigation tabs")
for fragment in (
    'AutomationProperties.SetName(HomeTab,',
    'AutomationProperties.SetName(ChatInput,',
    'AutomationProperties.SetName(TaskInput,',
    'AutomationProperties.SetName(BaseUrlInput,',
    'AutomationProperties.SetName(ApiKeyInput,',
    'AutomationProperties.SetName(MemoryTitleInput,',
    'AutomationProperties.SetName(MascotSizeSlider,',
):
    if fragment not in main_window_code_text:
        ERRORS.append(f"Accessible control naming is missing: {fragment}")
if "AssertKeyboardAccessibility(window, sendButton" not in theme_regression_test:
    ERRORS.append("Keyboard focus and UI Automation accessibility regression test is missing")

if "AssertInitialWindowFitsWorkArea(window)" not in theme_regression_test:
    ERRORS.append("Responsive work-area window bounds regression test is missing")
if "AssertChatMessageDirectionContracts(window, applyLanguage)" not in theme_regression_test:
    ERRORS.append("Mixed Arabic/Latin and English message flow-direction regression test is missing")
if "AssertLanguageLayoutContracts(window, language)" not in theme_regression_test:
    ERRORS.append("RTL/LTR input and accessible task-result contract test is missing")

for required in (
    "private void MainWindow_Loaded(",
    "SystemParameters.WorkArea",
    "Math.Clamp(Left",
    "Math.Clamp(Top",
):
    if required not in main_window_code_text:
        ERRORS.append(f"Responsive window work-area behavior is missing: {required}")
if 'Loaded="MainWindow_Loaded"' not in main_window_theme_text:
    ERRORS.append("Responsive window Loaded event binding is missing")


# Guard the real interaction/settings paths so their UI bindings, persistence, and regression tests cannot silently disappear.
interaction_ui_needles = (
    'x:Name="LanguageButton" Content="English" Click="LanguageButton_Click"',
    'x:Name="GoChatButton" Content="فتح المحادثة" Click="GoChatButton_Click"',
    'x:Name="GoSettingsButton" Content="إعداد اتصال AI" Click="GoSettingsButton_Click"',
    'x:Name="SaveProviderButton" Content="حفظ الإعدادات" Click="SaveProviderButton_Click"',
    'x:Name="ApplyMascotSettingsButton" Content="تطبيق إعدادات الشخصية" Click="ApplyMascotSettingsButton_Click"',
    'x:Name="WindowInspectionCheckbox"',
)
for needle in interaction_ui_needles:
    if needle not in main_window_theme_text:
        ERRORS.append(f"Interaction/settings UI control lost its expected XAML wiring: {needle}")

for fragment in (
    'AppRuntime.Settings.Language = language;',
    'AppRuntime.SettingsStore.Save(AppRuntime.Settings);',
    'AppRuntime.Tray?.SetLanguage(language);',
):
    if fragment not in main_window_code_text:
        ERRORS.append(f"Language persistence path is missing '{fragment}'")

mascot_settings_match = re.search(r"private void ApplyMascotSettingsButton_Click\(.*?^\s*\}", main_window_code_text, re.DOTALL | re.MULTILINE)
if not mascot_settings_match:
    ERRORS.append("ApplyMascotSettingsButton_Click handler is missing")
else:
    for fragment in (
        'AppRuntime.Settings.MascotAlwaysOnTop = AlwaysOnTopCheckbox.IsChecked == true;',
        'AppRuntime.Settings.MascotAnimationsEnabled = AnimationsCheckbox.IsChecked == true;',
        'Math.Clamp(MascotSizeSlider.Value, 0.65, 1.8)',
        'Math.Clamp(MascotOpacitySlider.Value, 0.35, 1.0)',
        'AppRuntime.SettingsStore.Save(AppRuntime.Settings);',
        'AppRuntime.MascotWindow.ApplyScale(AppRuntime.Settings.MascotScale);',
    ):
        if fragment not in mascot_settings_match.group(0):
            ERRORS.append(f"Apply mascot settings behavior lost required fragment: {fragment}")

inspection_match = re.search(r"private void WindowInspectionSettingChanged\(.*?^\s*\}", main_window_code_text, re.DOTALL | re.MULTILINE)
if not inspection_match or "AppRuntime.SettingsStore.Save(AppRuntime.Settings);" not in inspection_match.group(0) or "WindowInspectionCheckbox.IsChecked == true" not in inspection_match.group(0):
    ERRORS.append("Window-inspection opt-in must persist its checked/unchecked state")

interaction_test_needles = (
    "AssertInteractionAndSettingsPersistence(window, mascot, temp.Path);",
    "private static void AssertInteractionAndSettingsPersistence(",
    "The language change must survive reloading settings.json.",
    "The live mascot must stop staying above other windows.",
    "Enabling optional window-title inspection must persist.",
    "Apply must update live window opacity.",
    "An invalid provider URL must not overwrite a previously saved URL.",
    "The show-mascot button must restore a hidden mascot window.",
)
for needle in interaction_test_needles:
    if needle not in theme_regression_test:
        ERRORS.append(f"Interactive settings regression test is missing coverage: {needle}")

settings_test_path = ROOT / "tests/Layal.Tests/SettingsStoreTests.cs"
settings_test_code = settings_test_path.read_text(encoding="utf-8") if settings_test_path.is_file() else ""
for needle in (
    'Assert.AreEqual("light", settings.ThemeMode);',
    "Assert.IsFalse(settings.AllowWindowTitleInspection);",
    "AllowWindowTitleInspection = true",
    "Assert.IsTrue(reloaded.AllowWindowTitleInspection);",
):
    if needle not in settings_test_code:
        ERRORS.append(f"Settings Store regression coverage is missing: {needle}")

project_file = ROOT / "src/Layal.Desktop/Layal.Desktop.csproj"
project_text = project_file.read_text(encoding="utf-8") if project_file.exists() else ""
version_match = re.search(r"<Version>([^<]+)</Version>", project_text)
if not project_text:
    ERRORS.append("Project file src/Layal.Desktop/Layal.Desktop.csproj is missing")
elif not version_match:
    ERRORS.append("Project version is not defined in the project file")
else:
    project_version = version_match.group(1).strip()
    readme_path = ROOT / "README_AR.md"
    if readme_path.exists() and not re.search(rf"الإصدار المصدري:\s*{re.escape(project_version)}\b", readme_path.read_text(encoding="utf-8")):
        ERRORS.append(f"README_AR.md source version does not match project version {project_version}")
    if "<TargetFramework>net10.0-windows</TargetFramework>" not in project_text:
        ERRORS.append("Target framework is not the expected net10.0-windows; verify this change deliberately")

# Explicitly declare per-monitor DPI behavior and the WPF runtime scaling switch.
manifest_path = ROOT / "src/Layal.Desktop/app.manifest"
if "<ApplicationManifest>app.manifest</ApplicationManifest>" not in project_text:
    ERRORS.append("Project file must embed src/Layal.Desktop/app.manifest")
if not manifest_path.is_file():
    ERRORS.append("Per-monitor DPI application manifest is missing")
else:
    try:
        manifest_root = ET.parse(manifest_path).getroot()
        dpi_awareness = manifest_root.find(".//{http://schemas.microsoft.com/SMI/2016/WindowsSettings}dpiAwareness")
        if dpi_awareness is None or (dpi_awareness.text or "").strip().lower() != "permonitorv2":
            ERRORS.append("Application manifest must declare dpiAwareness=PerMonitorV2")
    except Exception as exc:
        ERRORS.append(f"Application manifest XML parse error: {exc}")

app_config_path = ROOT / "src/Layal.Desktop/App.config"
if not app_config_path.is_file():
    ERRORS.append("WPF per-monitor scaling configuration src/Layal.Desktop/App.config is missing")
else:
    try:
        config_root = ET.parse(app_config_path).getroot()
        overrides = config_root.find("./runtime/AppContextSwitchOverrides")
        switch_value = overrides.get("value", "") if overrides is not None else ""
        if "Switch.System.Windows.DoNotScaleForDpiChanges=false" not in switch_value:
            ERRORS.append("App.config must enable WPF scaling on DPI changes")
    except Exception as exc:
        ERRORS.append(f"App.config XML parse error: {exc}")

hit_test_path = ROOT / "src/Layal.Desktop/Services/MascotHitTestGeometry.cs"
hit_test_tests_path = ROOT / "tests/Layal.Tests/MascotHitTestGeometryTests.cs"
if not hit_test_path.is_file() or not hit_test_tests_path.is_file():
    ERRORS.append("Mascot hit-test geometry and its regression tests must both exist")
else:
    mascot_window_code = (ROOT / "src/Layal.Desktop/Views/FloatingMascotWindow.xaml.cs").read_text(encoding="utf-8")
    if "MascotHitTestGeometry.IsPointOnMascot(point, Width," not in mascot_window_code:
        ERRORS.append("Floating mascot input handling is not using the testable hit-test geometry")
    for fragment in ("_hitTestTimer", "UpdateHitTestTransparency()", "WsExTransparent", "GetCursorPos(out NativePoint point)", "SetWindowLongPtrCompat(hwnd, GwlExStyle"):
        if fragment not in mascot_window_code:
            ERRORS.append(f"Cross-process transparent-padding click-through wiring is missing '{fragment}'")
    for fragment in ("_lastDragScreenPosition", "TransformFromDevice.Transform(delta)", "ClampToVirtualScreen(Left + delta.X, Top + delta.Y)", "SaveMascotPosition()"):
        if fragment not in mascot_window_code:
            ERRORS.append(f"Captured-pointer mascot drag wiring is missing '{fragment}'")

# Keep real Windows monitor/DPI diagnostics runnable in CI and on a physical multi-monitor machine.
display_dpi_harness_path = ROOT / "tools/Test-LayalDisplayDpi.ps1"
display_dpi_harness = display_dpi_harness_path.read_text(encoding="utf-8") if display_dpi_harness_path.is_file() else ""
if not display_dpi_harness_path.is_file():
    ERRORS.append("Display/DPI acceptance harness is missing: tools/Test-LayalDisplayDpi.ps1")
else:
    for fragment in (
        "EnumDisplayMonitors",
        "GetMonitorInfo",
        "GetDpiForMonitor",
        "GetDpiForWindow",
        "GetWindowDpiAwarenessContext",
        "RequireMultipleMonitors",
        "RequireMonitorTransition",
        "RequireDpiTransition",
        "TopologyDpiGateStatus",
        "TopologyDpiGateDetail",
        "TopologyDpiTransitionsObserved",
        "ManualVisualAcceptanceStatus",
        "pending",
        "not-certified",
        "$monitorTransitionCount -gt 0",
        "$dpiTransitionCount -gt 0",
        "display-dpi-summary.json",
        "display-dpi-samples.csv",
    ):
        if fragment not in display_dpi_harness:
            ERRORS.append(f"Display/DPI acceptance coverage is missing '{fragment}'")
display_dpi_workflow_path = ROOT / ".github/workflows/windows-build.yml"
display_dpi_workflow_code = display_dpi_workflow_path.read_text(encoding="utf-8") if display_dpi_workflow_path.is_file() else ""
if "Run Windows display and DPI topology smoke test" not in display_dpi_workflow_code or "Test-LayalDisplayDpi.ps1" not in display_dpi_workflow_code or "-ObserveSeconds 3" not in display_dpi_workflow_code:
    ERRORS.append("Windows CI must run the display/DPI topology smoke test")
if "Upload display and DPI topology evidence" not in display_dpi_workflow_code or "artifacts/display-dpi/**/*" not in display_dpi_workflow_code:
    ERRORS.append("Windows CI must upload display/DPI topology evidence")
for fragment in (
    "ConvertFrom-Json",
    '$summary.ManualVisualAcceptanceStatus -ne "pending"',
    '$summary.MonitorCount -lt 2 -and $summary.TopologyDpiGateStatus -ne "not-certified"',
    '$summary.TopologyDpiGateStatus -eq "passed" -and -not $summary.TopologyDpiTransitionsObserved',
):
    if fragment not in display_dpi_workflow_code:
        ERRORS.append(f"Windows CI must assert accurate multi-monitor acceptance reporting: {fragment}")

# Keep native acceptance from silently regressing to a one-off movement check. The Windows
# job must replay opposite-direction drags and verify persisted position after process restart.
native_acceptance_path = ROOT / "tools/Test-LayalClickThrough.Native.ps1"
workflow_path = ROOT / ".github/workflows/windows-build.yml"
native_acceptance = native_acceptance_path.read_text(encoding="utf-8") if native_acceptance_path.is_file() else ""
workflow_code = workflow_path.read_text(encoding="utf-8") if workflow_path.is_file() else ""
if not native_acceptance_path.is_file():
    ERRORS.append("Native Windows interaction acceptance harness is missing")
else:
    for fragment in (
        "$dragVectors = @(@{X=42;Y=28}",
        "Native mouse drag cycle $iteration",
        "Dragged position persisted to settings",
        "Dragged position restored after app restart",
        "DpiScaleForWindow",
        "UnderlyingClicks -ne $beforeClicks",
    ):
        if fragment not in native_acceptance:
            ERRORS.append(f"Native drag regression coverage is missing '{fragment}'")
if "Run native Windows desktop click-through integration" not in workflow_code or "-ClickThroughOnly" not in workflow_code:
    ERRORS.append("Windows CI must execute the isolated native click-through regression suite")
if "Run native Windows desktop mouse-drag integration" not in workflow_code or "-DragOnly" not in workflow_code:
    ERRORS.append("Windows CI must execute the isolated native mouse-drag regression suite")

# Prevent reintroducing the WPF DragMove path: it bypassed reliable movement for
# this transparent layered overlay under native input. Keep the direct captured-pointer
# delta implementation and its tested per-mode check counts wired into every CI run.
if re.search(r"\bDragMove\s*\(", mascot_window_code):
    ERRORS.append("Do not reintroduce DragMove; use the tested captured-pointer delta path")
minimum_check_rule = '$minimumChecks = if ($CalibrationOnly) { 1 } elseif ($ClickThroughOnly) { 4 } elseif ($DragOnly) { 6 } else { 5 }'
if native_acceptance.count(minimum_check_rule) != 2:
    ERRORS.append("Native acceptance check-count thresholds changed unexpectedly; update both summary and exit guard deliberately")
if 'Mode=if ($CalibrationOnly) { "calibration-only" } elseif ($ClickThroughOnly) { "click-through-only" } elseif ($DragOnly) { "drag-only" } else { "full-interactive-acceptance" }' not in native_acceptance:
    ERRORS.append("Native acceptance summary must identify the active test mode accurately")

for xaml_path in xaml_files:
    text = xaml_path.read_text(encoding="utf-8")
    for match in re.finditer(r"\{(StaticResource|DynamicResource)\s+([^}\s]+)\}", text):
        key = match.group(2)
        if key.startswith("Layal.") and key not in resource_keys:
            ERRORS.append(f"Unresolved Layal resource '{key}' in {xaml_path.relative_to(ROOT)}")

# Ensure key code-behind controls still exist after view redesign.
required_controls = {
    "src/Layal.Desktop/Views/MainWindow.xaml": [
        "BaseUrlInput", "ModelInput", "ApiKeyInput", "MainTabs", "ChatInput", "TaskInput",
        "SkillsList", "MemoryList", "TaskEventsList", "RecentTasksList", "ConnectionStatusText",
        "AlwaysOnTopCheckbox", "AnimationsCheckbox", "WindowInspectionCheckbox", "MascotSizeSlider",
        "MascotOpacitySlider", "TestProviderButton", "ProviderActionStatus", "TaskResult", "ChatMessagesList"
    ],
    "src/Layal.Desktop/Views/FloatingMascotWindow.xaml": [
        "Root", "AntennaDot", "GlowHalo", "LeftPupil", "RightPupil", "LeftEye", "RightEye",
        "LeftEyeShine", "RightEyeShine", "LeftBrow", "RightBrow", "CheekLeft", "CheekRight",
        "Mouth", "StatusDot", "AvatarViewport", "TaskPropsLayer", "PaperProp", "PencilProp", "HoloComputerProp", "CinematicEffectsLayer"
    ]
}
for xaml_file, controls in required_controls.items():
    xnames = all_names.get(xaml_file, set())
    for control in controls:
        if control not in xnames:
            ERRORS.append(f"Required control '{control}' missing from {xaml_file}")

# Guard the new personality milestone against accidental removal of its XAML/code-behind wiring.
mascot_code_path = ROOT / "src/Layal.Desktop/Views/FloatingMascotWindow.xaml.cs"
main_code_path = ROOT / "src/Layal.Desktop/Views/MainWindow.xaml.cs"
mascot_code = mascot_code_path.read_text(encoding="utf-8") if mascot_code_path.exists() else ""
main_code = main_code_path.read_text(encoding="utf-8") if main_code_path.exists() else ""
palette_path = ROOT / "src/Layal.Desktop/Services/UiThemePalette.cs"
palette_code = palette_path.read_text(encoding="utf-8") if palette_path.exists() else ""
resolver_path = ROOT / "src/Layal.Desktop/Services/TaskMoodResolver.cs"
resolver_code = resolver_path.read_text(encoding="utf-8") if resolver_path.exists() else ""
if not resolver_path.exists():
    ERRORS.append("Task-stage mood resolver is missing: src/Layal.Desktop/Services/TaskMoodResolver.cs")
if "TaskMoodResolver.Resolve(text, TaskEvents.TakeLast(4))" not in main_code:
    ERRORS.append("Main window is not using the tested task-stage mood resolver")
for required_fragment in (
    "InitializeAvatarRig()", "CreateHeadModel()", "CreateBodyModel()", "CreateArticulatedArmRig(",
    "CreateArticulatedLegRig(", "AddEyeRig(bool left)", "SetJointRotation(",
    "_leftForearmJointHost", "_rightHandJointHost", "_leftLowerLegJointHost", "_rightFootJointHost",
    "ApplyThreeDimensionalFaceExpression(", "CreateCurveStrip(", "ConfigureCinematicEffects(",
    "AnimateCinematicEffects()", "ApplyRigPose(_floatPhase)", "CreateEllipsoid(", "Viewport3D",
    "UpdateTaskProps()", "SetTaskActive(bool active)", "IsPointOnMascot(Point point)",
    "IsTaskMood(string mood)", "Transform3DGroup", "MeshGeometry3D",
):
    if required_fragment not in mascot_code and required_fragment not in (ROOT / "src/Layal.Desktop/Views/FloatingMascotWindow.xaml").read_text(encoding="utf-8"):
        ERRORS.append(f"3D/personality regression: required implementation fragment '{required_fragment}' is missing")
if "SetTaskActive(true)" not in main_code or "SetTaskActive(false)" not in main_code:
    ERRORS.append("Mascot task-active state is not wired to both task start and completion")
for mood in ("embarrassed", "relieved", "determined", "confident", "apologetic", "frustrated", "grateful", "celebrating"):
    if mascot_code.count(f'"{mood}"') < 4:
        ERRORS.append(f"New facial mood '{mood}' is not fully wired into expression/reaction lifecycle")
    if f'return "{mood}";' not in main_code:
        ERRORS.append(f"Chat context is not mapped to the new facial mood '{mood}'")
for mood in ("writing", "validating"):
    if mascot_code.count(f'"{mood}"') < 4:
        ERRORS.append(f"Task-stage mood '{mood}' is not fully wired into the mascot expression/reaction lifecycle")
    if f'"{mood}"' not in resolver_code:
        ERRORS.append(f"Task-stage resolver does not route mood '{mood}'")
if "ForInterfaceLanguage(string? language)" not in (ROOT / "src/Layal.Desktop/Services/LayalIdentity.cs").read_text(encoding="utf-8"):
    ERRORS.append("Selected interface language is not applied to Layal's system prompt")
if "ChatMessageContextMenu_Opened" not in main_code:
    ERRORS.append("Chat message context menu does not switch its label with the interface language")

if "Layal.Color.BackgroundEnd" not in app_text or "Layal.Color.BackgroundEnd" not in main_code:
    ERRORS.append("Theme-aware glass-style background gradient is not defined in both resources and runtime palette")
for expected_glass_color in ("#F4FFFFFF", "#E5222B3F"):
    if expected_glass_color not in palette_code:
        ERRORS.append(f"Expected translucent card color '{expected_glass_color}' is missing from the theme palette")


# Unified keyboard-accessibility and bilingual UI guardrails.
ui_xaml_path = ROOT / "src/Layal.Desktop/Views/MainWindow.xaml"
ui_code_path = ROOT / "src/Layal.Desktop/Views/MainWindow.xaml.cs"
app_xaml_path = ROOT / "src/Layal.Desktop/App.xaml"
ui_xaml = ui_xaml_path.read_text(encoding="utf-8") if ui_xaml_path.is_file() else ""
ui_code = ui_code_path.read_text(encoding="utf-8") if ui_code_path.is_file() else ""
app_xaml = app_xaml_path.read_text(encoding="utf-8") if app_xaml_path.is_file() else ""

for required in (
    'PreviewKeyDown="MainWindow_PreviewKeyDown"',
    'x:Name="KeyboardHelpButton"',
    'AutomationProperties.LabeledBy="{Binding ElementName=EndpointLabel}"',
    'AutomationProperties.LabeledBy="{Binding ElementName=ModelLabel}"',
    'AutomationProperties.LabeledBy="{Binding ElementName=ApiKeyLabel}"',
    'AutomationProperties.LabeledBy="{Binding ElementName=MemoryTitleLabel}"',
    'AutomationProperties.LabeledBy="{Binding ElementName=MemoryContentLabel}"',
):
    if required not in ui_xaml:
        ERRORS.append(f"Unified keyboard-accessibility markup is missing: {required}")

if 'x:Key="Layal.KeyboardFocusVisual"' not in app_xaml:
    ERRORS.append("Windows-highlight keyboard focus visual is missing from App.xaml")
focus_refs = len(re.findall(
    r'FocusVisualStyle[^>]*Layal\.KeyboardFocusVisual',
    app_xaml,
))
if focus_refs < 8:
    ERRORS.append("Unified focus visual is not connected to enough interactive control styles")
if not re.search(r"private\s+void\s+MainWindow_PreviewKeyDown\s*\(", ui_code):
    ERRORS.append("Keyboard navigation preview handler is missing")
if "Key.F1" not in ui_code or "ShowKeyboardHelp()" not in ui_code:
    ERRORS.append("F1 keyboard help shortcut is missing")
for digit in range(1, 8):
    if f"Key.D{digit} or Key.NumPad{digit}" not in ui_code:
        ERRORS.append(f"Ctrl+{digit} tab shortcut is missing")
if "FindFirstFocusableControl" not in ui_code:
    ERRORS.append("Keyboard tab switching does not attempt to focus tab content")

localization_path = ROOT / "src/Layal.Desktop/Services/UiLocalization.cs"
localization_text = localization_path.read_text(encoding="utf-8") if localization_path.is_file() else ""
if not localization_text:
    ERRORS.append("Shared Arabic/English UI localization service is missing")
for required in (
    "FormatTaskStatus", "FormatTaskResult", "FormatTaskEvent",
    "LocalizeApprovalTitle", "LocalizeApprovalDetails",
    "TranslateKnownFeedback", "FormatDateTime",
    "CompletedWithWarnings", "مكتملة مع تحذيرات",
    "هل تسمح لليال", "Technical details",
):
    if required not in localization_text:
        ERRORS.append(f"Bilingual localization coverage is missing: {required}")

for required in (
    "UiLocalization.FormatTaskResult", "UiLocalization.FormatTaskEvent",
    "UiLocalization.LocalizeApprovalDetails", "UiLocalization.TranslateKnownFeedback",
    "ChatSessionService.SetDefaultTitle", "ChatMessageContextMenu_Opening",
    "Filter = IsArabic ?",
):
    if required not in ui_code:
        ERRORS.append(f"Bilingual localization runtime wiring is missing: {required}")
if 'Opened="ChatMessageContextMenu_Opening"' not in ui_xaml:
    ERRORS.append("Chat message context menu does not refresh its label on open")
if "نسخ الرسالة / Copy message" in ui_xaml:
    ERRORS.append("Chat context menu must use a runtime-localized label, not a mixed-language static label")

chat_session_path = ROOT / "src/Layal.Desktop/Services/ChatSessionService.cs"
chat_session_text = chat_session_path.read_text(encoding="utf-8") if chat_session_path.is_file() else ""
if "public void SetDefaultTitle(string defaultTitle)" not in chat_session_text:
    ERRORS.append("Conversation placeholder titles cannot follow the selected UI language")
app_code_path = ROOT / "src/Layal.Desktop/App.xaml.cs"
app_code_text = app_code_path.read_text(encoding="utf-8") if app_code_path.is_file() else ""
if "ChatSessionService.SetDefaultTitle" not in app_code_text:
    ERRORS.append("Saved blank conversation titles are not normalized to the startup language")
mascot_code_path = ROOT / "src/Layal.Desktop/Views/FloatingMascotWindow.xaml.cs"
mascot_code_text = mascot_code_path.read_text(encoding="utf-8") if mascot_code_path.is_file() else ""
if "Root.ToolTip = localizedToolTip" not in mascot_code_text or 'Title = ar ? "ليال" : "Layal"' not in mascot_code_text:
    ERRORS.append("Floating mascot title/tooltip is not fully localized")

localization_test_path = ROOT / "tests/Layal.Tests/UiLocalizationTests.cs"
localization_test_text = localization_test_path.read_text(encoding="utf-8") if localization_test_path.is_file() else ""
if not localization_test_text:
    ERRORS.append("Automated bilingual UI localization tests are missing")
for required in (
    "TaskStatusesAreRenderedInTheSelectedLanguage",
    "TaskEventTranslationRetainsTimestamp",
    "ApprovalDetailsAreLocalized",
    "UnrecognizedModelOutputIsPreserved",
    "PlaceholderFeedbackCanBeSwitched",
):
    if required not in localization_test_text:
        ERRORS.append(f"Bilingual localization test coverage is missing: {required}")

# Voice-reply regression gate: protect the AI voice disclosure, secure Realtime endpoint, and auto-speech wiring.
voice_service_path = ROOT / "src/Layal.Desktop/Services/LayalSpeechService.cs"
voice_test_path = ROOT / "tests/Layal.Tests/LayalSpeechServiceTests.cs"
voice_docs_path = ROOT / "docs/VOICE_REPLY_AR.md"
voice_settings_path = ROOT / "src/Layal.Desktop/Models/UserSettings.cs"
voice_service_text = voice_service_path.read_text(encoding="utf-8") if voice_service_path.is_file() else ""
voice_test_text = voice_test_path.read_text(encoding="utf-8") if voice_test_path.is_file() else ""
voice_docs_text = voice_docs_path.read_text(encoding="utf-8") if voice_docs_path.is_file() else ""
voice_settings_text = voice_settings_path.read_text(encoding="utf-8") if voice_settings_path.is_file() else ""
if not voice_service_text:
    ERRORS.append("Layal Realtime voice service is missing")
else:
    for required in (
        'public const string DefaultModelId = "gpt-realtime-2.1-mini";',
        '"response.output_audio.delta"',
        '"audio/pcm"',
        'ClientWebSocket',
        'Array.Clear(pcm, 0, pcm.Length)',
        'Read the user\'s supplied text aloud exactly as written',
        'distinctly childlike and feminine',
        'young-girl character',
        'robotic, evenly timed cadence',
        'vary naturally',
    ):
        if required not in voice_service_text:
            ERRORS.append(f"Layal voice service is missing required behavior: {required}")
if not voice_test_text:
    ERRORS.append("Layal voice reply unit tests are missing")
for required in (
    "BuildRealtimeUri_RejectsRemoteHttpAndEmbeddedCredentials",
    "PrepareTextForSpeech_RemovesMarkupAndLinksButKeepsVisibleText",
    "SplitTextForSpeech_ProducesBoundedNonEmptyChunks",
    "IsSupportedVoice_OnlyAcceptsKnownVoicePresets",
):
    if required not in voice_test_text:
        ERRORS.append(f"Layal voice reply test coverage is missing: {required}")
for required in (
    'x:Name="VoiceReplyCheckbox"',
    'x:Name="VoiceReplyModelInput"',
    'x:Name="VoiceReplyVoiceSelector"',
    'x:Name="VoiceReplyDisclosureText"',
    'Click="TestVoiceReplyButton_Click"',
):
    if required not in ui_xaml:
        ERRORS.append(f"Layal voice reply control is missing from MainWindow.xaml: {required}")
for required in (
    "await _layalSpeech.SpeakAsync",
    "VoiceReplySettingChanged",
    "VoiceReplyVoiceSelector_SelectionChanged",
    "TestVoiceReplyButton_Click",
    "VoiceRepliesEnabled",
):
    if required not in ui_code:
        ERRORS.append(f"Layal voice reply runtime wiring is missing: {required}")
for required in (
    "VoiceRepliesEnabled { get; set; } = true",
    'VoiceReplyModelId { get; set; } = "gpt-realtime-2.1-mini"',
    'VoiceReplyId { get; set; } = "marin"',
):
    if required not in voice_settings_text:
        ERRORS.append(f"Layal voice reply defaults are missing: {required}")
if "AI-generated" not in ui_code or "ليس صوت إنسان" not in ui_xaml:
    ERRORS.append("AI-generated voice disclosure is missing")
if not voice_docs_text:
    ERRORS.append("Layal voice reply usage/privacy documentation is missing")

# Protect the full-duplex voice conversation path without coupling it to a replacement chat framework.
voice_conversation_path = ROOT / "src/Layal.Desktop/Services/VoiceConversationService.cs"
voice_conversation_test_path = ROOT / "tests/Layal.Tests/VoiceConversationServiceTests.cs"
voice_conversation_text = voice_conversation_path.read_text(encoding="utf-8") if voice_conversation_path.is_file() else ""
voice_conversation_test_text = voice_conversation_test_path.read_text(encoding="utf-8") if voice_conversation_test_path.is_file() else ""
if not voice_conversation_text:
    ERRORS.append("Full-duplex Realtime voice conversation service is missing")
for required in (
    'input_audio_buffer.append',
    'input_audio_buffer.speech_started',
    'conversation.item.truncate',
    'audio_end_ms = audioEndMs',
    'interrupt_response = true',
    'create_response = true',
    'new WaveIn',
    'new BufferedWaveProvider',
    'RaiseTranscript("user", userTranscript)',
    'RaiseTranscript("assistant", responseTranscript)',
):
    if required not in voice_conversation_text:
        ERRORS.append(f"Live voice conversation behavior is missing: {required}")
if not voice_conversation_test_text:
    ERRORS.append("Live voice conversation configuration tests are missing")
for required in (
    "BuildSessionUpdate_EnablesServerVadAndAutomaticInterruption",
    "BuildSessionUpdate_RejectsBlankModelAndUnsupportedVoice",
):
    if required not in voice_conversation_test_text:
        ERRORS.append(f"Live voice conversation regression coverage is missing: {required}")
for required in (
    'x:Name="VoiceConversationButton"',
    'x:Name="VoiceConversationIndicator"',
    'x:Name="VoiceConversationMicDot"',
    'x:Name="VoiceConversationMicStatus"',
):
    if required not in ui_xaml:
        ERRORS.append(f"Live voice conversation UI is missing: {required}")
for required in (
    "VoiceConversationButton_Click",
    "VoiceConversationService_StatusChanged",
    "VoiceConversationService_TranscriptReceived",
    "VoiceConversationService.BuildSessionUpdate",
    "_voiceConversationCancellation?.Cancel()",
    "VoiceConversationStatusMessage",
):
    if required not in ui_code and required != "VoiceConversationService.BuildSessionUpdate":
        ERRORS.append(f"Live voice conversation UI wiring is missing: {required}")

# Vision: user-selected images and opt-in still-frame camera observation.
vision_client_path = ROOT / "src/Layal.Desktop/Services/OpenAiCompatibleClient.cs"
vision_test_path = ROOT / "tests/Layal.Tests/OpenAiCompatibleClientTests.cs"
camera_xaml_path = ROOT / "src/Layal.Desktop/Views/VisionCameraWindow.xaml"
camera_code_path = ROOT / "src/Layal.Desktop/Views/VisionCameraWindow.xaml.cs"
vision_docs_path = ROOT / "docs/VISION_AR.md"
vision_client_text = vision_client_path.read_text(encoding="utf-8") if vision_client_path.is_file() else ""
vision_test_text = vision_test_path.read_text(encoding="utf-8") if vision_test_path.is_file() else ""
camera_xaml_text = camera_xaml_path.read_text(encoding="utf-8") if camera_xaml_path.is_file() else ""
camera_code_text = camera_code_path.read_text(encoding="utf-8") if camera_code_path.is_file() else ""
if "AnalyzeImageAsync(" not in vision_client_text:
    ERRORS.append("Multimodal image analysis API client is missing")
for required in (
    "AnalyzeImageSendsMultimodalImageUrlAndParsesTextResponse",
    "AnalyzeImageExplainsWhenProviderDoesNotSupportVisionInput",
    "AnalyzeImageRejectsEmptyImageBeforeMakingRequest",
):
    if required not in vision_test_text:
        ERRORS.append(f"Vision client regression test is missing: {required}")
for required in (
    'x:Name="ChooseImageButton"',
    'x:Name="ClearVisionImageButton"',
    'x:Name="CameraVisionButton"',
    'x:Name="CameraVisionIndicator"',
):
    if required not in ui_xaml:
        ERRORS.append(f"Image/camera chat UI is missing: {required}")
for required in (
    "ChooseImageButton_Click",
    "ClearVisionImageButton_Click",
    "CameraVisionButton_Click",
    "AnalyzeVisionImageAsync(",
    "UpdateCameraVisionIndicator()",
):
    if required not in ui_code:
        ERRORS.append(f"Image/camera chat flow is missing: {required}")
if not camera_xaml_text or not camera_code_text:
    ERRORS.append("Local webcam preview window or its code-behind is missing")
for required in (
    'x:Name="ContinuousObservationCheckBox"',
    'x:Name="AnalyzeCurrentFrameButton"',
    'x:Name="CloseCameraButton"',
):
    if required not in camera_xaml_text:
        ERRORS.append(f"Camera vision UI is missing: {required}")
for required in (
    "CameraStatusChanged",
    "ContinuousObservationCheckBox_Click",
    "ObserveRequested?.Invoke",
    "VideoCaptureAPIs.DSHOW",
    "VideoCaptureAPIs.MSMF",
):
    if required not in camera_code_text:
        ERRORS.append(f"Camera still-frame behavior is missing: {required}")
if not vision_docs_path.is_file():
    ERRORS.append("Image/camera vision privacy and acceptance guide is missing: docs/VISION_AR.md")
for required in ("OpenCvSharp4", "OpenCvSharp4.runtime.win"):
    if required not in project_text:
        ERRORS.append(f"Camera vision runtime dependency is missing from Layal.Desktop.csproj: {required}")

# Local-network ONVIF camera discovery and authenticated RTSP preview.
onvif_service_path = ROOT / "src/Layal.Desktop/Services/OnvifCameraDiscoveryService.cs"
onvif_test_path = ROOT / "tests/Layal.Tests/OnvifCameraDiscoveryServiceTests.cs"
onvif_text = onvif_service_path.read_text(encoding="utf-8") if onvif_service_path.is_file() else ""
onvif_test_text = onvif_test_path.read_text(encoding="utf-8") if onvif_test_path.is_file() else ""
if not onvif_text:
    ERRORS.append("Local ONVIF camera discovery service is missing")
for required in (
    "239.255.255.250",
    "DiscoverAsync(",
    "TryParseProbeResponse(",
    "GetAuthenticatedStreamUriAsync(",
    "BuildAuthenticatedStreamAddress(",
    "IsPrivateHost(",
    "IsPrivateCameraEndpointHost(",
    "IsSamePrivateCameraEndpointHost(",
    "DtdProcessing = DtdProcessing.Prohibit",
    "AllowAutoRedirect = false",
    "RandomNumberGenerator.GetBytes",
):
    if required not in onvif_text:
        ERRORS.append(f"ONVIF/RTSP security behavior is missing: {required}")
for required in (
    "TryParseProbeResponseAcceptsPrivateLocalOnvifEndpoint",
    "TryParseProbeResponseRejectsPublicEndpointEvenWhenReplyComesFromLan",
    "TryParseProbeResponseRejectsLoopbackDeviceEndpoints",
    "TryParseProbeResponseRejectsDifferentPrivateEndpointFromSender",
    "SameCameraHostValidationRejectsForeignPrivateAndUntrustedEndpoints",
    "TryParseProbeResponseRejectsRemoteOrMalformedXml",
    "BuildAuthenticatedStreamAddressRejectsNonLocalStreamsAndMissingCredentials",
    "CameraEndpointValidationRejectsLoopbackAndPublicAddresses",
    "IsPrivateHostOnlyAllowsLoopbackAndLocalPrivateAddressRanges",
):
    if required not in onvif_test_text:
        ERRORS.append(f"ONVIF/RTSP regression test is missing: {required}")
for required in (
    'x:Name="ScanNetworkCamerasButton"',
    'x:Name="DiscoveredCamerasSelector"',
    'x:Name="ManualRtspAddressInput"',
    'x:Name="CameraUsernameInput"',
    'x:Name="CameraPasswordInput"',
    'x:Name="ConnectNetworkCameraButton"',
    'x:Name="ScanSurroundingsButton"',
    'x:Name="SpeakCameraObservationsCheckBox"',
):
    if required not in camera_xaml_text:
        ERRORS.append(f"Camera/network discovery control is missing: {required}")
for required in (
    "ScanNetworkCamerasButton_Click",
    "ConnectNetworkCameraButton_Click",
    "UseLocalCameraButton_Click",
    "ScanSurroundingsButton_Click",
    "ScanSurroundingsRequested?.Invoke",
    "CameraPasswordInput.Clear()",
):
    if required not in camera_code_text:
        ERRORS.append(f"Camera/network discovery flow is missing: {required}")
if "SpeakAutomaticObservations" not in camera_code_text or "SpeakAutomaticObservations == true" not in ui_code:
    ERRORS.append("Spoken camera observations must remain an explicit opt-in")
if "ScanSurroundingsRequested" not in ui_code or "CameraVisionWindow_ScanSurroundingsRequested" not in ui_code:
    ERRORS.append("Main chat does not route one-shot surroundings scans to image analysis")


# Home dashboard clock/date and configurable current weather.
weather_service_path = ROOT / "src/Layal.Desktop/Services/WeatherService.cs"
weather_test_path = ROOT / "tests/Layal.Tests/WeatherServiceTests.cs"
weather_service_text = weather_service_path.read_text(encoding="utf-8") if weather_service_path.is_file() else ""
weather_test_text = weather_test_path.read_text(encoding="utf-8") if weather_test_path.is_file() else ""
for required in (
    "GetCurrentWeatherAsync(",
    "GetCurrentWeatherAtCoordinatesAsync(",
    "geocoding-api.open-meteo.com",
    "api.open-meteo.com/v1/forecast",
    "GetConditionText(",
    "GetConditionIcon(",
    "TimeZoneId",
):
    if required not in weather_service_text:
        ERRORS.append(f"Clock/weather service behavior is missing: {required}")
for required in (
    "GetCurrentWeatherAsyncUsesGeocodingAndReturnsCurrentConditions",
    "GetCurrentWeatherAsyncRejectsUnknownCity",
    "GetCurrentWeatherAsyncRejectsEmptyCityWithoutNetworkRequest",
    "GetCurrentWeatherAtCoordinatesAsyncUsesDetectedCoordinatesAndTimezone",
    "GetCurrentWeatherAtCoordinatesAsyncRejectsInvalidCoordinatesWithoutNetworkRequest",
    "WeatherCodeDescriptionsAreLocalized",
):
    if required not in weather_test_text:
        ERRORS.append(f"Weather service regression test is missing: {required}")
for required in (
    'x:Name="ClockTimeText"',
    'x:Name="CurrentDateText"',
    'x:Name="WeatherTemperatureText"',
    'x:Name="WeatherSummaryText"',
    'x:Name="WeatherLocationInput"',
    'x:Name="UseMyLocationButton"',
    'x:Name="SaveWeatherLocationButton"',
    'x:Name="RefreshWeatherButton"',
    'x:Name="WeatherPrivacyNote"',
):
    if required not in ui_xaml:
        ERRORS.append(f"Clock/weather UI control is missing: {required}")
for required in (
    "ClockTimer_Tick",
    "UpdateClockDisplay()",
    "WeatherRefreshTimer_Tick",
    "RefreshWeatherAsync()",
    "SaveWeatherLocationButton_Click",
    "UseMyLocationButton_Click",
    "RefreshLocationWeatherAsync()",
    "Geolocator.RequestAccessAsync",
    "GetGeopositionAsync(",
    "AppRuntime.Settings.UseAutomaticLocation",
    "WeatherService.GetConditionText(",
    "AppRuntime.Settings.WeatherLocation = location;",
):
    if required not in ui_code:
        ERRORS.append(f"Clock/weather UI wiring is missing: {required}")
if "WeatherLocation" not in (ROOT / "src/Layal.Desktop/Models/UserSettings.cs").read_text(encoding="utf-8"):
    ERRORS.append("Weather city preference is not persisted in UserSettings.cs")
if "UseAutomaticLocation" not in (ROOT / "src/Layal.Desktop/Models/UserSettings.cs").read_text(encoding="utf-8"):
    ERRORS.append("Automatic location preference is not persisted in UserSettings.cs")
if "Microsoft.Windows.SDK.NET.Ref" not in project_text:
    ERRORS.append("Windows SDK targeting pack is required for native geolocation")
location_service_path = ROOT / "src/Layal.Desktop/Services/DeviceLocationService.cs"
location_service_text = location_service_path.read_text(encoding="utf-8") if location_service_path.is_file() else ""
for required in ("Geolocator.RequestAccessAsync", "GetGeopositionAsync(", "DesiredAccuracyInMeters", "DeviceLocationAccessException"):
    if required not in location_service_text:
        ERRORS.append(f"Windows device-location behavior is missing: {required}")
if "docs/CLOCK_WEATHER_AR.md" not in (ROOT / "README_AR.md").read_text(encoding="utf-8"):
    ERRORS.append("Clock/weather documentation is not linked from README_AR.md")

trace_path = ROOT / "docs/REQUIREMENT_TRACEABILITY.csv"
trace_text = trace_path.read_text(encoding="utf-8-sig") if trace_path.is_file() else ""
language_requirement = next((line for line in trace_text.splitlines() if line.startswith("LAYAL-006,")), "")
if not language_requirement:
    ERRORS.append("Requirement traceability row LAYAL-006 is missing")
else:
    language_status = language_requirement.split(",")[3].strip() if len(language_requirement.split(",")) > 3 else ""
    if language_status in {"متحقق", "Verified"}:
        ERRORS.append("LAYAL-006 must not be marked verified before Windows language acceptance is recorded")

cs_files = sorted(list((ROOT / "src").rglob("*.cs")) + list((ROOT / "tests").rglob("*.cs")))
for cs_path in cs_files:
    check_csharp_delimiters(cs_path)

test_project = ROOT / "tests/Layal.Tests/Layal.Tests.csproj"
if not test_project.is_file():
    ERRORS.append("Automated C# test project is missing: tests/Layal.Tests/Layal.Tests.csproj")
else:
    test_project_text = test_project.read_text(encoding="utf-8")
    for expected in ("<TargetFramework>net10.0-windows</TargetFramework>", 'Include="Microsoft.NET.Test.Sdk"', 'Include="MSTest"', "Layal.Desktop.csproj"):
        if expected not in test_project_text:
            ERRORS.append(f"Test project is missing expected configuration: {expected}")
    test_count = len(list((ROOT / "tests/Layal.Tests").glob("*Tests.cs")))
    if test_count < 4:
        ERRORS.append(f"Expected at least four C# test suites; found {test_count}")
if not (ROOT / "tools/validate_test_results.py").is_file():
    ERRORS.append("TRX result validator is missing: tools/validate_test_results.py")

icon_path = ROOT / "src/Layal.Desktop/Assets/layal-tray.ico"
if not icon_path.exists() or icon_path.stat().st_size < 1000:
    ERRORS.append("Branded tray icon is missing or unexpectedly small")
preview_path = ROOT / "src/Layal.Desktop/Assets/layal-mascot-modern-preview.png"
if not preview_path.exists() or preview_path.stat().st_size < 1000:
    ERRORS.append("Modern mascot design preview is missing or unexpectedly small")

print(f"Project root: {ROOT}")
print(f"C# source/test files: {len(cs_files)}")
print(f"Automated C# test suites: {len(list((ROOT / 'tests/Layal.Tests').glob('*Tests.cs')))}")
print(f"XAML files: {len(xaml_files)}")
print(f"App resource keys: {len(resource_keys)}")
print(f"Project version: {version_match.group(1).strip() if version_match else 'UNKNOWN'}")
print("XAML XML parsing: PASS" if not any("XAML parse error" in e for e in ERRORS) else "XAML XML parsing: FAIL")
print("Light theme/design resource checks: " + ("PASS" if not any("design resource" in e or "background color" in e or "appear light" in e or "Legacy dark surface" in e or "Project version" in e for e in ERRORS) else "FAIL"))
print("Resource/name/event/delimiter checks: " + ("PASS" if not ERRORS else "FAIL"))
for error in ERRORS:
    print("ERROR:", error)
print("LIMITATION: This script cannot replace the WPF compiler. Build/runtime tests still require the .NET SDK and Windows.")
raise SystemExit(1 if ERRORS else 0)
