import importlib.util
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "score_speech_transcripts.py"
MANIFEST_PATH = ROOT / "tests" / "TestAssets" / "Speech" / "speech-reference-set.json"
SPEC = importlib.util.spec_from_file_location("score_speech_transcripts", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class SpeechTranscriptScoringTests(unittest.TestCase):
    def test_reference_manifest_is_valid_and_has_expected_language_coverage(self):
        payload = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
        self.assertEqual([], MODULE.validate_manifest(payload))
        self.assertEqual(30, len(payload["cases"]))

        counts = {}
        for case in payload["cases"]:
            counts[case["locale"]] = counts.get(case["locale"], 0) + 1
        self.assertEqual({"ar-EG": 10, "ar": 6, "en": 6, "mixed": 8}, counts)

    def test_normalization_is_conservative_for_arabic_diacritics_and_punctuation(self):
        self.assertEqual("انت عايز ايه", MODULE.normalize("إِنْتَ عايز إيه؟"))

    def test_identical_transcripts_have_zero_word_and_character_error(self):
        word_errors, words, char_errors, characters = MODULE.score_pair(
            "Please open the settings page.",
            "Please open the settings page!",
        )
        self.assertEqual(0, word_errors)
        self.assertEqual(5, words)
        self.assertEqual(0, char_errors)
        self.assertGreater(characters, 0)

    def test_empty_hypothesis_is_counted_as_deletions(self):
        word_errors, words, char_errors, characters = MODULE.score_pair("hello world", "")
        self.assertEqual((2, 2, 10, 10), (word_errors, words, char_errors, characters))

    def test_invalid_locale_shape_is_reported_without_crashing(self):
        payload = {
            "schemaVersion": 1,
            "cases": [{
                "id": "bad-locale",
                "locale": [],
                "referenceText": "hello",
                "hypotheses": {}
            }]
        }
        errors = MODULE.validate_manifest(payload)
        self.assertTrue(any("unsupported locale" in error for error in errors))

    def test_duplicate_case_ids_and_invalid_hypotheses_are_reported(self):
        payload = {
            "schemaVersion": 1,
            "cases": [
                {"id": "same", "locale": "en", "referenceText": "hello", "hypotheses": {"engine": "hello"}},
                {"id": "same", "locale": "en", "referenceText": "world", "hypotheses": {"engine": 42}},
            ],
        }
        errors = MODULE.validate_manifest(payload)
        self.assertTrue(any("duplicates id" in error for error in errors))
        self.assertTrue(any("must be a string" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
