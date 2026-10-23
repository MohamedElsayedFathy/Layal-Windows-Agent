import unittest
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import validate_licenses


def valid_review_fixture():
    document = {
        "schemaVersion": 1,
        "projectLicense": {
            "status": "DECLARED",
            "releaseBlocker": False,
            "approvedBy": "rights-holder",
            "approvalDate": "2026-10-23",
            "approvalEvidence": "approval-record-001",
        },
        "assetsRequiringProvenanceReview": [{
            "path": "Assets/mascot.png",
            "status": "REVIEWED",
            "reviewedBy": "asset-reviewer",
            "reviewedOn": "2026-10-23",
            "evidence": "source-and-redistribution-proof-001",
        }],
    }
    packages = {
        "example.package": {
            "id": "Example.Package",
            "reviewStatus": "Approved after human review",
            "reviewedBy": "license-reviewer",
            "reviewedOn": "2026-10-23",
            "reviewEvidence": "upstream-license-review-001",
        }
    }
    components = [{
        "name": "Example.Package",
        "purl": "pkg:nuget/Example.Package@1.0.0",
        "version": "1.0.0",
        "licenses": [{"license": {"id": "MIT"}}],
    }]
    return document, packages, components


class ReleaseClearanceTests(unittest.TestCase):
    def test_fully_evidenced_fixture_passes(self):
        document, packages, components = valid_review_fixture()
        self.assertEqual(
            [],
            validate_licenses.release_clearance_errors(document, packages, components, True),
        )

    def test_undeclared_project_license_blocks_release(self):
        document, packages, components = valid_review_fixture()
        document["projectLicense"]["status"] = "UNDECLARED"
        document["projectLicense"]["releaseBlocker"] = True
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)
        self.assertTrue(any("Project-wide license" in error for error in errors))

    def test_pending_asset_without_evidence_blocks_release(self):
        document, packages, components = valid_review_fixture()
        document["assetsRequiringProvenanceReview"][0]["status"] = "PROVENANCE_REVIEW_REQUIRED"
        document["assetsRequiringProvenanceReview"][0].pop("evidence")
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)
        self.assertTrue(any("Assets/mascot.png" in error for error in errors))

    def test_package_without_review_evidence_blocks_release(self):
        document, packages, components = valid_review_fixture()
        packages["example.package"]["reviewStatus"] = "Human legal review pending."
        packages["example.package"].pop("reviewEvidence")
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)
        self.assertTrue(any("human license review is still pending" in error for error in errors))
        self.assertTrue(any("reviewEvidence" in error for error in errors))

    def test_sbom_component_without_license_blocks_release(self):
        document, packages, components = valid_review_fixture()
        components[0].pop("licenses")
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)
        self.assertTrue(any("SBOM license metadata is missing" in error for error in errors))


if __name__ == "__main__":
    unittest.main(verbosity=2)
