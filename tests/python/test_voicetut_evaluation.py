import importlib.util
import json
import tempfile
import unittest
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "render_voicetut_evaluation.py"
PROMPT_PATH = ROOT / "tests" / "TestAssets" / "Speech" / "voice-persona-evaluation.json"
SPEC = importlib.util.spec_from_file_location("render_voicetut_evaluation", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)

BRIDGE_PATH = ROOT / "src" / "Layal.Desktop" / "VoiceTut" / "voicetut_local_server.py"
BRIDGE_SPEC = importlib.util.spec_from_file_location("layal_voicetut_bridge", BRIDGE_PATH)
BRIDGE = importlib.util.module_from_spec(BRIDGE_SPEC)
assert BRIDGE_SPEC and BRIDGE_SPEC.loader
sys.modules[BRIDGE_SPEC.name] = BRIDGE
BRIDGE_SPEC.loader.exec_module(BRIDGE)


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
        self.assertEqual(8, len(MODULE.DELIVERY_STYLES))
        self.assertIn("playful", MODULE.DELIVERY_STYLES)
        self.assertEqual("playful", MODULE.DEFAULT_DELIVERY_STYLE)
        self.assertEqual(8, len(MODULE.STYLE_SPEED_MULTIPLIERS))
        self.assertIn("playful", MODULE.STYLE_INSTRUCTIONS)

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

    def test_style_cues_and_speed_preserve_one_default_identity(self):
        base = "female, child, very high pitch"
        playful = MODULE.build_voice_instruction(base, "playful")
        self.assertTrue(playful.startswith(base + "; "))
        self.assertIn("playful", playful.casefold())
        self.assertGreater(MODULE.effective_style_speed(1.0, "energetic"), 1.0)
        self.assertLess(MODULE.effective_style_speed(1.0, "gentle"), 1.0)
        with self.assertRaises(ValueError):
            MODULE.build_voice_instruction(base, "unknown")

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
                "0e92b49d73fb667425a268632ef0d25eac80e6e1ba278fa9c8f8445e7ad6baf9",
                MODULE.sha256_file(path),
            )


    def test_bridge_accepts_only_female_voice_design_and_rejects_reference_audio(self):
        request = {
            "text": "ازيك يا ليال؟",
            "language": "arz",
            "instruct": "female, child, very high pitch",
            "num_steps": 32,
            "guidance_scale": 2.0,
            "speed": 1.0,
            "normalize": True,
        }
        values = BRIDGE.validate_request(request)
        self.assertEqual("female, child, very high pitch", values[2])
        self.assertEqual(32, values[3])
        self.assertTrue(values[6])

        unsupported_msa = dict(request, text="كيف حالك؟", language="ar")
        with self.assertRaises(ValueError):
            BRIDGE.validate_request(unsupported_msa)
        exploratory_msa = dict(request, text="كيف حالك؟", language="arz")
        self.assertEqual("arz", BRIDGE.validate_request(exploratory_msa)[1])

        composed = dict(request, instruct=MODULE.build_voice_instruction(
            "female, child, very high pitch", "playful"))
        self.assertLessEqual(len(composed["instruct"]), BRIDGE.MAX_INSTRUCTION_CHARS)
        self.assertIn("playful", BRIDGE.validate_request(composed)[2].casefold())

        for invalid_prompt in ("male, adult", "female, ref_audio clone", "female, reference audio imitation"):
            invalid = dict(request, instruct=invalid_prompt)
            with self.assertRaises(ValueError):
                BRIDGE.validate_request(invalid)

    def test_bridge_rejects_invalid_generation_ranges_and_non_finite_speed(self):
        request = {
            "text": "Hello Layal",
            "language": "en",
            "instruct": "female, child, very high pitch",
            "num_steps": 100,
            "guidance_scale": 2.0,
            "speed": 1.0,
            "normalize": True,
        }
        with self.assertRaises(ValueError):
            BRIDGE.validate_request(request)

        request["num_steps"] = 32
        request["speed"] = float("nan")
        with self.assertRaises(ValueError):
            BRIDGE.validate_request(request)

    def test_model_source_is_pinned_and_voice_references_are_excluded(self):
        self.assertEqual({"playful", "natural", "gentle", "bright", "clear", "empathetic", "serious", "energetic"},
                         set(MODULE.DELIVERY_STYLES))
        source = MODULE_PATH.read_text(encoding="utf-8")
        self.assertEqual(40, len(MODULE.MODEL_REVISION))
        self.assertIn('references=str(disabled_refs)', source)
        self.assertIn('"model.safetensors"', source)
        self.assertNotIn('"reference_speakers/*"', source)
        self.assertIn('"speakerRightsStatus": "pending"', source)
        self.assertIn('"voiceCloningEnabled": False', source)


if __name__ == "__main__":
    unittest.main()
