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
            "sourceDescription": "Created specifically for this test fixture.",
            "licenseExpression": "CC0-1.0",
            "redistributionPermission": "Rights-holder approval evidence fixture.",
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

    def test_evidenced_provenance_status_is_supported_but_does_not_clear_rights(self):
        status = "PROVENANCE_EVIDENCED_RIGHTS_REVIEW_REQUIRED"
        self.assertIn(status, validate_licenses.ASSET_PROVENANCE_STATUSES)

        document, packages, components = valid_review_fixture()
        document["assetsRequiringProvenanceReview"][0]["status"] = status
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)

        self.assertTrue(
            any("source, license, and redistribution rights are not approved" in error for error in errors),
            "Evidence of a source must not satisfy the separate legal rights/release gate.",
        )

    def test_package_without_review_evidence_blocks_release(self):
        document, packages, components = valid_review_fixture()
        packages["example.package"]["reviewStatus"] = "Human legal review pending."
        packages["example.package"].pop("reviewEvidence")
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)
        self.assertTrue(any("human license review is not explicitly approved" in error for error in errors))
        self.assertTrue(any("reviewEvidence" in error for error in errors))

    def test_asset_without_explicit_redistribution_permission_blocks_release(self):
        document, packages, components = valid_review_fixture()
        document["assetsRequiringProvenanceReview"][0].pop("redistributionPermission")
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)
        self.assertTrue(any("explicit redistribution permission is missing" in error for error in errors))

    def test_sbom_component_without_license_blocks_release(self):
        document, packages, components = valid_review_fixture()
        components[0].pop("licenses")
        errors = validate_licenses.release_clearance_errors(document, packages, components, True)
        self.assertTrue(any("SBOM license metadata is missing" in error for error in errors))

    def test_missing_sbom_license_is_enriched_from_inventory_with_source(self):
        document, packages, components = valid_review_fixture()
        component = components[0]
        component.pop("licenses")
        packages["example.package"]["licenseExpression"] = "MIT"
        packages["example.package"]["sourceUrl"] = "https://packages.example.invalid/Example.Package"
        packages["example.package"]["licenseUrl"] = "https://licenses.example.invalid/MIT"

        enriched = validate_licenses.enrich_missing_license_metadata(components, packages)

        self.assertEqual(["Example.Package (MIT)"], enriched)
        self.assertEqual(["MIT"], validate_licenses.component_licenses(component))
        properties = {item["name"]: item["value"] for item in component["properties"]}
        self.assertEqual(
            "https://packages.example.invalid/Example.Package",
            properties["layal:license-metadata-source"],
        )
        self.assertIn("human legal review still required", properties["layal:license-metadata-status"])

    def test_existing_sbom_license_is_not_overwritten(self):
        document, packages, components = valid_review_fixture()
        packages["example.package"]["licenseExpression"] = "MIT"
        components[0]["licenses"] = [{"expression": "Apache-2.0"}]
        before = components[0]["licenses"].copy()
        enriched = validate_licenses.enrich_missing_license_metadata(components, packages)
        self.assertEqual([], enriched)
        self.assertEqual(before, components[0]["licenses"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
