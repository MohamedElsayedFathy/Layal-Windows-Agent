#!/usr/bin/env python3
"""Fail CI when the AYO feature-parity register loses or duplicates a tracked function."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

EXPECTED_IDS = tuple(f"AYO-{number:02d}" for number in range(1, 34))
ROW_START = re.compile(r"^\|\s*(AYO-\d{2})\s*(?:\||$)")
PLACEHOLDERS = ("TODO", "TBD", "TO BE DEFINED", "UNKNOWN")


def validate_tracker_text(text: str) -> list[str]:
    errors: list[str] = []
    if "## سجل الوظائف (33 بندًا)" not in text:
        errors.append("The tracker must explicitly declare the 33-function register.")

    rows: list[tuple[int, str, list[str]]] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        match = ROW_START.match(line)
        if not match:
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        rows.append((line_number, match.group(1), cells))

    found_ids = [row_id for _, row_id, _ in rows]
    for expected in EXPECTED_IDS:
        occurrences = found_ids.count(expected)
        if occurrences == 0:
            errors.append(f"Missing required function row: {expected}.")
        elif occurrences > 1:
            errors.append(f"Function row {expected} occurs {occurrences} times; each ID must occur once.")

    unexpected = sorted(set(found_ids) - set(EXPECTED_IDS))
    for row_id in unexpected:
        errors.append(f"Unexpected function ID: {row_id}.")

    if len(rows) != len(EXPECTED_IDS):
        errors.append(
            f"Expected exactly {len(EXPECTED_IDS)} function rows, found {len(rows)}."
        )

    sequence = [row_id for _, row_id, _ in rows]
    if sequence and sequence != list(EXPECTED_IDS):
        errors.append("Function rows must remain ordered from AYO-01 through AYO-33.")

    for line_number, row_id, cells in rows:
        if len(cells) != 4:
            errors.append(
                f"Line {line_number} ({row_id}) must contain exactly four data columns: ID, function, status, acceptance."
            )
            continue

        _, function_name, status, acceptance = cells
        for label, value in (
            ("function name", function_name),
            ("status", status),
            ("remaining work / acceptance criteria", acceptance),
        ):
            if not value:
                errors.append(f"Line {line_number} ({row_id}) has an empty {label}.")

            upper = value.upper()
            if any(marker in upper for marker in PLACEHOLDERS):
                errors.append(f"Line {line_number} ({row_id}) contains a placeholder in {label}.")

        if "**" not in status:
            errors.append(f"Line {line_number} ({row_id}) must emphasize its current status.")

        if len(acceptance) < 20:
            errors.append(
                f"Line {line_number} ({row_id}) needs a concrete remaining-work or acceptance note."
            )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "tracker",
        nargs="?",
        default="docs/AYO_FEATURE_PARITY_AR.md",
        help="Path to the AYO feature parity register.",
    )
    args = parser.parse_args()
    path = Path(args.tracker)

    if not path.is_file():
        print(f"AYO parity validation failed: tracker not found: {path}", file=sys.stderr)
        return 1

    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        print(f"AYO parity validation failed: cannot read {path}: {exc}", file=sys.stderr)
        return 1

    errors = validate_tracker_text(text)
    if errors:
        print("AYO parity validation failed:")
        for error in errors:
            print(f"- {error}")
        return 1

    print(f"AYO parity validation passed: all {len(EXPECTED_IDS)} function IDs are present once, ordered, and have status/acceptance notes.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
