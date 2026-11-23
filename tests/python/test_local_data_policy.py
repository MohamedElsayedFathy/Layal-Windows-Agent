from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tools.local_data_policy import is_local_only_archive_member, is_local_only_path
from tools.package_source import should_exclude_path


class LocalRuntimeDataPolicyTests(unittest.TestCase):
    def test_user_runtime_data_and_dpapi_stores_are_local_only(self) -> None:
        local_paths = (
            "Layal/agent-tasks.dpapi",
            "Layal/chat-sessions.dpapi",
            "Layal/secrets/ai-provider.dpapi",
            "Layal/settings.json",
            "Layal/LocalAi/speech-models/ggml-base.bin",
            "Layal/LocalAi/hf-cache/hub/CACHEDIR.TAG",
            "Layal/LocalAi/voicetut/venv/pyvenv.cfg",
            "Layal/SpeechEvaluation/source/metadata.jsonl",
            "Layal/logs/layal-errors.log",
            "some/other/user-store.dpapi",
            "layal_repo_audit.ps1",
        )
        for value in local_paths:
            with self.subTest(path=value):
                self.assertTrue(is_local_only_path(value))
                self.assertTrue(should_exclude_path(Path(value)))

    def test_archive_guard_recognizes_local_paths_under_versioned_root(self) -> None:
        self.assertTrue(
            is_local_only_archive_member(
                "Layal-Windows-Agent-0.5.8/Layal/LocalAi/speech-models/ggml-base.bin"
            )
        )
        self.assertTrue(
            is_local_only_archive_member(
                "Layal-Windows-Agent-0.5.8/Layal/secrets/ai-provider.dpapi"
            )
        )
        self.assertFalse(
            is_local_only_archive_member(
                "Layal-Windows-Agent-0.5.8/src/Layal.Desktop/App.xaml"
            )
        )

    def test_normal_source_files_remain_packageable(self) -> None:
        self.assertFalse(should_exclude_path(Path("README_AR.md")))
        self.assertFalse(
            should_exclude_path(Path("src/Layal.Desktop/Services/SettingsStore.cs"))
        )
        self.assertFalse(
            should_exclude_path(Path("tests/python/test_local_data_policy.py"))
        )

    def test_gitignore_excludes_local_runtime_data_but_not_source(self) -> None:
        ignored_paths = (
            "Layal/agent-tasks.dpapi",
            "Layal/chat-sessions.dpapi",
            "Layal/secrets/ai-provider.dpapi",
            "Layal/settings.json",
            "Layal/LocalAi/speech-models/ggml-base.bin",
            "Layal/LocalAi/hf-cache/hub/CACHEDIR.TAG",
            "Layal/LocalAi/voicetut/venv/pyvenv.cfg",
            "Layal/SpeechEvaluation/source/metadata.jsonl",
            "Layal/logs/layal-errors.log",
            "layal_repo_audit.ps1",
            "src/Layal.Desktop/bin/Release/Layal.exe",
            "src/Layal.Desktop/obj/project.assets.json",
        )
        source_paths = (
            "README_AR.md",
            "src/Layal.Desktop/Services/SettingsStore.cs",
            "tests/python/test_local_data_policy.py",
            "docs/MASTER_REQUIREMENT_REGISTER_AR.md",
        )
        try:
            ignored = subprocess.run(
                ["git", "check-ignore", "--no-index", "-v", "--stdin"],
                cwd=ROOT,
                input="\n".join((*ignored_paths, *source_paths)) + "\n",
                text=True,
                capture_output=True,
                check=False,
            )
        except FileNotFoundError:
            self.skipTest("Git executable is not available in this environment.")

        self.assertIn(ignored.returncode, (0, 1), ignored.stderr)
        matched = {
            line.rsplit("\t", 1)[-1]
            for line in ignored.stdout.splitlines()
            if "\t" in line
        }
        self.assertTrue(set(ignored_paths).issubset(matched))
        self.assertTrue(set(source_paths).isdisjoint(matched))


if __name__ == "__main__":
    unittest.main()
