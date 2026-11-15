import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "render_voicetut_evaluation.py"
PROMPT_PATH = ROOT / "tests" / "TestAssets" / "Speech" / "voice-persona-evaluation.json"
SPEC = importlib.util.spec_from_file_location("render_voicetut_evaluation", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)


class VoiceTutEvaluationTests(unittest.TestCase):
    def test_language_mapping_keeps_msa_exploratory(self):
        self.assertEqual(("arz", True), MODULE.normalize_language_code("EgyptianArabic"))
        self.assertEqual(("en", True), MODULE.normalize_language_code("English"))
        self.assertEqual(("arz", True), MODULE.normalize_language_code("MixedArabicEnglish"))
        self.assertEqual(("arz", False), MODULE.normalize_language_code("ArabicStandard"))
        self.assertEqual(
            {"EgyptianArabic", "English", "MixedArabicEnglish"},
            MODULE.SUPPORTED_LANGUAGES,
        )

    def test_fixed_voice_persona_set_has_all_four_language_groups(self):
        payload = MODULE.load_prompt_set(PROMPT_PATH)
        counts = {}
        for case in payload["testCases"]:
            counts[case["language"]] = counts.get(case["language"], 0) + 1
        self.assertEqual(18, len(payload["testCases"]))
        self.assertEqual({
            "EgyptianArabic": 6,
            "ArabicStandard": 4,
            "English": 4,
            "MixedArabicEnglish": 4,
        }, counts)

    def test_evaluation_output_ids_are_safe_relative_file_tokens(self):
        self.assertEqual("egy-01", MODULE.safe_case_id("egy-01"))
        self.assertEqual("mixed-case-02", MODULE.safe_case_id("mixed case/02"))
        with self.assertRaises(ValueError):
            MODULE.safe_case_id("///")

    def test_sha256_file_records_exact_model_or_audio_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "bytes.bin"
            path.write_bytes(b"layal-voice-tut")
            self.assertEqual(
                "8d4624d2e35b16a9159d6dcefbf4b8c4a725ae97cc5b6717907c52f3a9d3c7b1",
                MODULE.sha256_file(path),
            )

    def test_model_source_is_pinned_and_voice_references_are_excluded(self):
        source = MODULE_PATH.read_text(encoding="utf-8")
        self.assertEqual(40, len(MODULE.MODEL_REVISION))
        self.assertIn('references=str(disabled_refs)', source)
        self.assertIn('"model.safetensors"', source)
        self.assertNotIn('"reference_speakers/*"', source)
        self.assertIn('"speakerRightsStatus": "pending"', source)
        self.assertIn('"voiceCloningEnabled": False', source)


if __name__ == "__main__":
    unittest.main()
