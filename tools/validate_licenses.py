#!/usr/bin/env python3
"""Validate the curated third-party license inventory and optionally a CycloneDX SBOM."""
from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "docs/THIRD_PARTY_LICENSES.json"
APP_PROJECT = ROOT / "src/Layal.Desktop/Layal.Desktop.csproj"
NOTICES = ROOT / "docs/THIRD_PARTY_NOTICES.md"
PROJECT_STATUS = ROOT / "docs/PROJECT_LICENSE_STATUS_AR.md"
ERRORS: list[str] = []
WARNINGS: list[str] = []


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        ERRORS.append(f"Could not read valid JSON from {path.name}: {exc}")
        return None


def normalize_license(value: str) -> str:
    token = re.sub(r"[^A-Z0-9]+", "", value.upper())
    if "WTFPL" in token or "DOWHATTHEFUCKYOUWANT" in token:
        return "WTFPL"
    if token == "MIT" or ("MIT" in token and "LICENSE" in token):
        return "MIT"
    return token


def get_package_id(component: dict[str, Any]) -> str:
    purl = str(component.get("purl") or "")
    match = re.match(r"pkg:nuget/([^@?]+)", purl, re.IGNORECASE)
    if match:
        return match.group(1).replace("%2f", "/").lower()
    return str(component.get("name") or "").lower()


def component_licenses(component: dict[str, Any]) -> list[str]:
    values: list[str] = []
    expression = component.get("licenseExpression")
    if isinstance(expression, str) and expression.strip():
        values.append(expression.strip())
    for entry in component.get("licenses") or []:
        if not isinstance(entry, dict):
            continue
        if isinstance(entry.get("expression"), str):
            values.append(entry["expression"])
        license_obj = entry.get("license")
        if isinstance(license_obj, dict):
            for field in ("id", "name"):
                if isinstance(license_obj.get(field), str):
                    values.append(license_obj[field])
    return list(dict.fromkeys(values))


def direct_dependencies() -> set[str]:
    try:
        project = ET.parse(APP_PROJECT).getroot()
    except (OSError, ET.ParseError) as exc:
        ERRORS.append(f"Could not parse application project file: {exc}")
        return set()
    return {
        item.attrib.get("Include", "").lower()
        for item in project.findall(".//PackageReference")
        if item.attrib.get("Include")
    }


def validate_manifest(document: Any) -> dict[str, dict[str, Any]]:
    if not isinstance(document, dict) or document.get("schemaVersion") != 1:
        ERRORS.append("License manifest schemaVersion must be 1.")
        return {}

    project = document.get("projectLicense")
    if not isinstance(project, dict):
        ERRORS.append("Manifest must explicitly state the project license status.")
        project = {}
    state = project.get("status")
    if state == "UNDECLARED":
        if not project.get("releaseBlocker"):
            ERRORS.append("An undeclared project license must remain a release blocker.")
    elif state == "DECLARED":
        if project.get("releaseBlocker"):
            ERRORS.append("A declared project license cannot remain blocked in the manifest.")
        license_file = ROOT / "LICENSE"
        if not license_file.is_file() or not license_file.read_text(encoding="utf-8").strip():
            ERRORS.append("Project license status is DECLARED but root LICENSE is missing or empty.")
    else:
        ERRORS.append("Project license status must be UNDECLARED or DECLARED.")

    entries = document.get("packages")
    if not isinstance(entries, list) or not entries:
        ERRORS.append("License manifest must list third-party packages.")
        return {}

    packages: dict[str, dict[str, Any]] = {}
    for entry in entries:
        if not isinstance(entry, dict):
            ERRORS.append("Malformed third-party package entry.")
            continue
        name = str(entry.get("id") or "").strip()
        if not name:
            ERRORS.append("Third-party package entry has no id.")
            continue
        key = name.lower()
        if key in packages:
            ERRORS.append(f"Duplicate license manifest package: {name}")
            continue
        packages[key] = entry
        for field in ("licenseExpression", "sourceUrl", "licenseUrl", "licenseTextPath", "reviewStatus"):
            if not str(entry.get(field) or "").strip():
                ERRORS.append(f"{name} is missing required license metadata: {field}")
        license_path = ROOT / str(entry.get("licenseTextPath") or "")
        if not license_path.is_file():
            ERRORS.append(f"License text missing for {name}: {entry.get('licenseTextPath')}")
        if not str(entry.get("reviewStatus", "")).lower().endswith("pending."):
            WARNINGS.append(f"{name}: human legal review is not explicitly recorded as pending.")

    assets = document.get("assetsRequiringProvenanceReview")
    if not isinstance(assets, list) or not assets:
        ERRORS.append("Asset provenance review list must not be empty until reviewed.")
        assets = []
    for asset in assets:
        if not isinstance(asset, dict):
            ERRORS.append("Malformed asset provenance entry.")
            continue
        path = str(asset.get("path") or "")
        status = asset.get("status")
        if not path or not (ROOT / path).is_file():
            ERRORS.append(f"Asset in provenance register is missing: {path}")
        if status not in {"PROVENANCE_REVIEW_REQUIRED", "REVIEWED"}:
            ERRORS.append(f"Invalid provenance status for asset {path}: {status}")
        if status == "REVIEWED" and not str(asset.get("evidence") or "").strip():
            ERRORS.append(f"Reviewed asset requires provenance/permission evidence: {path}")

    expected_assets = {
        "src/Layal.Desktop/Assets/layal-mascot-modern-preview.png",
        "src/Layal.Desktop/Assets/layal-mascot-reference.png",
        "src/Layal.Desktop/Assets/layal-tray.ico",
    }
    listed_assets = {item.get("path") for item in assets if isinstance(item, dict)}
    for path in sorted(expected_assets - listed_assets):
        ERRORS.append(f"Asset provenance register is missing: {path}")

    for required in (NOTICES, PROJECT_STATUS, ROOT / "licenses/PROJECT-LICENSE-STATUS.md"):
        if not required.is_file():
            ERRORS.append(f"Required license notice/status file is missing: {required.relative_to(ROOT)}")
    if NOTICES.is_file():
        notice_text = NOTICES.read_text(encoding="utf-8").lower()
        for name in packages:
            if name not in notice_text:
                ERRORS.append(f"Third-party notices do not mention package {name}.")
    if PROJECT_STATUS.is_file() and "لم تُحدَّد رخصة" not in PROJECT_STATUS.read_text(encoding="utf-8"):
        ERRORS.append("Project license status document must state that licensing is undecided.")

    for dependency in sorted(direct_dependencies()):
        if dependency not in packages:
            ERRORS.append(f"Direct app dependency is absent from the license manifest: {dependency}.")
    return packages


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-only", action="store_true", help="Validate static notices without an SBOM.")
    parser.add_argument("--sbom", type=Path, help="CycloneDX JSON SBOM to compare against the inventory.")
    parser.add_argument("--report", type=Path, help="Write a Markdown report.")
    parser.add_argument("--require-project-license", action="store_true", help="Fail unless the rights holder declared a project license.")
    args = parser.parse_args()
    if not args.manifest_only and args.sbom is None:
        parser.error("provide --manifest-only or --sbom")

    document = read_json(MANIFEST)
    packages = validate_manifest(document)
    components: list[dict[str, Any]] = []
    sbom_label = "not provided"
    if args.sbom is not None:
        sbom_path = args.sbom if args.sbom.is_absolute() else ROOT / args.sbom
        sbom_label = str(sbom_path)
        bom = read_json(sbom_path)
        if not isinstance(bom, dict) or bom.get("bomFormat") != "CycloneDX":
            ERRORS.append("SBOM is not valid CycloneDX JSON.")
        else:
            raw = bom.get("components")
            if not isinstance(raw, list) or not raw:
                ERRORS.append("CycloneDX SBOM contains no components.")
            else:
                components = [item for item in raw if isinstance(item, dict)]

        seen: set[str] = set()
        for component in components:
            name = get_package_id(component)
            if not name:
                ERRORS.append(f"SBOM component has no package name or NuGet purl: {component.get('name')}")
                continue
            seen.add(name)
            entry = packages.get(name)
            if entry is None:
                ERRORS.append(
                    f"SBOM contains unmapped package {component.get('name')} "
                    f"(version {component.get('version', 'unknown')}); review and add it to the inventory."
                )
                continue
            expected = normalize_license(str(entry.get("licenseExpression") or ""))
            observed = component_licenses(component)
            if observed and expected not in {normalize_license(value) for value in observed}:
                ERRORS.append(
                    f"License mismatch for {entry['id']} {component.get('version', '')}: "
                    f"inventory={entry.get('licenseExpression')}, SBOM={observed}."
                )
            elif not observed:
                WARNINGS.append(
                    f"{entry['id']} {component.get('version', '')}: SBOM omits a license field; "
                    "the inventory relies on cited upstream package metadata."
                )
        for dependency in sorted(direct_dependencies()):
            if dependency not in seen:
                ERRORS.append(f"Direct app package missing from product SBOM: {dependency}.")

    if args.require_project_license and (
        not isinstance(document, dict) or document.get("projectLicense", {}).get("status") != "DECLARED"
    ):
        ERRORS.append("Project-wide license has not been declared by the rights holder.")

    status = document.get("projectLicense", {}).get("status", "UNKNOWN") if isinstance(document, dict) else "UNKNOWN"
    asset_items = document.get("assetsRequiringProvenanceReview", []) if isinstance(document, dict) else []
    pending_assets = [
        item.get("path") for item in asset_items
        if isinstance(item, dict) and item.get("status") != "REVIEWED"
    ]
    lines = [
        "# Layal license inventory audit",
        "",
        f"- Project license status: {status}",
        f"- SBOM: {sbom_label}",
        f"- Third-party packages in inventory: {len(packages)}",
        f"- SBOM components inspected: {len(components)}",
        f"- Asset provenance reviews still open: {len(pending_assets)}",
        "",
        "## Package inventory",
        "",
        "| Package ID | Resolved SBOM version | Declared license | Source | Review status |",
        "|---|---|---|---|---|",
    ]
    versions = {get_package_id(item): str(item.get("version") or "not present in this SBOM") for item in components}
    for key, entry in sorted(packages.items()):
        lines.append(
            f"| {entry['id']} | {versions.get(key, 'not present in product SBOM')} "
            f"| {entry['licenseExpression']} | [Upstream package]({entry['sourceUrl']}) "
            f"| {entry['reviewStatus']} |"
        )
    lines += [
        "",
        "## Open release gates",
        "",
        "- The project-wide license is not declared; third-party notices do not grant rights to Layal itself.",
        "- Provenance and redistribution permissions for mascot/reference/tray assets still require owner confirmation.",
        "- Human legal review remains required before public or commercial distribution.",
        "- This is an inventory consistency check, not legal advice or license certification.",
        "",
        "## Warnings",
        "",
    ]
    lines.extend([f"- {warning}" for warning in WARNINGS] or ["- None."])
    lines += ["", "## Validation result", "", f"**{'FAIL' if ERRORS else 'PASS WITH OPEN LEGAL GATES'}**", ""]
    lines.extend([f"- ERROR: {error}" for error in ERRORS])
    if args.report:
        report = args.report if args.report.is_absolute() else ROOT / args.report
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"License audit report: {report}")
    print(f"License inventory: {'FAIL' if ERRORS else 'PASS WITH OPEN LEGAL GATES'}")
    print(f"SBOM components inspected: {len(components)}")
    for warning in WARNINGS:
        print("WARNING:", warning)
    for error in ERRORS:
        print("ERROR:", error)
    return 1 if ERRORS else 0


if __name__ == "__main__":
    raise SystemExit(main())
