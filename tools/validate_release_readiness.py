#!/usr/bin/env python3
"""Fail-closed human acceptance evidence gate for signed Layal releases."""
from __future__ import annotations

import argparse
from datetime import date
import json
from pathlib import Path
import re
import subprocess
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = Path("docs/RELEASE_GATE_EVIDENCE.json")
SHA1_RE = re.compile(r"^[0-9a-fA-F]{40}$")
REQUIRED_GATES = {
    "windowsDesktopAndSessions": "Manual Windows desktop and user-session acceptance",
    "screenReaderAndHighContrast": "Narrator/NVDA and Windows High Contrast acceptance",
    "rtlLtrDisplayScaling": "Arabic/English RTL-LTR and display scaling acceptance",
    "multiMonitorDpi": "Two-monitor mixed-DPI transition acceptance",
}


def _run_git(repo_root: Path, *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", "-C", str(repo_root), *args],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )


def validate_manual_acceptance(
    document: Any,
    candidate_commit_sha: str | None = None,
    repo_root: Path = ROOT,
) -> list[str]:
    """Return release-blocking problems. Git lineage is checked when a candidate SHA is supplied."""
    errors: list[str] = []
    if not isinstance(document, dict) or document.get("schemaVersion") != 1:
        return ["Manual acceptance evidence must be a JSON object with schemaVersion 1."]

    tested_sha = str(document.get("testedCommitSha") or "").strip()
    if not SHA1_RE.fullmatch(tested_sha):
        errors.append("testedCommitSha must be the full 40-character SHA of the code commit manually accepted.")

    gates = document.get("gates")
    if not isinstance(gates, dict):
        errors.append("Manual acceptance evidence must contain a gates object.")
        gates = {}

    for key, label in REQUIRED_GATES.items():
        gate = gates.get(key)
        if not isinstance(gate, dict):
            errors.append(f"{label}: evidence record is missing.")
            continue

        if gate.get("status") != "APPROVED":
            errors.append(f"{label}: status must be APPROVED; current status is {gate.get('status') or 'missing'}.")
        reviewer = str(gate.get("reviewedBy") or "").strip()
        if not reviewer:
            errors.append(f"{label}: reviewedBy is required.")
        reviewed_on = str(gate.get("reviewedOn") or "").strip()
        if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", reviewed_on):
            errors.append(f"{label}: reviewedOn must be an ISO date (YYYY-MM-DD).")
        else:
            try:
                date.fromisoformat(reviewed_on)
            except ValueError:
                errors.append(f"{label}: reviewedOn must be a valid calendar date (YYYY-MM-DD).")
        evidence = str(gate.get("evidence") or "").strip()
        if not evidence:
            errors.append(f"{label}: evidence must reference real test records, screenshots, or logs.")

    if candidate_commit_sha is not None:
        candidate_sha = candidate_commit_sha.strip()
        if not SHA1_RE.fullmatch(candidate_sha):
            errors.append("The release candidate commit must be supplied as a full 40-character Git SHA.")
        elif SHA1_RE.fullmatch(tested_sha):
            if tested_sha.lower() == candidate_sha.lower():
                errors.append(
                    "testedCommitSha must name the prior code commit: the approval commit may change only "
                    "docs/RELEASE_GATE_EVIDENCE.json after manual acceptance."
                )
            else:
                ancestor = _run_git(repo_root, "merge-base", "--is-ancestor", tested_sha, candidate_sha)
                if ancestor.returncode != 0:
                    errors.append("testedCommitSha must be an ancestor of the release candidate commit.")
                else:
                    diff = _run_git(
                        repo_root, "diff", "--name-only", "--no-renames", tested_sha, candidate_sha, "--"
                    )
                    if diff.returncode != 0:
                        errors.append("Could not compare the manually tested commit with the release candidate.")
                    else:
                        changed = sorted(line.strip().replace("\\", "/") for line in diff.stdout.splitlines() if line.strip())
                        if changed != [MANIFEST.as_posix()]:
                            errors.append(
                                "Source changed after manual acceptance. The release candidate may differ only "
                                "by docs/RELEASE_GATE_EVIDENCE.json; rerun manual acceptance on the new code commit. "
                                f"Changed paths: {', '.join(changed) or '(none)'}."
                            )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--require-manual-acceptance", action="store_true",
                        help="Fail unless all required manual acceptance gates have evidence.")
    parser.add_argument("--commit-sha", help="Full Git SHA of the release candidate; required with --require-manual-acceptance.")
    parser.add_argument("--manifest", type=Path, default=MANIFEST,
                        help="Evidence JSON path (default: docs/RELEASE_GATE_EVIDENCE.json).")
    args = parser.parse_args()
    if not args.require_manual_acceptance:
        parser.error("provide --require-manual-acceptance")
    if not args.commit_sha:
        parser.error("--commit-sha is required with --require-manual-acceptance")

    manifest_path = args.manifest if args.manifest.is_absolute() else ROOT / args.manifest
    try:
        document = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"Release acceptance gate: FAIL — cannot read {manifest_path}: {exc}")
        return 1

    errors = validate_manual_acceptance(document, args.commit_sha, ROOT)
    if errors:
        print("Release acceptance gate: FAIL")
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    print("Release acceptance gate: PASS — all required human acceptance records are present.")
    print("NOTE: this validates evidence records and commit lineage; it does not independently observe the physical test.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
