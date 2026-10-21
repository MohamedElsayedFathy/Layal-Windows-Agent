#!/usr/bin/env python3
"""Create a clean Layal source ZIP and validate the exact archive before reporting success."""
from __future__ import annotations
import argparse
import re
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXCLUDED_DIRS = {".git", ".vs", "bin", "obj", "__pycache__", "artifacts"}
EXCLUDED_NAMES = {".env", ".env.local", ".env.production", "secrets.json", "appsettings.Local.json", ".DS_Store"}
EXCLUDED_SUFFIXES = {".pyc", ".pyo", ".pfx", ".p12", ".pem", ".key", ".user", ".suo", ".tmp"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="Output ZIP path (defaults to a versioned ZIP next to the project folder).")
    args = parser.parse_args()

    project_text = (ROOT / "src/Layal.Desktop/Layal.Desktop.csproj").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", project_text)
    if not match:
        print("ERROR: Could not read project version from the csproj.", file=sys.stderr)
        return 2
    version = match.group(1).strip()
    output = args.output.resolve() if args.output else ROOT.parent / f"Layal-Windows-Agent-{version}-source.zip"
    if output == ROOT or ROOT in output.parents:
        # Output inside the source tree is allowed only under artifacts, which is excluded from packaging.
        if "artifacts" not in output.relative_to(ROOT).parts:
            print("ERROR: Output ZIP must be outside the source tree or inside its ignored artifacts folder.", file=sys.stderr)
            return 2
    output.parent.mkdir(parents=True, exist_ok=True)
    prefix = f"Layal-Windows-Agent-{version}/"

    if output.exists():
        output.unlink()
    written = 0
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=7) as archive:
        for path in sorted(ROOT.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(ROOT)
            if any(part in EXCLUDED_DIRS for part in relative.parts):
                continue
            if path.name in EXCLUDED_NAMES or path.suffix.lower() in EXCLUDED_SUFFIXES:
                continue
            if path.name.startswith(".env."):
                continue
            archive.write(path, prefix + relative.as_posix())
            written += 1

    if written == 0:
        output.unlink(missing_ok=True)
        print("ERROR: No source files were packaged.", file=sys.stderr)
        return 2

    validator = ROOT / "tools/validate_release.py"
    result = subprocess.run([sys.executable, str(validator), str(output)], check=False)
    if result.returncode != 0:
        output.unlink(missing_ok=True)
        print("ERROR: Release validation failed; invalid ZIP was removed.", file=sys.stderr)
        return result.returncode

    print(f"Package created: {output}")
    print(f"Project version: {version}")
    print(f"Files packaged: {written}")
    print(f"Archive size: {output.stat().st_size:,} bytes")
    print("Note: this validates source packaging, not Windows compilation or runtime correctness.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
