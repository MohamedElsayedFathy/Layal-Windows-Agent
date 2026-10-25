#!/usr/bin/env python3
"""Run the .NET 10 NuGet vulnerability query and fail on reported advisories."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_json_report(stdout: str) -> dict[str, Any]:
    """Parse JSON even if the SDK prints a status line before the payload."""
    candidates = [stdout.strip()]
    start, end = stdout.find("{"), stdout.rfind("}")
    if start >= 0 and end > start:
        candidates.append(stdout[start : end + 1])
    for candidate in candidates:
        try:
            payload = json.loads(candidate)
        except json.JSONDecodeError:
            continue
        if isinstance(payload, dict) and isinstance(payload.get("projects"), list):
            return payload
    raise ValueError("NuGet did not return the expected JSON package-list report with a projects array.")


def detect_vulnerabilities(node: Any, results: list[dict[str, str]]) -> None:
    """Walk the output schema recursively so project/framework nesting does not hide advisories."""
    if isinstance(node, dict):
        if "vulnerabilities" in node:
            advisories = node.get("vulnerabilities")
            if isinstance(advisories, dict):
                advisories = list(advisories.values())
            elif not isinstance(advisories, list):
                advisories = [advisories] if advisories else []
            for advisory in advisories:
                if not advisory:
                    continue
                package_id = str(
                    node.get("id") or node.get("name") or node.get("packageId")
                    or node.get("package") or "<package-id-not-reported>"
                )
                version = str(
                    node.get("resolvedVersion") or node.get("version")
                    or node.get("resolved") or "<version-not-reported>"
                )
                if isinstance(advisory, dict):
                    severity = str(advisory.get("severity") or advisory.get("severityName") or "unknown")
                    url = str(advisory.get("advisoryurl") or advisory.get("url") or advisory.get("advisoryUrl") or "")
                    advisory_id = str(advisory.get("advisoryId") or advisory.get("id") or "")
                else:
                    severity, url, advisory_id = "unknown", str(advisory), ""
                item = {
                    "packageId": package_id,
                    "resolvedVersion": version,
                    "severity": severity,
                    "advisoryId": advisory_id,
                    "advisoryUrl": url,
                }
                if item not in results:
                    results.append(item)
        for value in node.values():
            detect_vulnerabilities(value, results)
    elif isinstance(node, list):
        for value in node:
            detect_vulnerabilities(value, results)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--solution", type=Path, default=Path("Layal.sln"))
    parser.add_argument("--output", type=Path, default=Path("artifacts/security/nuget-vulnerability-audit.json"))
    args = parser.parse_args()

    solution = args.solution.resolve()
    if not solution.is_file():
        print(f"ERROR: solution not found: {solution}", file=sys.stderr)
        return 2

    command = [
        "dotnet", "package", "list", "--project", str(solution),
        "--vulnerable", "--include-transitive", "--format", "json", "--no-restore",
    ]
    process = subprocess.run(command, capture_output=True, text=True, check=False)
    report: dict[str, Any] | None = None
    vulnerabilities: list[dict[str, str]] = []
    parse_error: str | None = None
    if process.returncode == 0:
        try:
            report = parse_json_report(process.stdout)
            detect_vulnerabilities(report, vulnerabilities)
        except ValueError as exc:
            parse_error = str(exc)
    else:
        parse_error = (
            f"dotnet package list failed with exit code {process.returncode}; "
            f"stderr: {process.stderr.strip() or '<empty>'}"
        )

    result = {
        "schemaVersion": 1,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "solution": str(solution),
        "command": command,
        "status": "FAIL" if parse_error or vulnerabilities else "PASS",
        "vulnerabilityCount": len(vulnerabilities),
        "vulnerabilities": vulnerabilities,
        "error": parse_error,
        "nugetReport": report,
        "stdout": process.stdout if report is None else None,
        "stderr": process.stderr or None,
    }
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"NuGet vulnerability audit: {result['status']}")
    print(f"Packages/advisories reported: {len(vulnerabilities)}")
    print(f"Evidence: {output}")
    if parse_error:
        print(f"ERROR: {parse_error}", file=sys.stderr)
        return 2
    if vulnerabilities:
        for item in vulnerabilities:
            print(
                "VULNERABLE: "
                f"{item['packageId']} {item['resolvedVersion']} | "
                f"{item['severity']} | {item['advisoryUrl'] or item['advisoryId']}"
            )
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
