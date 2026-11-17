import importlib.util
import json
import sys
import tempfile
import wave
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

    def test_critical_token_metric_detects_lost_arabic_negation(self):
        self.assertEqual((1, 1), MODULE.score_critical_tokens("أنا مش موافق", "أنا موافق"))

    def test_critical_token_metric_detects_changed_numbers(self):
        self.assertEqual((1, 1), MODULE.score_critical_tokens("الملف رقم 42", "الملف رقم 43"))

    def test_critical_token_metric_ignores_non_critical_edits(self):
        self.assertEqual((0, 0), MODULE.score_critical_tokens("من فضلك افتح الملف", "لو سمحت افتح المستند"))

    def test_critical_token_metric_counts_missing_negation_and_number(self):
        self.assertEqual((2, 2), MODULE.score_critical_tokens("Do not upload file 12", "Upload file"))

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

    @staticmethod
    def recorded_case():
        return {
            "id": "eg-recorded-001",
            "locale": "ar-EG",
            "referenceText": "أنا مش موافق على الرقم 42",
            "recordingStatus": "recorded",
            "audioAsset": "audio/eg-recorded-001.wav",
            "speakerId": "spk-001",
            "consent": {
                "granted": True,
                "recordedAt": "2026-11-17T10:00:00Z",
                "scope": "research-evaluation",
            },
            "referenceReview": {
                "status": "approved",
                "reviewerId": "rev-001",
                "reviewedAt": "2026-11-17T11:00:00Z",
            },
            "hypotheses": {"whisper_cpp_base": "أنا مش موافق على الرقم 42"},
        }

    def test_recorded_case_requires_explicit_consent_and_human_review(self):
        case = self.recorded_case()
        case["consent"]["granted"] = False
        case["referenceReview"]["status"] = "pending"
        errors = MODULE.validate_manifest({"schemaVersion": 1, "cases": [case]})
        self.assertTrue(any("consent.granted=true" in error for error in errors))
        self.assertTrue(any("cannot contain ASR hypotheses" in error for error in errors))

    def test_recorded_case_accepts_pseudonymous_consent_and_approved_reference(self):
        errors = MODULE.validate_manifest({"schemaVersion": 1, "cases": [self.recorded_case()]})
        self.assertEqual([], errors)

    def test_recorded_case_rejects_path_traversal_and_identifying_speaker_id(self):
        case = self.recorded_case()
        case["audioAsset"] = "audio/../../private.wav"
        case["speakerId"] = "speaker-name@example.com"
        errors = MODULE.validate_manifest({"schemaVersion": 1, "cases": [case]})
        self.assertTrue(any("relative WAV path" in error for error in errors))
        self.assertTrue(any("pseudonymous speakerId" in error for error in errors))

    def test_malformed_recording_and_review_states_are_reported_without_crashing(self):
        case = self.recorded_case()
        case["recordingStatus"] = []
        errors = MODULE.validate_manifest({"schemaVersion": 1, "cases": [case]})
        self.assertTrue(any("unsupported recordingStatus" in error for error in errors))

        case = self.recorded_case()
        case["referenceReview"]["status"] = []
        errors = MODULE.validate_manifest({"schemaVersion": 1, "cases": [case]})
        self.assertTrue(any("referenceReview.status" in error for error in errors))

    def test_not_recorded_case_cannot_reference_audio_or_consent(self):
        case = {
            "id": "eg-unrecorded",
            "locale": "ar-EG",
            "referenceText": "جملة اختبار",
            "audioAsset": "audio/hidden.wav",
            "consent": {"granted": True},
            "hypotheses": {},
        }
        errors = MODULE.validate_manifest({"schemaVersion": 1, "cases": [case]})
        self.assertTrue(any("must not reference audioAsset" in error for error in errors))
        self.assertTrue(any("must not store consent metadata" in error for error in errors))

    def test_audio_file_check_requires_existing_file_inside_audio_directory(self):
        case = self.recorded_case()
        payload = {"schemaVersion": 1, "cases": [case]}
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "speech-reference-set.json"
            audio_dir = Path(directory) / "audio"
            audio_dir.mkdir()
            self.assertTrue(any("does not exist" in error for error in MODULE.validate_audio_files(payload, manifest)))
            with wave.open(str(audio_dir / "eg-recorded-001.wav"), "wb") as audio:
                audio.setnchannels(1)
                audio.setsampwidth(2)
                audio.setframerate(16000)
                audio.writeframes(b"\\x00\\x00" * 160)
            self.assertEqual([], MODULE.validate_audio_files(payload, manifest))

    def test_audio_file_check_rejects_wrong_sample_format(self):
        case = self.recorded_case()
        payload = {"schemaVersion": 1, "cases": [case]}
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "speech-reference-set.json"
            audio_dir = Path(directory) / "audio"
            audio_dir.mkdir()
            with wave.open(str(audio_dir / "eg-recorded-001.wav"), "wb") as audio:
                audio.setnchannels(2)
                audio.setsampwidth(2)
                audio.setframerate(44100)
                audio.writeframes(b"\\x00\\x00\\x00\\x00" * 160)
            errors = MODULE.validate_audio_files(payload, manifest)
            self.assertTrue(any("mono 16 kHz / 16-bit PCM" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
