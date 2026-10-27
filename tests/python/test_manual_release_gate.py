import json
import subprocess
import tempfile
import unittest
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import validate_release_readiness


class ManualAcceptanceReleaseGateTests(unittest.TestCase):
    def valid_manual_evidence(self, tested_sha: str) -> dict:
        return {
            "schemaVersion": 1,
            "testedCommitSha": tested_sha,
            "gates": {
                key: {
                    "status": "APPROVED",
                    "reviewedBy": "manual-reviewer",
                    "reviewedOn": "2026-10-27",
                    "evidence": f"artifacts/manual-acceptance/{key}/summary.json",
                }
                for key in validate_release_readiness.REQUIRED_GATES
            },
        }

    @staticmethod
    def git(repo: Path, *args: str) -> str:
        result = subprocess.run(
            ["git", "-C", str(repo), *args],
            check=True,
            capture_output=True,
            text=True,
        )
        return result.stdout.strip()

    def create_test_repository(self, directory: Path) -> tuple[str, Path]:
        subprocess.run(
            ["git", "init", str(directory)],
            check=True,
            capture_output=True,
            text=True,
        )
        self.git(directory, "config", "user.name", "Release Gate Test")
        self.git(directory, "config", "user.email", "release-gate-test@example.invalid")
        (directory / "src").mkdir()
        (directory / "docs").mkdir()
        (directory / "src" / "app.cs").write_text("initial source\n", encoding="utf-8")
        (directory / "docs" / "RELEASE_GATE_EVIDENCE.json").write_text(
            json.dumps({"schemaVersion": 1, "testedCommitSha": "", "gates": {}}, indent=2) + "\n",
            encoding="utf-8",
        )
        self.git(directory, "add", ".")
        self.git(directory, "commit", "-m", "baseline tested code")
        return self.git(directory, "rev-parse", "HEAD"), directory / "docs" / "RELEASE_GATE_EVIDENCE.json"

    def test_pending_manual_acceptance_blocks_release(self):
        document = self.valid_manual_evidence("a" * 40)
        document["gates"]["windowsDesktopAndSessions"]["status"] = "PENDING"

        errors = validate_release_readiness.validate_manual_acceptance(document)

        self.assertTrue(any("status must be APPROVED" in error for error in errors))

    def test_complete_manual_acceptance_records_pass_structural_validation(self):
        document = self.valid_manual_evidence("a" * 40)

        errors = validate_release_readiness.validate_manual_acceptance(document)

        self.assertEqual([], errors)

    def test_reviewed_code_commit_can_be_followed_by_evidence_only_commit(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            repo = Path(temp_dir)
            tested_sha, manifest_path = self.create_test_repository(repo)
            document = self.valid_manual_evidence(tested_sha)
            manifest_path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
            self.git(repo, "add", "docs/RELEASE_GATE_EVIDENCE.json")
            self.git(repo, "commit", "-m", "record manual acceptance evidence")
            candidate_sha = self.git(repo, "rev-parse", "HEAD")

            errors = validate_release_readiness.validate_manual_acceptance(document, candidate_sha, repo)

            self.assertEqual([], errors)

    def test_source_change_after_manual_acceptance_blocks_release(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            repo = Path(temp_dir)
            tested_sha, manifest_path = self.create_test_repository(repo)
            (repo / "src" / "app.cs").write_text("changed source after QA\n", encoding="utf-8")
            self.git(repo, "add", "src/app.cs")
            self.git(repo, "commit", "-m", "change source after manual QA")
            document = self.valid_manual_evidence(tested_sha)
            manifest_path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
            self.git(repo, "add", "docs/RELEASE_GATE_EVIDENCE.json")
            self.git(repo, "commit", "-m", "attempt to approve stale QA evidence")
            candidate_sha = self.git(repo, "rev-parse", "HEAD")

            errors = validate_release_readiness.validate_manual_acceptance(document, candidate_sha, repo)

            self.assertTrue(any("Source changed after manual acceptance" in error for error in errors))

    def test_non_ancestor_tested_sha_blocks_release(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            repo = Path(temp_dir)
            self.create_test_repository(repo)
            candidate_sha = self.git(repo, "rev-parse", "HEAD")
            document = self.valid_manual_evidence("f" * 40)

            errors = validate_release_readiness.validate_manual_acceptance(document, candidate_sha, repo)

            self.assertTrue(any("must be an ancestor" in error for error in errors))


if __name__ == "__main__":
    unittest.main(verbosity=2)
