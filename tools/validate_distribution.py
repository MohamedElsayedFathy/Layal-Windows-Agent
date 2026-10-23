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
args = parser.parse_args()

required_source_files = [
    "installer/Layal.iss",
    "tools/Build-LayalDistribution.ps1",
    "tools/Test-LayalInstaller.ps1",
    "docs/DISTRIBUTION_AR.md",
    ".github/workflows/windows-build.yml",
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
    "x64-only installer": r"(?im)^ArchitecturesAllowed=x64\s*$",
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
    ("NOT SIGNED", "explicit unsigned-release warning"),
]:
    if needle not in builder:
        errors.append(f"Distribution builder is missing: {label}")
for needle, label in [
    ("sentinelPath", "test data sentinel"),
    ("sentinel was removed during upgrade", "upgrade data-preservation assertion"),
    ("sentinel was removed during rollback", "rollback data-preservation assertion"),
    ("sentinel was removed during uninstall", "uninstall data-preservation assertion"),
    ("unins000.exe", "uninstall test"),
]:
    if needle.lower() not in lifecycle.lower():
        errors.append(f"Installer lifecycle test is missing: {label}")
if "-RunInstallerLifecycleTests" not in workflow or "artifacts/distribution/**/*" not in workflow:
    errors.append("Windows CI must run installer lifecycle tests and upload the distribution artifact.")
if "<Version>" not in project:
    errors.append("Could not find a source version to bind distribution artifacts.")
if "NOT SIGNED" not in doc or "SBOM" not in doc or "القبول اليدوي" not in doc:
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
                    for member in ("Layal.exe", "coreclr.dll", "hostfxr.dll"):
                        if member not in names:
                            errors.append(f"Portable archive is not self-contained; missing {member}.")
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
        if checksum_file.is_file():
            lines = checksum_file.read_text(encoding="ascii").splitlines()
            if not lines:
                errors.append("SHA256SUMS.txt is empty.")
            for line in lines:
                match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
                if not match:
                    errors.append(f"Malformed SHA-256 line: {line}")
                    continue
                expected, relative = match.groups()
                target = (distribution / relative).resolve()
                if distribution.resolve() not in target.parents:
                    errors.append(f"Checksum path escapes distribution folder: {relative}")
                    continue
                if not target.is_file():
                    errors.append(f"Checksum references missing file: {relative}")
                    continue
                actual = hashlib.sha256(target.read_bytes()).hexdigest()
                if actual != expected:
                    errors.append(f"SHA-256 mismatch: {relative}")
        status_file = distribution / f"Layal-{version}-release-status.txt"
        if status_file.is_file():
            status_text = status_file.read_text(encoding="utf-8-sig")
            for needle in ("NOT SIGNED", "NOT CERTIFIED"):
                if needle not in status_text:
                    errors.append(f"Release status file must disclose: {needle}")
        print(f"Generated outputs checked for version: {version}")
        print("Generated package validation: " + ("PASS" if not errors else "FAIL"))
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        errors.append(f"Artifact validation failed: {exc}")

for error in errors:
    print("ERROR:", error)
if errors:
    raise SystemExit(1)
print("RESULT: PASS")
