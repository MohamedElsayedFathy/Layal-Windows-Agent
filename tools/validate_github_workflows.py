#!/usr/bin/env python3
"""Validate GitHub Actions workflow security invariants for Layal."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORKFLOW_DIR = ROOT / ".github" / "workflows"
EXPECTED_WORKFLOWS = {
    "phase-c-qwen-lifecycle.yml",
    "security-supply-chain.yml",
    "windows-build.yml",
    "windows-final-acceptance.yml",
    "codeql.yml",
}
ACTION_REF = re.compile(r"^([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)@([0-9a-fA-F]{40})$")


def _step_block(lines: list[str], start: int) -> str:
    """Return the current YAML step from its action line to the next step."""
    end = start + 1
    while end < len(lines) and not re.match(r"^      - ", lines[end]):
        end += 1
    return "\n".join(lines[start:end])


def validate_workflow(path: str, content: str) -> list[str]:
    errors: list[str] = []
    name = Path(path).name
    lines = content.splitlines()

    permission_match = re.search(
        r"(?ms)^permissions:\s*\n((?:^  [^\n]*\n)+)", content
    )
    if not permission_match:
        errors.append(f"{name}: missing explicit least-privilege workflow permissions.")
    else:
        global_permissions = permission_match.group(1)
        if not re.search(r"(?m)^  contents:\s*read\s*$", global_permissions):
            errors.append(f"{name}: workflow-level contents: read permission is required.")
    for match in re.finditer(r"(?m)^\s*([a-z-]+):\s*(write|write-all)\s*$", content):
        permission, _ = match.groups()
        if not (name == "codeql.yml" and permission == "security-events"):
            errors.append(
                f"{name}: unexpected write permission '{permission}'; keep CI tokens read-only."
            )

    if not re.search(r"(?m)^concurrency:\s*$", content):
        errors.append(f"{name}: missing workflow concurrency group.")
    else:
        block = re.search(r"(?ms)^concurrency:\s*\n(.*?)(?=^[^ \t#]|\Z)", content)
        concurrency = block.group(1) if block else ""
        if "github.workflow" not in concurrency or "github.ref" not in concurrency:
            errors.append(f"{name}: concurrency group must be scoped to workflow and ref/PR.")
        if "cancel-in-progress:" not in concurrency:
            errors.append(f"{name}: concurrency must declare cancel-in-progress.")

    if re.search(r"(?m)^\s{2}(?:pull_request_target|workflow_run):", content):
        errors.append(f"{name}: privileged pull_request_target/workflow_run trigger is not allowed.")

    for line in lines:
        if re.match(r"^\s+uses:\s*", line):
            reference = line.split("uses:", 1)[1].strip().split()[0]
            if reference.startswith("./"):
                continue
            if not ACTION_REF.fullmatch(reference):
                errors.append(
                    f"{name}: action '{reference}' is not pinned to a full 40-character commit SHA."
                )

    for index, line in enumerate(lines):
        if re.match(r"^\s+uses:\s*actions/checkout@", line):
            block = _step_block(lines, index)
            if not re.search(r"(?m)^\s+persist-credentials:\s*false\s*$", block):
                errors.append(
                    f"{name}: every actions/checkout step must set persist-credentials: false."
                )

        if re.match(r"^\s+uses:\s*actions/upload-artifact@", line):
            block = _step_block(lines, index)
            retention = re.search(r"(?m)^\s+retention-days:\s*(\d+)\s*$", block)
            if not retention or not (1 <= int(retention.group(1)) <= 30):
                errors.append(
                    f"{name}: every uploaded artifact must have an explicit 1-30 day retention."
                )

    secret_names = set(re.findall(r"\$\{\{\s*secrets\.([A-Z0-9_]+)\s*\}\}", content))
    if name == "security-supply-chain.yml":
        if secret_names != {"GITHUB_TOKEN"}:
            errors.append(f"{name}: only the read-only GITHUB_TOKEN may be referenced.")
        if 'GITLEAKS_ENABLE_COMMENTS: "false"' not in content:
            errors.append(f"{name}: Gitleaks PR comments must remain disabled.")
        if not re.search(r"(?m)^\s+pull-requests:\s*read\s*$", content):
            errors.append(f"{name}: Gitleaks may only read pull-request metadata.")
    elif name == "windows-build.yml":
        expected = {
            "LAYAL_SIGNING_CERTIFICATE_BASE64",
            "LAYAL_SIGNING_CERTIFICATE_PASSWORD",
            "LAYAL_SIGNING_CERTIFICATE_THUMBPRINT",
        }
        if secret_names != expected:
            errors.append(
                f"{name}: signing secrets must be the only referenced non-default secrets."
            )
        step_start = next(
            (i for i, item in enumerate(lines)
             if item.strip() == "- name: Build signed and verified release distribution"),
            -1,
        )
        if step_start < 0:
            errors.append(f"{name}: tagged signing step is missing.")
        else:
            block = _step_block(lines, step_start)
            guard = (
                "startsWith(github.ref, 'refs/tags/v')" in block
                and "steps.release_guard.outputs.approved == 'true'" in block
            )
            step_secrets = set(
                re.findall(r"\$\{\{\s*secrets\.([A-Z0-9_]+)\s*\}\}", block)
            )
            if not guard or step_secrets != expected:
                errors.append(
                    f"{name}: signing secrets must be confined to the approved version-tag step."
                )
        guard_start = next(
            (i for i, item in enumerate(lines)
             if item.strip() == "- name: Validate release tag belongs to main history"),
            -1,
        )
        if guard_start < 0 or "git merge-base --is-ancestor" not in _step_block(lines, guard_start):
            errors.append(f"{name}: signing must require a tag reachable from main.")
    else:
        if secret_names:
            errors.append(f"{name}: workflow must not reference repository secrets.")

    if name == "codeql.yml":
        if re.search(r"(?m)^  pull_request:", content):
            errors.append(
                "codeql.yml: CodeQL upload token is write-scoped; scan only trusted main pushes/schedules."
            )
        for required in (
            "github/codeql-action/init@",
            "github/codeql-action/analyze@",
            "security-events: write",
            "languages: csharp",
            "build-mode: autobuild",
        ):
            if required not in content:
                errors.append(f"codeql.yml: missing required CodeQL configuration '{required}'.")
    elif "security-events: write" in content:
        errors.append(f"{name}: security-events: write is reserved for the trusted CodeQL workflow.")

    return errors


def validate_repository(root: Path = ROOT) -> list[str]:
    workflow_dir = root / ".github" / "workflows"
    paths = sorted(workflow_dir.glob("*.yml"))
    actual = {path.name for path in paths}
    errors: list[str] = []
    missing = sorted(EXPECTED_WORKFLOWS - actual)
    unexpected = sorted(actual - EXPECTED_WORKFLOWS)
    if missing:
        errors.append(f"Missing expected workflow files: {', '.join(missing)}")
    if unexpected:
        errors.append(f"Unreviewed workflow files were added: {', '.join(unexpected)}")
    for path in paths:
        try:
            content = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as exc:
            errors.append(f"{path.name}: could not read workflow: {exc}")
            continue
        errors.extend(validate_workflow(path.name, content))
    return errors


def main() -> int:
    errors = validate_repository()
    print("GitHub Actions security validation")
    print(f"Workflow files checked: {len(list(WORKFLOW_DIR.glob('*.yml')))}")
    for error in errors:
        print("ERROR:", error)
    print("Result: " + ("FAIL" if errors else "PASS"))
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
