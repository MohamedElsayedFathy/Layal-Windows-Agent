#!/usr/bin/env python3
"""Check a Layal source ZIP before delivery. Does not replace Windows build or security review."""
from __future__ import annotations
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
errors: list[str] = []
required = [
    "Layal.sln",
    "README_AR.md",
    "README.md",
    "docs/MASTER_REQUIREMENT_REGISTER_AR.md",
    "tools/validate_project.py",
    "tools/VERIFY-LAYAL.ps1",
    "tools/package_source.py",
    ".github/workflows/windows-build.yml",
    "src/Layal.Desktop/Layal.Desktop.csproj",
    "src/Layal.Desktop/App.xaml",
    "src/Layal.Desktop/Views/MainWindow.xaml",
    "src/Layal.Desktop/Views/FloatingMascotWindow.xaml",
    "tests/Layal.Tests/Layal.Tests.csproj",
    "tests/Layal.Tests/SettingsStoreTests.cs",
    "tests/Layal.Tests/SkillPackageServiceTests.cs",
    "tests/Layal.Tests/MemoryServiceTests.cs",
    "tests/Layal.Tests/AgentToolExecutorTests.cs",
    "tests/Layal.Tests/OpenAiCompatibleClientTests.cs",
    "docs/AUTOMATED_TESTS_AR.md",
]
for rel in required:
    if not (ROOT / rel).is_file():
        errors.append(f"Required release file missing: {rel}")

project_text = (ROOT / "src/Layal.Desktop/Layal.Desktop.csproj").read_text(encoding="utf-8")
match = re.search(r"<Version>([^<]+)</Version>", project_text)
version = match.group(1) if match else "unknown"

sensitive_patterns = [
    (re.compile(r"(?i)\bsk-(?:proj|ant)-[A-Za-z0-9_-]{16,}"), "possible provider API key"),
    (re.compile(r"\bgh[pousr]_[A-Za-z0-9]{25,}\b"), "possible GitHub token"),
    (re.compile(r"\bgithub_pat_[A-Za-z0-9_]{20,}\b"), "possible GitHub fine-grained token"),
    (re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----\s+[A-Za-z0-9+/=\r\n]{100,}-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"), "private key material block"),
]
scan_suffixes = {".cs", ".csproj", ".sln", ".props", ".targets", ".xaml", ".json", ".toml", ".yml", ".yaml", ".md", ".ps1", ".bat", ".config"}
for path in ROOT.rglob("*"):
    if not path.is_file() or any(part in {"bin", "obj", ".git", ".vs"} for part in path.parts):
        continue
    if path.suffix.lower() not in scan_suffixes:
        continue
    try:
        data = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        continue
    for pattern, label in sensitive_patterns:
        if pattern.search(data):
            errors.append(f"{label} pattern found in {path.relative_to(ROOT)}")

if len(sys.argv) > 1:
    archive_path = Path(sys.argv[1]).resolve()
    if not archive_path.is_file():
        errors.append(f"Archive not found: {archive_path}")
    else:
        try:
            with zipfile.ZipFile(archive_path) as zf:
                bad = zf.testzip()
                if bad:
                    errors.append(f"Corrupt ZIP member: {bad}")
                names = set(zf.namelist())
                for rel in required:
                    if not any(n == rel or n.endswith("/" + rel) for n in names):
                        errors.append(f"Archive is missing required file: {rel}")
                for name in names:
                    normalized = name.replace("\\", "/")
                    parts = normalized.split("/")
                    if "bin" in parts or "obj" in parts or ".git" in parts or ".vs" in parts or "__pycache__" in parts:
                        errors.append(f"Build/cache output must not be included: {name}")
                    if "artifacts" in parts:
                        errors.append(f"Generated artifacts/logs must not be included in source ZIP: {name}")
        except zipfile.BadZipFile as exc:
            errors.append(f"Invalid ZIP archive: {exc}")

print(f"Layal source/release validation | version {version}")
print(f"Required files checked: {len(required)}")
print("Working tree checks: " + ("PASS" if not errors else "FAIL"))
for err in errors:
    print("ERROR:", err)
if len(sys.argv) > 1 and Path(sys.argv[1]).is_file():
    print("Archive checks: " + ("PASS" if not errors else "FAIL"))
else:
    print("Archive checks: SKIPPED (provide a ZIP path to check packaged contents)")
print("LIMITATION: Pattern scanning is not a complete secrets scanner and cannot prove runtime correctness.")
raise SystemExit(1 if errors else 0)
