import csv
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "prepare_speech_collection.py"
SPEC = importlib.util.spec_from_file_location("prepare_speech_collection", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class SpeechCollectionPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.seed = self.root / "seed.json"
        self.repo = self.root / "repository"
        self.repo.mkdir()
        self.write_seed()

    def tearDown(self):
        self.temporary.cleanup()

    def seed_payload(self):
        return {
            "schemaVersion": 1,
            "description": "Transcript-only test fixture.",
            "recordingStatus": "not-recorded",
            "cases": [
                {
                    "id": "eg-001",
                    "locale": "ar-EG",
                    "referenceText": "إزيك؟ عامل إيه النهارده؟",
                    "audioAsset": None,
                    "recordingStatus": "not-recorded",
                    "speakerId": None,
                    "consentRequired": True,
                    "hypotheses": {},
                },
                {
                    "id": "en-001",
                    "locale": "en",
                    "referenceText": "Please open the project folder.",
                    "audioAsset": None,
                    "recordingStatus": "not-recorded",
                    "speakerId": None,
                    "consentRequired": True,
                    "hypotheses": {},
                },
            ],
        }

    def write_seed(self, payload=None):
        self.seed.write_text(
            json.dumps(payload if payload is not None else self.seed_payload(), ensure_ascii=False),
            encoding="utf-8",
        )

    def test_prepares_private_workspace_without_claiming_recording_or_consent(self):
        output = self.root / "private" / "speech-session-001"
        summary = MODULE.prepare_workspace(self.seed, output, self.repo)

        self.assertEqual(2, summary["case_count"])
        self.assertEqual({"ar-EG": 1, "en": 1}, summary["locale_counts"])
        self.assertTrue((output / "audio").is_dir())
        self.assertEqual([], list((output / "audio").iterdir()))
        self.assertTrue((output / "README_AR.md").is_file())
        self.assertTrue((output / "CONSENT_FORM_AR.md").is_file())

        copied = json.loads((output / "speech-reference-set.json").read_text(encoding="utf-8"))
        expected = {
            "schemaVersion": 1,
            "description": "Transcript-only speech collection prompts; no recordings, consent, or approved references.",
            "recordingStatus": "not-recorded",
            "cases": self.seed_payload()["cases"],
        }
        self.assertEqual(expected, copied)
        for case in copied["cases"]:
            self.assertEqual("not-recorded", case["recordingStatus"])
            self.assertIsNone(case["audioAsset"])
            self.assertIsNone(case["speakerId"])
            self.assertNotIn("consent", case)
            self.assertNotIn("referenceReview", case)
            self.assertEqual({}, case["hypotheses"])

        with (output / "recording-sheet.csv").open(encoding="utf-8-sig", newline="") as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual(2, len(rows))
        self.assertEqual("إزيك؟ عامل إيه النهارده؟", rows[0]["reference_text"])
        self.assertEqual("eg-001.wav", rows[0]["suggested_audio_filename"])
        self.assertNotIn("consent", rows[0])
        self.assertNotIn("speaker_id", rows[0])

        package_readme = (output / "README_AR.md").read_text(encoding="utf-8")
        consent_form = (output / "CONSENT_FORM_AR.md").read_text(encoding="utf-8")
        self.assertIn("لم يُسجَّل أي صوت", package_readme)
        self.assertIn("مدة الاحتفاظ وتاريخ الحذف النهائي", consent_form)
        self.assertIn("استنساخ صوتي", consent_form)

    def test_rejects_output_inside_repository_before_writing(self):
        output = self.repo / "private-dataset"
        with self.assertRaisesRegex(ValueError, "outside the Git repository"):
            MODULE.prepare_workspace(self.seed, output, self.repo)
        self.assertFalse(output.exists())

    def test_refuses_existing_output_directory_and_preserves_its_contents(self):
        output = self.root / "existing"
        output.mkdir()
        sentinel = output / "keep.txt"
        sentinel.write_text("keep", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            MODULE.prepare_workspace(self.seed, output, self.repo)
        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))

    def test_rejects_duplicate_or_unsafe_case_ids(self):
        payload = self.seed_payload()
        payload["cases"][1]["id"] = "eg-001"
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "Duplicate case id"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

        payload = self.seed_payload()
        payload["cases"][0]["id"] = "../escape"
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "safe, non-identifying token"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

    def test_rejects_invalid_locale_and_empty_reference(self):
        payload = self.seed_payload()
        payload["cases"][0]["locale"] = "unknown"
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "locale is not supported"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

        payload = self.seed_payload()
        payload["cases"][0]["referenceText"] = "  "
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "referenceText must be non-empty"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

    def test_rejects_any_seed_that_already_claims_recording_consent_or_asr(self):
        mutations = [
            ("recordingStatus", "recorded"),
            ("audioAsset", "audio/eg-001.wav"),
            ("speakerId", "spk-001"),
            ("consent", {"granted": True}),
            ("referenceReview", {"status": "approved"}),
            ("hypotheses", {"whisper_cpp_base": "prior transcript"}),
        ]
        for key, value in mutations:
            with self.subTest(key=key):
                payload = self.seed_payload()
                payload["cases"][0][key] = value
                self.write_seed(payload)
                with self.assertRaises(ValueError):
                    MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)


    def test_rejects_root_manifest_marked_as_recorded(self):
        payload = self.seed_payload()
        payload["recordingStatus"] = "recorded"
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "root recordingStatus must be not-recorded"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

    def test_rejects_unrecognized_metadata_instead_of_copying_it(self):
        payload = self.seed_payload()
        payload["participantName"] = "Personal identifying metadata"
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "unsupported root metadata fields"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

        payload = self.seed_payload()
        payload["cases"][0]["participantName"] = "Personal identifying metadata"
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "unsupported metadata fields"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

    def test_requires_explicit_consent_policy_on_every_seed_case(self):
        payload = self.seed_payload()
        payload["cases"][0]["consentRequired"] = False
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "must require explicit consent"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)



    def test_rejects_boolean_schema_version_and_non_string_locale(self):
        payload = self.seed_payload()
        payload["schemaVersion"] = True
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "schemaVersion=1"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)

        payload = self.seed_payload()
        payload["cases"][0]["locale"] = ["ar-EG"]
        self.write_seed(payload)
        with self.assertRaisesRegex(ValueError, "locale is not supported"):
            MODULE.prepare_workspace(self.seed, self.root / "out", self.repo)


    def test_default_speech_prompt_bank_has_balanced_transcript_only_coverage(self):
        seed_path = ROOT / "tests" / "TestAssets" / "Speech" / "speech-reference-set.json"
        payload = json.loads(seed_path.read_text(encoding="utf-8"))
        cases = payload["cases"]
        counts = {
            locale: sum(case["locale"] == locale for case in cases)
            for locale in ("ar-EG", "ar", "en", "mixed")
        }

        self.assertEqual(150, len(cases))
        self.assertEqual({"ar-EG": 40, "ar": 36, "en": 36, "mixed": 38}, counts)
        self.assertEqual(len(cases), len({case["id"] for case in cases}))
        self.assertEqual("not-recorded", payload["recordingStatus"])
        for case in cases:
            self.assertIsNone(case["audioAsset"])
            self.assertIsNone(case["speakerId"])
            self.assertIs(True, case["consentRequired"])
            self.assertEqual("not-recorded", case["recordingStatus"])
            self.assertEqual({}, case["hypotheses"])


if __name__ == "__main__":
    unittest.main()
