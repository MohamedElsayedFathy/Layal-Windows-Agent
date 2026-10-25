#!/usr/bin/env python3
"""Guard the Layal distribution pipeline and optionally validate generated release artifacts."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
errors: list[str] = []
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--artifacts", action="store_true", help="Validate the generated Windows distribution folder.")
parser.add_argument("--require-signed", action="store_true", help="Reject unsigned artifacts; use only for tagged release validation.")
args = parser.parse_args()
if args.require_signed and not args.artifacts:
    parser.error("--require-signed requires --artifacts")

required_source_files = [
    "installer/Layal.iss",
    "tools/Build-LayalDistribution.ps1",
    "BUILD-DISTRIBUTION.bat",
    "tools/Test-LayalInstaller.ps1",
    "tools/audit_nuget.py",
    "tools/validate_licenses.py",
    "tools/Sign-LayalFile.ps1",
    "tools/Verify-LayalDistributionIntegrity.ps1",
    "docs/CODE_SIGNING_AR.md",
    "Directory.Build.props",
    ".github/dependabot.yml",
    ".github/workflows/windows-build.yml",
    ".github/workflows/security-supply-chain.yml",
    "docs/DISTRIBUTION_AR.md",
    "docs/THIRD_PARTY_LICENSES.json",
    "docs/THIRD_PARTY_NOTICES.md",
    "docs/PROJECT_LICENSE_STATUS_AR.md",
    "docs/SECURITY_SUPPLY_CHAIN_AR.md",
    "licenses/WTFPL.txt",
    "licenses/MIT-JeremyAnsel.HLSL.Targets.txt",
    "licenses/PROJECT-LICENSE-STATUS.md",
]
for rel in required_source_files:
    if not (ROOT / rel).is_file():
        errors.append(f"Required distribution pipeline file missing: {rel}")

installer_path = ROOT / "installer/Layal.iss"
builder_path = ROOT / "tools/Build-LayalDistribution.ps1"
lifecycle_path = ROOT / "tools/Test-LayalInstaller.ps1"
workflow_path = ROOT / ".github/workflows/windows-build.yml"
project_path = ROOT / "src/Layal.Desktop/Layal.Desktop.csproj"
doc_path = ROOT / "docs/DISTRIBUTION_AR.md"

def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        errors.append(f"Could not read required text file: {path.relative_to(ROOT)}")
        return ""

installer = read_text(installer_path)
builder = read_text(builder_path)
lifecycle = read_text(lifecycle_path)
workflow = read_text(workflow_path)
project = read_text(project_path)
doc = read_text(doc_path)

installer_requirements = {
    "stable product identity": r"(?im)^AppId=\{\{[0-9A-F-]{36}\}",
    "per-user install": r"(?im)^PrivilegesRequired=lowest\s*$",
    "x64-compatible Windows installer": r"(?im)^ArchitecturesAllowed=x64compatible\s*$",
    "64-bit install mode uses x64-compatible Windows": r"(?im)^ArchitecturesInstallIn64BitMode=x64compatible\s*$",
    "install path separate from user data": r"(?im)^DefaultDirName=\{localappdata\}\\Programs\\Layal\s*$",
    "uninstaller enabled": r"(?im)^Uninstallable=yes\s*$",
    "active app close handling": r"(?im)^CloseApplications=yes\s*$",
    "self-contained publish payload reference": r'Source: "\{#PublishDir\}\\\*',
}
for label, pattern in installer_requirements.items():
    if not re.search(pattern, installer):
        errors.append(f"Installer is missing required safety/configuration rule: {label}")
if re.search(r"(?im)^\s*\[UninstallDelete\]", installer):
    errors.append("Installer must not define an UninstallDelete section; user data must be preserved.")

for needle, label in [
    ("--self-contained", "self-contained .NET publish"),
    ("CycloneDX", "SBOM generation"),
    ("SHA256SUMS.txt", "SHA-256 manifest"),
    ("Test-LayalInstaller.ps1", "installer lifecycle test"),
    ("validate_licenses.py", "SBOM license inventory validation"),
    ("--enrich-sbom-license-metadata", "explicit CycloneDX license metadata enrichment from cited inventory"),
    ("THIRD-PARTY-NOTICES.md", "third-party notice payload"),
    ("PROJECT-LICENSE-STATUS.md", "project-license status disclosure"),
    ("PASS: fresh install, upgrade, rollback", "successful installer lifecycle evidence"),
    ("NOT RUN", "explicit status when lifecycle tests are skipped"),
    ("NOT SIGNED", "explicit unsigned-release warning"),
    ("RequireCodeSigning", "tagged-release fail-closed signing gate"),
    ("--require-release-clearance", "tagged-release legal/provenance clearance gate"),
    ("Sign-LayalFile.ps1", "Authenticode signing script"),
    ("Verify-LayalDistributionIntegrity.ps1", "signature and checksum coverage verification"),
    ("SIGNED AND VERIFIED", "positive signed-release status"),
    ("Layal.dll", "signed managed application assembly"),
    ("RFC 3161", "trusted timestamp configuration"),
]:
    if needle not in builder:
        errors.append(f"Distribution builder is missing: {label}")
for needle, label in [
    ("sentinelPath", "test data sentinel"),
    ("sentinelContents", "test data sentinel content fingerprint"),
    ("Assert-UserDataSentinel", "data-preservation assertions across lifecycle stages"),
    ("Programs\\Layal", "default per-user installation path test"),
    ("Get-LayalUninstallEntries", "Add/Remove Programs registry inspection"),
    ("Assert-StartMenuShortcut", "Start Menu shortcut target validation"),
    ("Start Menu shortcut remains after uninstall", "shortcut cleanup assertion"),
    ("Assert-Uninstalled", "post-uninstall cleanup assertions"),
    ("Refusing to overwrite a pre-existing default install folder", "safe clean-profile preflight"),
    ("$testOwnsInstall", "cleanup ownership state"),
    ("if ($testOwnsInstall -and", "cleanup restricted to test-owned installs"),
    ("unins000.exe", "uninstall test"),
]:
    if needle.lower() not in lifecycle.lower():
        errors.append(f"Installer lifecycle test is missing: {label}")
if "-RunInstallerLifecycleTests" not in workflow or "artifacts/distribution/**/*" not in workflow:
    errors.append("Windows CI must run installer lifecycle tests and upload the distribution artifact.")
if "<Version>" not in project:
    errors.append("Could not find a source version to bind distribution artifacts.")
if "التوقيع الرقمي" not in doc or "SBOM" not in doc or "القبول اليدوي" not in doc:
    errors.append("Distribution documentation must state signing, SBOM/license, and manual acceptance limitations.")

print("Layal distribution source validation")
print(f"Required source files checked: {len(required_source_files)}")
print("Source configuration: " + ("PASS" if not errors else "FAIL"))

if args.artifacts:
    try:
        project_xml = re.search(r"<Version>([^<]+)</Version>", project)
        if not project_xml:
            raise ValueError("Project version is missing.")
        version = project_xml.group(1).strip()
        distribution = ROOT / "artifacts/distribution"
        required_outputs = [
            distribution / f"Layal-Setup-{version}-win-x64.exe",
            distribution / f"Layal-{version}-portable-win-x64.zip",
            distribution / f"Layal-{version}-dependencies.txt",
            distribution / f"Layal-{version}-release-status.txt",
            distribution / "sbom" / f"Layal-{version}.cdx.json",
            distribution / "licenses" / f"Layal-{version}-license-audit.md",
            distribution / "licenses" / "THIRD-PARTY-NOTICES.md",
            distribution / "licenses" / "WTFPL.txt",
            distribution / "licenses" / "MIT-JeremyAnsel.HLSL.Targets.txt",
            distribution / "licenses" / "PROJECT-LICENSE-STATUS.md",
            distribution / "SHA256SUMS.txt",
        ]
        for path in required_outputs:
            if not path.is_file():
                errors.append(f"Generated distribution output missing: {path.relative_to(ROOT)}")

        setup = distribution / f"Layal-Setup-{version}-win-x64.exe"
        if setup.is_file() and setup.stat().st_size < 1_000_000:
            errors.append("Generated installer is unexpectedly small.")
        portable = distribution / f"Layal-{version}-portable-win-x64.zip"
        if portable.is_file():
            try:
                with zipfile.ZipFile(portable) as archive:
                    damaged = archive.testzip()
                    names = set(archive.namelist())
                    for member in (
                        "Layal.exe", "Layal.dll", "coreclr.dll", "hostfxr.dll",
                        "licenses/THIRD-PARTY-NOTICES.md",
                        "licenses/WTFPL.txt",
                        "licenses/MIT-JeremyAnsel.HLSL.Targets.txt",
                        "licenses/PROJECT-LICENSE-STATUS.md",
                    ):
                        if member not in names:
                            errors.append(f"Portable archive is missing runtime/license file: {member}.")
                    if damaged:
                        errors.append(f"Portable archive CRC check failed at {damaged}.")
            except zipfile.BadZipFile:
                errors.append("Portable ZIP is invalid.")
        sbom = distribution / "sbom" / f"Layal-{version}.cdx.json"
        if sbom.is_file():
            try:
                data = json.loads(sbom.read_text(encoding="utf-8-sig"))
                if data.get("bomFormat") != "CycloneDX" or not data.get("components"):
                    errors.append("SBOM is not CycloneDX JSON with dependency components.")
            except (OSError, json.JSONDecodeError):
                errors.append("SBOM JSON is invalid.")
        checksum_file = distribution / "SHA256SUMS.txt"
        manifest_paths: set[str] = set()
        if checksum_file.is_file():
            lines = checksum_file.read_text(encoding="ascii").splitlines()
            if not lines:
                errors.append("SHA256SUMS.txt is empty.")
            for line in lines:
                match = re.fullmatch(r"([0-9a-fA-F]{64})  (.+)", line)
                if not match:
                    errors.append(f"Malformed SHA-256 line: {line}")
                    continue
                expected, relative = match.groups()
                relative = relative.replace("\\", "/")
                if relative.startswith("/") or ".." in relative.split("/") or relative == "SHA256SUMS.txt":
                    errors.append(f"Unsafe SHA-256 manifest path: {relative}")
                    continue
                if relative in manifest_paths:
                    errors.append(f"Duplicate SHA-256 manifest entry: {relative}")
                    continue
                manifest_paths.add(relative)
                target = (distribution / relative).resolve()
                if distribution.resolve() not in target.parents:
                    errors.append(f"Checksum path escapes distribution folder: {relative}")
                    continue
                if not target.is_file():
                    errors.append(f"Checksum references missing file: {relative}")
                    continue
                actual = hashlib.sha256(target.read_bytes()).hexdigest()
                if actual.lower() != expected.lower():
                    errors.append(f"SHA-256 mismatch: {relative}")
            actual_paths = {
                item.relative_to(distribution).as_posix()
                for item in distribution.rglob("*")
                if item.is_file() and item.resolve() != checksum_file.resolve()
            }
            for missing in sorted(actual_paths - manifest_paths):
                errors.append(f"File is not covered by SHA256SUMS.txt: {missing}")
            for extra in sorted(manifest_paths - actual_paths):
                errors.append(f"SHA256SUMS.txt contains a path not in the distribution: {extra}")
        license_report = distribution / "licenses" / f"Layal-{version}-license-audit.md"
        if license_report.is_file():
            license_text = license_report.read_text(encoding="utf-8-sig").lower()
            for needle in ("project license status", "asset provenance", "review"):
                if needle not in license_text:
                    errors.append(f"License audit report is missing required inventory context: {needle}")
            if args.require_signed and (
                "**pass with open legal gates**" in license_text
                or "**fail**" in license_text
            ):
                errors.append("A signed public release cannot contain unresolved project/package/asset legal gates.")
        status_file = distribution / f"Layal-{version}-release-status.txt"
        if status_file.is_file():
            status_text = status_file.read_text(encoding="utf-8-sig")
            if "NOT CERTIFIED" not in status_text:
                errors.append("Release status file must disclose that the legal/SBOM review is not certified.")
            signed_status = "Code signing: SIGNED AND VERIFIED" in status_text
            unsigned_status = "Code signing: NOT SIGNED" in status_text
            if not signed_status and not unsigned_status:
                errors.append("Release status file must explicitly report whether code signing was verified.")
            if signed_status:
                if not re.search(r"(?m)^Signing certificate thumbprint: [0-9A-F]{40}$", status_text):
                    errors.append("Signed release status must include the validated certificate thumbprint.")
                for required_status in ("Authenticode verification: PASS", "RFC 3161 timestamp: PASS", "Layal.dll"):
                    if required_status not in status_text:
                        errors.append(f"Signed release status is missing: {required_status}")
            elif "Authenticode verification: NOT RUN" not in status_text:
                errors.append("Unsigned builds must explicitly state that Authenticode verification was not run.")
            if args.require_signed and not signed_status:
                errors.append("This tagged release requires SIGNED AND VERIFIED artifacts.")
            if "Installer lifecycle CI: PASS:" not in status_text:
                errors.append("Release artifacts require a passing installer lifecycle test; rebuild with -RunInstallerLifecycleTests.")
        print(f"Generated outputs checked for version: {version}")
        print("Generated package validation: " + ("PASS" if not errors else "FAIL"))
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        errors.append(f"Artifact validation failed: {exc}")

for error in errors:
    print("ERROR:", error)
if errors:
    raise SystemExit(1)
print("RESULT: PASS")
