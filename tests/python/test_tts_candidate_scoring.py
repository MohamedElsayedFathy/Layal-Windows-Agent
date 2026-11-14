import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "score_tts_candidates.py"
PROMPT_PATH = ROOT / "tests" / "TestAssets" / "Speech" / "voice-persona-evaluation.json"
SPEC = importlib.util.spec_from_file_location("score_tts_candidates", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class TtsCandidateScoringTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.prompt_payload = json.loads(PROMPT_PATH.read_text(encoding="utf-8"))
        errors, cls.prompt_cases = MODULE.validate_prompt_set(cls.prompt_payload)
        if errors:
            raise AssertionError(errors)

    def make_engine(self, languages=None):
        return {
            "id": "candidate-a",
            "displayName": "Candidate A",
            "sourceUrl": "https://example.com/model",
            "revision": "0123456789abcdef0123456789abcdef01234567",
            "revisionIsImmutable": True,
            "speakerId": "female-01",
            "licenseStatus": "pending",
            "speakerRightsStatus": "pending",
            "manualAcceptanceStatus": "pending",
            "voiceCloningEnabled": False,
            "modelSha256": ["a" * 64],
            "supportedLanguages": languages or ["EgyptianArabic"],
            "supportedStyles": ["natural"],
        }

    def make_manifest(self, samples=None, languages=None):
        return {
            "schemaVersion": 1,
            "runId": "test-run",
            "device": {"os": "Windows", "cpu": "Test CPU", "ramMb": 8192, "gpu": "Test GPU"},
            "engines": [self.make_engine(languages)],
            "samples": samples or [],
        }

    def write_wav(self, path, values=None, rate=24000):
        values = values if values is not None else [0, 1000, -1000, 10000, -10000] * 240
        path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(path), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(rate)
            import struct
            wav.writeframes(b"".join(struct.pack("<h", value) for value in values))
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def sample_for(self, directory, audio_path):
        # Give each prompt a distinct deterministic waveform; real TTS renders for
        # different prompts must not collapse to the same cached audio artifact.
        seed = sum(audio_path.encode("utf-8")) % 12000 + 1000
        values = [0, seed, -seed, 10000, -10000] * 240
        digest = self.write_wav(directory / audio_path, values=values)
        return {
            "caseId": "egy-01",
            "engineId": "candidate-a",
            "style": "natural",
            "audioPath": audio_path,
            "audioSha256": digest,
            "renderTimeMs": 1000,
            "timeToFirstAudioMs": 125,
            "ratings": [{
                "raterId": "rater-01",
                "scores": {
                    "promptFidelity": 5,
                    "naturalness": 4,
                    "intelligibility": 5,
                    "softness": 4,
                    "accentFit": 4,
                    "identityConsistency": 4,
                    "prosodyFit": 3,
                    "pronunciationAccuracy": 5,
                },
            }],
        }

    def test_fixed_voice_prompt_set_has_expected_language_coverage(self):
        counts = {}
        for case in self.prompt_cases.values():
            counts[case["language"]] = counts.get(case["language"], 0) + 1
        self.assertEqual(18, len(self.prompt_cases))
        self.assertEqual({
            "EgyptianArabic": 6,
            "ArabicStandard": 4,
            "English": 4,
            "MixedArabicEnglish": 4,
        }, counts)

    def test_valid_wav_is_hashed_and_measured_without_loading_entire_file(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            digest = self.write_wav(root / "samples" / "egy-01.wav")
            metrics = MODULE.inspect_wav(root / "samples" / "egy-01.wav", digest)
            self.assertEqual(24000, metrics["sampleRateHz"])
            self.assertAlmostEqual(50.0, metrics["durationMs"], places=2)
            self.assertGreater(metrics["rmsFullScale"], 0)
            self.assertEqual(0.0, metrics["clippedSamplesPercent"])

    def test_build_report_does_not_turn_missing_samples_into_zero_quality(self):
        manifest = self.make_manifest()
        errors, engines = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertEqual([], errors)
        report = MODULE.build_report(manifest, self.prompt_cases, engines, [])
        engine = report["engines"][0]
        self.assertEqual(0, engine["observedSamples"])
        self.assertEqual(6, engine["expectedSamples"])
        self.assertEqual(0, engine["coveragePercent"])
        self.assertIsNone(engine["metrics"]["realTimeFactor"])
        self.assertFalse(engine["eligibleForProduction"])

    def test_score_uses_duration_and_keeps_human_dimensions_separate(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sample = self.sample_for(root, "samples/egy-01.wav")
            manifest = self.make_manifest([sample])
            errors, engines = MODULE.validate_manifest(manifest, self.prompt_cases)
            self.assertEqual([], errors)
            audio = MODULE.inspect_wav(root / sample["audioPath"], sample["audioSha256"])
            duration = audio["durationMs"]
            record = {
                **sample,
                "language": "EgyptianArabic",
                "realTimeFactor": sample["renderTimeMs"] / duration,
                **audio,
            }
            report = MODULE.build_report(manifest, self.prompt_cases, engines, [record])
            engine = report["engines"][0]
            self.assertAlmostEqual(20.0, engine["metrics"]["realTimeFactor"], places=3)
            self.assertEqual(5.0, engine["humanRatings"]["promptFidelity"])
            self.assertEqual(4.0, engine["humanRatings"]["naturalness"])
            self.assertEqual(5.0, engine["humanRatings"]["intelligibility"])
            self.assertEqual(manifest["device"], report["device"])
            self.assertEqual("https://example.com/model", engine["sourceUrl"])
            self.assertEqual(["a" * 64], engine["modelSha256"])
            self.assertNotIn("overallScore", engine)

    def test_audio_path_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(ValueError):
                MODULE.resolve_audio_file(Path(temp), "../outside.wav")
            with self.assertRaises(ValueError):
                MODULE.resolve_audio_file(Path(temp), r"C:\\outside.wav")

    def test_audio_hash_mismatch_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "sample.wav"
            self.write_wav(path)
            with self.assertRaisesRegex(ValueError, "does not match"):
                MODULE.inspect_wav(path, "0" * 64)

    def test_duplicate_case_engine_style_rows_are_rejected(self):
        sample = {
            "caseId": "egy-01", "engineId": "candidate-a", "style": "natural",
            "audioPath": "samples/a.wav", "audioSha256": "a" * 64,
            "renderTimeMs": 100, "timeToFirstAudioMs": 50, "ratings": [],
        }
        manifest = self.make_manifest([sample, dict(sample)])
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertTrue(any("duplicates the case/engine/style" in error for error in errors))

    def test_engine_cannot_claim_unsupported_language_or_enable_voice_cloning(self):
        engine = self.make_engine()
        engine["voiceCloningEnabled"] = True
        manifest = {"schemaVersion": 1, "runId": "x", "engines": [engine], "samples": []}
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertTrue(any("voiceCloningEnabled" in error for error in errors))
        engine["voiceCloningEnabled"] = False
        manifest["samples"] = [{
            "caseId": "eng-01", "engineId": "candidate-a", "style": "natural",
            "audioPath": "samples/a.wav", "audioSha256": "a" * 64,
            "renderTimeMs": 100, "timeToFirstAudioMs": 50, "ratings": [],
        }]
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertTrue(any("not declared by engine" in error for error in errors))

    def test_malformed_unhashable_sample_fields_are_reported_not_crashed(self):
        manifest = self.make_manifest([{
            "caseId": ["egy-01"], "engineId": "candidate-a", "style": ["natural"],
            "audioPath": "samples/a.wav", "audioSha256": "a" * 64,
            "renderTimeMs": 100, "timeToFirstAudioMs": 50, "ratings": [],
        }])
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertTrue(any("unknown caseId" in error for error in errors))

    def test_production_gate_requires_two_human_ratings_and_manual_acceptance(self):
        engine = self.make_engine()
        engine["licenseStatus"] = "verified"
        engine["licenseEvidenceUrl"] = "https://example.com/license"
        engine["speakerRightsStatus"] = "verified"
        engine["speakerRightsEvidenceUrl"] = "https://example.com/rights"
        engine["manualAcceptanceStatus"] = "accepted"
        cases = [case_id for case_id, case in self.prompt_cases.items() if case["language"] == "EgyptianArabic"]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            samples = []
            records = []
            for case_id in cases:
                sample = self.sample_for(root, f"samples/{case_id}.wav")
                sample["caseId"] = case_id
                sample["ratings"].append({"raterId": "rater-02", "scores": dict(sample["ratings"][0]["scores"])})
                samples.append(sample)
                audio = MODULE.inspect_wav(root / sample["audioPath"], sample["audioSha256"])
                records.append({
                    **sample, "language": "EgyptianArabic",
                    "realTimeFactor": sample["renderTimeMs"] / audio["durationMs"],
                    **audio,
                })
            manifest = {
                "schemaVersion": 1,
                "runId": "accepted-test",
                "device": {"os": "Windows", "cpu": "Test CPU", "ramMb": 8192, "gpu": "Test GPU"},
                "engines": [engine],
                "samples": samples,
            }
            errors, engines = MODULE.validate_manifest(manifest, self.prompt_cases)
            self.assertEqual([], errors)
            report = MODULE.build_report(manifest, self.prompt_cases, engines, records)
            self.assertTrue(report["engines"][0]["productionGates"]["humanReviewComplete"])
            self.assertFalse(report["engines"][0]["productionGates"]["targetLanguageCoverage"])
            self.assertFalse(report["engines"][0]["productionGates"]["targetStyleCoverage"])
            self.assertFalse(report["engines"][0]["eligibleForProduction"])

    def test_verified_rights_need_https_evidence_and_scores_must_be_in_range(self):
        engine = self.make_engine()
        engine["licenseStatus"] = "verified"
        engine["speakerRightsStatus"] = "verified"
        manifest = {"schemaVersion": 1, "runId": "x", "engines": [engine], "samples": []}
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertTrue(any("licenseEvidenceUrl" in error for error in errors))
        self.assertTrue(any("speakerRightsEvidenceUrl" in error for error in errors))

        engine["licenseEvidenceUrl"] = "https://example.com/license"
        engine["speakerRightsEvidenceUrl"] = "https://example.com/rights"
        sample = {
            "caseId": "egy-01", "engineId": "candidate-a", "style": "natural",
            "audioPath": "samples/a.wav", "audioSha256": "a" * 64,
            "renderTimeMs": 100, "timeToFirstAudioMs": 50,
            "ratings": [{"raterId": "r1", "scores": {field: 6 for field in MODULE.RATING_FIELDS}}],
        }
        manifest["samples"] = [sample]
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertTrue(any("between 1 and 5" in error for error in errors))


    def test_production_target_requires_all_languages_all_styles_and_manual_acceptance(self):
        engine = self.make_engine(languages=sorted(MODULE.LANGUAGES))
        engine["supportedStyles"] = sorted(MODULE.STYLES)
        engine["licenseStatus"] = "verified"
        engine["licenseEvidenceUrl"] = "https://example.com/license"
        engine["speakerRightsStatus"] = "verified"
        engine["speakerRightsEvidenceUrl"] = "https://example.com/rights"
        engine["manualAcceptanceStatus"] = "accepted"

        records = []
        for case_id, case in self.prompt_cases.items():
            for style in engine["supportedStyles"]:
                scores = {field: 4 for field in MODULE.RATING_FIELDS}
                records.append({
                    "caseId": case_id,
                    "engineId": engine["id"],
                    "language": case["language"],
                    "style": style,
                    "renderTimeMs": 500,
                    "timeToFirstAudioMs": 80,
                    "durationMs": 1000,
                    "realTimeFactor": 0.5,
                    "rmsFullScale": 0.1,
                    "peakFullScale": 0.2,
                    "clippedSamplesPercent": 0.0,
                    "sha256": "a" * 64,
                    "sampleRateHz": 24000,
                    "ratings": [
                        {"raterId": "rater-01", "scores": scores},
                        {"raterId": "rater-02", "scores": scores},
                    ],
                })

        manifest = {
            "schemaVersion": 1,
            "runId": "full-target-test",
            "device": {"os": "Windows", "cpu": "Test CPU", "ramMb": 8192, "gpu": "Test GPU"},
            "engines": [engine],
            "samples": [],
        }
        errors, engines = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertEqual([], errors)
        report = MODULE.build_report(manifest, self.prompt_cases, engines, records)
        result = report["engines"][0]
        self.assertEqual(144, result["expectedSamples"])
        self.assertEqual(144, result["observedSamples"])
        self.assertTrue(result["productionGates"]["targetLanguageCoverage"])
        self.assertTrue(result["productionGates"]["targetStyleCoverage"])
        self.assertTrue(result["productionGates"]["humanReviewComplete"])
        self.assertTrue(result["eligibleForProduction"])

        engine["manualAcceptanceStatus"] = "pending"
        report = MODULE.build_report(manifest, self.prompt_cases, engines, records)
        self.assertFalse(report["engines"][0]["eligibleForProduction"])


    def test_same_candidate_wav_cannot_be_reused_for_different_prompts(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sample = self.sample_for(root, "samples/reused.wav")
            duplicate = dict(sample)
            duplicate["caseId"] = "egy-02"
            duplicate["style"] = "gentle"
            manifest = self.make_manifest([sample, duplicate])
            errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
            self.assertTrue(any("reuses an identical WAV hash for a different prompt" in error for error in errors))

    def test_zero_render_time_and_missing_device_provenance_are_rejected(self):
        manifest = self.make_manifest()
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertEqual([], errors)

        manifest["device"].pop("cpu")
        manifest["samples"] = [{
            "caseId": "egy-01", "engineId": "candidate-a", "style": "natural",
            "audioPath": "samples/a.wav", "audioSha256": "a" * 64,
            "renderTimeMs": 0, "timeToFirstAudioMs": 0, "ratings": [],
        }]
        errors, _ = MODULE.validate_manifest(manifest, self.prompt_cases)
        self.assertTrue(any("device.cpu" in error for error in errors))
        self.assertTrue(any("positive numeric 'renderTimeMs'" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
