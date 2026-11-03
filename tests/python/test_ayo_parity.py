import importlib.util
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "validate_ayo_parity.py"
SPEC = importlib.util.spec_from_file_location("validate_ayo_parity", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def make_tracker():
    rows = ["# سجل مطابقة وظائف AYO مع مشروع ليال", "", "## سجل الوظائف (33 بندًا)", ""]
    rows.append("| ID | Function | Status | Acceptance |")
    rows.append("|---|---|---|---|")
    for number in range(1, 34):
        row_id = f"AYO-{number:02d}"
        rows.append(f"| {row_id} | Feature {number} | **Partial** — implemented | Verify behavior on Windows and record evidence for this function. |")
    return "\n".join(rows) + "\n"


class AyoParityValidationTests(unittest.TestCase):
    def test_complete_ordered_register_passes(self):
        self.assertEqual([], MODULE.validate_tracker_text(make_tracker()))

    def test_missing_function_is_reported(self):
        tracker = make_tracker().replace(
            "| AYO-17 | Feature 17 | **Partial** — implemented | Verify behavior on Windows and record evidence for this function. |\n",
            "",
        )
        errors = MODULE.validate_tracker_text(tracker)
        self.assertTrue(any("Missing required function row: AYO-17" in error for error in errors))

    def test_duplicate_function_is_reported(self):
        tracker = make_tracker()
        duplicate = "| AYO-12 | Feature duplicate | **Partial** — implemented | Verify behavior on Windows and record evidence for this function. |\n"
        tracker = tracker.replace("| AYO-13 |", duplicate + "| AYO-13 |")
        errors = MODULE.validate_tracker_text(tracker)
        self.assertTrue(any("AYO-12 occurs 2 times" in error for error in errors))

    def test_empty_acceptance_criteria_is_reported(self):
        tracker = make_tracker().replace(
            "| AYO-05 | Feature 5 | **Partial** — implemented | Verify behavior on Windows and record evidence for this function. |",
            "| AYO-05 | Feature 5 | **Partial** — implemented | |",
        )
        errors = MODULE.validate_tracker_text(tracker)
        self.assertTrue(any("AYO-05) has an empty remaining work / acceptance criteria" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
