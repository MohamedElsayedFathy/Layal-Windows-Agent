import importlib.util
import json
import tempfile
import unittest
import wave
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "run_qwencleo_benchmark.py"
SPEC = importlib.util.spec_from_file_location("run_qwencleo_benchmark", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)

REVISION = "a" * 40


class QwenCleoBenchmarkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.model_dir = self.root / "model-snapshot"
        self.model_dir.mkdir()
        (self.model_dir / "config.json").write_text(
            json.dumps({"model_type": "qwen3_asr", "architectures": ["Qwen3ASRModel"]}),
            encoding="utf-8",
        )
        (self.model_dir / "model.safetensors").write_bytes(b"test model weights")

        self.manifest_dir = self.root / "collection"
        (self.manifest_dir / "audio").mkdir(parents=True)
        self.audio_path = self.manifest_dir / "audio" / "eg-recorded-001.wav"
        with wave.open(str(self.audio_path), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(16000)
            audio.writeframes(b"\x00\x00" * 16000)
        self.manifest_path = self.manifest_dir / "speech-reference-set.json"
        self.source_payload = {
            "schemaVersion": 1,
            "description": "local collection",
            "privateRootNote": "must not be copied to the scoring manifest",
            "cases": [{
                "id": "eg-recorded-001",
                "locale": "ar-EG",
                "referenceText": "أنا مش موافق على الرقم 42",
                "recordingStatus": "recorded",
                "audioAsset": "audio/eg-recorded-001.wav",
                "speakerId": "spk-001",
                "consent": {
                    "granted": True,
                    "recordedAt": "2026-11-19T08:00:00Z",
                    "scope": "research-evaluation",
                },
                "referenceReview": {
                    "status": "approved",
                    "reviewerId": "rev-001",
                    "reviewedAt": "2026-11-19T08:10:00Z",
                },
                "hypotheses": {MODULE.ENGINE_ID: "stale prior transcript"},
                "privateCaseNote": "must not be copied",
            }],
        }
        self.manifest_path.write_text(json.dumps(self.source_payload), encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def test_model_snapshot_is_hashed_and_revision_must_be_immutable(self):
        root, inventory = MODULE.inspect_model_snapshot(self.model_dir, REVISION)
        self.assertEqual(self.model_dir.resolve(), root)
        self.assertEqual({"config.json", "model.safetensors"}, {item["path"] for item in inventory})
        weight = next(item for item in inventory if item["path"] == "model.safetensors")
        self.assertEqual(MODULE.sha256_file(self.model_dir / "model.safetensors"), weight["sha256"])
        with self.assertRaisesRegex(ValueError, "40-character"):
            MODULE.inspect_model_snapshot(self.model_dir, "main")

    def test_snapshot_folder_commit_must_match_requested_revision(self):
        snapshot = self.root / REVISION
        snapshot.mkdir()
        (snapshot / "config.json").write_text(
            json.dumps({"model_type": "qwen3_asr"}), encoding="utf-8"
        )
        (snapshot / "model.safetensors").write_bytes(b"weights")
        with self.assertRaisesRegex(ValueError, "does not match"):
            MODULE.inspect_model_snapshot(snapshot, "b" * 40)

    def test_snapshot_rejects_symbolic_links(self):
        link = self.model_dir / "linked-weights.safetensors"
        target = self.root / "external.safetensors"
        target.write_bytes(b"external weights")
        try:
            link.symlink_to(target)
        except (OSError, NotImplementedError):
            self.skipTest("Symbolic links are unavailable in this test environment.")
        with self.assertRaisesRegex(ValueError, "symbolic-link"):
            MODULE.inspect_model_snapshot(self.model_dir, REVISION)

    def test_language_hints_keep_english_experimental(self):
        self.assertEqual("Arabic", MODULE.language_hint("ar-EG"))
        self.assertEqual("Arabic", MODULE.language_hint("ar"))
        self.assertEqual("Arabic", MODULE.language_hint("mixed"))
        self.assertIsNone(MODULE.language_hint("en"))
        with self.assertRaises(ValueError):
            MODULE.language_hint("fr")

    def test_scoring_manifest_is_allowlisted_and_drops_stale_challenger_result(self):
        result = {
            "caseId": "eg-recorded-001",
            "transcript": "أنا مش موافق على الرقم 42",
            "status": "passed",
        }
        scoring = MODULE._scoring_manifest(self.source_payload, [result])
        case = scoring["cases"][0]
        self.assertEqual("أنا مش موافق على الرقم 42", case["hypotheses"][MODULE.ENGINE_ID])
        self.assertNotIn("privateRootNote", scoring)
        self.assertNotIn("privateCaseNote", case)

        scoring_failed = MODULE._scoring_manifest(self.source_payload, [])
        self.assertNotIn(MODULE.ENGINE_ID, scoring_failed["cases"][0]["hypotheses"])

    def test_run_uses_only_local_model_loader_and_writes_report_and_scoring_copy(self):
        class FakeModel:
            def __init__(self):
                self.calls = []

            def transcribe(self, audio, language):
                self.calls.append((Path(audio).is_file(), language))
                return [SimpleNamespace(text="أنا مش موافق على الرقم 42")]

        fake_model = FakeModel()
        loader = mock.Mock(return_value=(fake_model, {
            "device": "cpu",
            "dtype": "float32",
            "torchVersion": "test",
            "qwenAsrVersion": "test",
        }))
        output_dir = self.root / "evaluation"
        report_path, scoring_path, report = MODULE.run_benchmark(
            self.manifest_path, self.model_dir, REVISION, output_dir, "cpu", loader
        )

        loader.assert_called_once_with(self.model_dir.resolve(), "cpu")
        self.assertEqual([(True, "Arabic")], fake_model.calls)
        self.assertTrue(report_path.is_file())
        self.assertTrue(scoring_path.is_file())
        self.assertEqual(1, report["summary"]["passedSamples"])
        self.assertEqual(0, report["summary"]["failedSamples"])
        self.assertTrue(report["policy"]["localOnly"])
        self.assertFalse(report["policy"]["modelDownloadAttempted"])
        self.assertFalse(report["engine"]["productionEligible"])
        scoring = json.loads(scoring_path.read_text(encoding="utf-8"))
        self.assertEqual(
            "أنا مش موافق على الرقم 42",
            scoring["cases"][0]["hypotheses"][MODULE.ENGINE_ID],
        )
        self.assertEqual(self.source_payload, json.loads(self.manifest_path.read_text(encoding="utf-8")))

    def test_invalid_consent_or_unapproved_reference_blocks_before_model_load(self):
        payload = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        payload["cases"][0]["consent"]["granted"] = False
        self.manifest_path.write_text(json.dumps(payload), encoding="utf-8")
        loader = mock.Mock()
        with self.assertRaisesRegex(ValueError, "consent.granted=true"):
            MODULE.run_benchmark(
                self.manifest_path, self.model_dir, REVISION, self.root / "evaluation", "cpu", loader
            )
        loader.assert_not_called()

    def test_failed_sample_does_not_reuse_stale_hypothesis(self):
        class FailingModel:
            def transcribe(self, audio, language):
                raise RuntimeError(f"synthetic failure at {audio}")

        _, scoring_path, report = MODULE.run_benchmark(
            self.manifest_path, self.model_dir, REVISION, self.root / "evaluation", "cpu",
            mock.Mock(return_value=(FailingModel(), {"device": "cpu"})),
        )
        self.assertEqual(1, report["summary"]["failedSamples"])
        failure = report["failures"][0]["error"]
        self.assertIn("<local path>", failure)
        self.assertNotIn(str(self.audio_path), failure)
        scoring = json.loads(scoring_path.read_text(encoding="utf-8"))
        self.assertNotIn(MODULE.ENGINE_ID, scoring["cases"][0]["hypotheses"])


if __name__ == "__main__":
    unittest.main()
