from __future__ import annotations

import unittest

from tools.validate_github_workflows import ROOT, validate_repository, validate_workflow


class GitHubWorkflowSecurityTests(unittest.TestCase):
    def test_all_repository_workflows_follow_security_policy(self) -> None:
        self.assertEqual(validate_repository(ROOT), [])

    def test_checkout_must_not_persist_credentials(self) -> None:
        sha = "a" * 40
        workflow = f"""name: Sample
concurrency:
  group: ${{{{ github.workflow }}}}-${{{{ github.ref }}}}
  cancel-in-progress: true
permissions:
  contents: read
jobs:
  build:
    steps:
      - name: Checkout
        uses: actions/checkout@{sha}
        with:
          persist-credentials: true
"""
        errors = validate_workflow("sample.yml", workflow)
        self.assertTrue(any("persist-credentials: false" in item for item in errors))

    def test_external_actions_must_use_full_commit_shas(self) -> None:
        workflow = """name: Sample
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
permissions:
  contents: read
jobs:
  build:
    steps:
      - name: Checkout
        uses: actions/checkout@v7
        with:
          persist-credentials: false
"""
        errors = validate_workflow("sample.yml", workflow)
        self.assertTrue(any("full 40-character commit SHA" in item for item in errors))

    def test_privileged_pull_request_target_trigger_is_rejected(self) -> None:
        workflow = """name: Sample
on:
  pull_request_target:
permissions:
  contents: read
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
"""
        errors = validate_workflow("sample.yml", workflow)
        self.assertTrue(any("pull_request_target" in item for item in errors))

    def test_codeql_write_token_is_limited_to_main(self) -> None:
        workflow_path = ROOT / ".github" / "workflows" / "codeql.yml"
        original = workflow_path.read_text(encoding="utf-8")
        tampered = original.replace(
            "    if: github.ref == 'refs/heads/main'\n",
            "",
            1,
        )
        errors = validate_workflow("codeql.yml", tampered)
        self.assertTrue(any("missing required CodeQL configuration" in item for item in errors))

    def test_signing_secrets_must_remain_in_approved_tag_step(self) -> None:
        workflow_path = ROOT / ".github" / "workflows" / "windows-build.yml"
        original = workflow_path.read_text(encoding="utf-8")
        marker = "      - name: Build signed and verified release distribution"
        prefix, found, signing_step = original.partition(marker)
        self.assertTrue(found, "signing step must exist")
        tampered = prefix + found + signing_step.replace(
            "if: success() && startsWith(github.ref, 'refs/tags/v') && steps.release_guard.outputs.approved == 'true'",
            "if: always()",
            1,
        )
        errors = validate_workflow("windows-build.yml", tampered)
        self.assertTrue(any("signing secrets must be confined" in item for item in errors))


if __name__ == "__main__":
    unittest.main()
