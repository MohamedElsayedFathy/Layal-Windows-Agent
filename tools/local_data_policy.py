"""Shared rules for keeping user-specific runtime state out of source packages."""
from __future__ import annotations

from pathlib import PurePosixPath

LOCAL_ONLY_ROOT_DIRS = frozenset({"Layal"})
LOCAL_ONLY_ROOT_FILES = frozenset({"layal_repo_audit.ps1"})


def _parts(path: str | PurePosixPath) -> tuple[str, ...]:
    normalized = str(path).replace("\\", "/")
    return tuple(part for part in normalized.split("/") if part not in {"", "."})


def is_local_only_path(path: str | PurePosixPath) -> bool:
    """Return True for repository-local runtime state and one-off audit output."""
    parts = _parts(path)
    if not parts:
        return False
    if parts[0] in LOCAL_ONLY_ROOT_DIRS:
        return True
    if len(parts) == 1 and parts[0] in LOCAL_ONLY_ROOT_FILES:
        return True
    return parts[-1].lower().endswith(".dpapi")


def is_local_only_archive_member(member_name: str) -> bool:
    """Apply the same policy to ZIP members under the versioned source root."""
    parts = _parts(member_name)
    if parts and parts[0].startswith("Layal-Windows-Agent-"):
        parts = parts[1:]
    if not parts:
        return False
    return is_local_only_path(PurePosixPath(*parts))
