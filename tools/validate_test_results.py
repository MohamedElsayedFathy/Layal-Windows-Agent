#!/usr/bin/env python3
"""Validate TRX files: ensure tests actually ran and no failures are reported."""
from __future__ import annotations
import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results_dir", type=Path, help="Directory containing TRX test reports")
    args = parser.parse_args()
    if not args.results_dir.is_dir():
        print(f"ERROR: test results directory does not exist: {args.results_dir}")
        return 2
    files = sorted(args.results_dir.rglob("*.trx"))
    if not files:
        print(f"ERROR: no .trx report found in {args.results_dir}")
        return 2

    reports = 0
    total_tests = 0
    failures = 0
    errors: list[str] = []
    for path in files:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as exc:
            errors.append(f"Invalid TRX XML in {path}: {exc}")
            continue
        counters = next((node for node in root.iter() if local_name(node.tag) == "Counters"), None)
        if counters is None:
            errors.append(f"No Counters element found in {path}")
            continue
        try:
            total = int(counters.attrib.get("total", "0"))
            failed = sum(int(counters.attrib.get(key, "0")) for key in ("failed", "error", "timeout", "aborted", "notRunnable"))
        except ValueError:
            errors.append(f"Malformed test counters in {path}")
            continue
        reports += 1
        total_tests += total
        failures += failed
        print(f"{path.name}: total={total}, failed/error/timeout/aborted/notRunnable={failed}")

    if errors:
        for error in errors:
            print("ERROR:", error)
    if reports == 0:
        print("ERROR: no readable TRX report with counters was found")
        return 2
    if total_tests == 0:
        print("ERROR: TRX reports exist, but zero tests were discovered or executed")
        return 1
    if failures:
        print(f"ERROR: {failures} failed/error/timeout/aborted/notRunnable test outcomes")
        return 1
    if errors:
        return 1
    print(f"PASS: {reports} TRX report(s), {total_tests} test(s), no failing outcomes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
