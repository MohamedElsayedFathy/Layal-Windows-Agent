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

    if xaml_path.name in {"MainWindow.xaml", "FloatingMascotWindow.xaml"}:
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
    for fragment in ("MascotHitTestWindowProc", "AddHook(MascotHitTestWindowProc)", "WmNcHitTest", "HtTransparent"):
        if fragment not in mascot_window_code:
            ERRORS.append(f"Transparent-padding click-through wiring is missing '{fragment}'")

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
    if expected_glass_color not in main_code:
        ERRORS.append(f"Expected translucent card color '{expected_glass_color}' is missing from the theme palette")

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
