import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
import wave
import zipfile
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "run_whisper_benchmark.py"
SPEC = importlib.util.spec_from_file_location("run_whisper_benchmark", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class WhisperBenchmarkTests(unittest.TestCase):
    def make_resources(self, root):
        archive_path = root / MODULE.WHISPER_ARCHIVE_NAME
        install_root = root / MODULE.WHISPER_INSTALL_RELATIVE_PATH
        model_path = root / MODULE.WHISPER_MODEL_RELATIVE_PATH
        install_root.mkdir(parents=True)
        model_path.parent.mkdir(parents=True)

        runtime_files = {
            "whisper-cli.exe": b"test whisper executable",
            "ggml-base.dll": b"test whisper dependency",
        }
        with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, payload in runtime_files.items():
                archive.writestr(name, payload)
                (install_root / name).write_bytes(payload)
        model_path.write_bytes(b"test whisper model")

        MODULE.WHISPER_ARCHIVE_SIZE = archive_path.stat().st_size
        MODULE.WHISPER_ARCHIVE_SHA256 = MODULE.sha256_file(archive_path)
        MODULE.WHISPER_MODEL_SIZE = model_path.stat().st_size
        MODULE.WHISPER_MODEL_SHA256 = MODULE.sha256_file(model_path)
        return archive_path, install_root, model_path

    @staticmethod
    def write_audio(path):
        path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(path), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(16000)
            audio.writeframes(b"\x00\x00" * 16000)

    @staticmethod
    def recorded_manifest():
        return {
            "schemaVersion": 1,
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
                "hypotheses": {},
            }],
        }

    def setUp(self):
        self._constants = {
            name: getattr(MODULE, name)
            for name in (
                "WHISPER_ARCHIVE_SIZE", "WHISPER_ARCHIVE_SHA256",
                "WHISPER_MODEL_SIZE", "WHISPER_MODEL_SHA256",
            )
        }

    def tearDown(self):
        for name, value in self._constants.items():
            setattr(MODULE, name, value)

    def test_verified_resources_require_archive_model_and_extracted_files_to_match(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_resources(root)
            verified = MODULE.verify_whisper_resources(root)
            self.assertEqual("whisper-cli.exe", verified["executable"].name)
            self.assertEqual(MODULE.WHISPER_MODEL_SHA256, verified["model_sha256"])

            (root / MODULE.WHISPER_INSTALL_RELATIVE_PATH / "ggml-base.dll").write_bytes(b"tampered")
            with self.assertRaisesRegex(ValueError, "does not match the pinned archive"):
                MODULE.verify_whisper_resources(root)

    def test_resource_verification_rejects_tampered_model_before_launch(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_resources(root)
            (root / MODULE.WHISPER_MODEL_RELATIVE_PATH).write_bytes(b"tampered")
            with self.assertRaisesRegex(ValueError, "model size|model SHA-256"):
                MODULE.verify_whisper_resources(root)

    def test_archive_member_validation_rejects_traversal_and_drive_paths(self):
        for value in ("../escape.exe", "folder/../../escape.exe", "C:/escape.exe", "/rooted.exe"):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    MODULE._validate_archive_member(value)

    def test_unrecorded_starter_corpus_is_not_treated_as_audio_benchmark(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "speech-reference-set.json"
            manifest.write_text(json.dumps({
                "schemaVersion": 1,
                "cases": [{
                    "id": "transcript-only",
                    "locale": "ar-EG",
                    "referenceText": "إزيك",
                    "recordingStatus": "not-recorded",
                    "audioAsset": None,
                    "hypotheses": {},
                }],
            }), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "No eligible recordings"):
                MODULE.validate_manifest_and_audio(manifest)

    def test_recording_requires_consent_approved_human_reference_and_real_wav(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "speech-reference-set.json"
            audio_path = root / "audio" / "eg-recorded-001.wav"
            self.write_audio(audio_path)
            payload = self.recorded_manifest()
            manifest.write_text(json.dumps(payload), encoding="utf-8")
            _, cases = MODULE.validate_manifest_and_audio(manifest)
            self.assertEqual(1, len(cases))
            self.assertEqual("ar-EG", cases[0]["locale"])
            self.assertEqual(1.0, cases[0]["audio_duration_seconds"])

            payload["cases"][0]["consent"]["granted"] = False
            manifest.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "consent.granted=true"):
                MODULE.validate_manifest_and_audio(manifest)

    def test_successful_run_writes_separate_report_and_scoring_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_resources(root)
            manifest = root / "speech-reference-set.json"
            audio_path = root / "audio" / "eg-recorded-001.wav"
            self.write_audio(audio_path)
            original = self.recorded_manifest()
            manifest.write_text(json.dumps(original), encoding="utf-8")
            output_dir = root / "evaluation"

            def fake_whisper(command, **kwargs):
                self.assertFalse(kwargs["shell"])
                self.assertEqual("auto", command[command.index("-l") + 1])
                prefix = Path(command[command.index("-of") + 1])
                prefix.with_suffix(".json").write_text(json.dumps({
                    "transcription": [{"text": "أنا مش موافق على الرقم 42"}],
                }), encoding="utf-8")
                return SimpleNamespace(returncode=0, stdout="", stderr="")

            with mock.patch.object(MODULE.subprocess, "run", side_effect=fake_whisper) as run:
                report_path, scoring_path, report = MODULE.run_benchmark(
                    root, manifest, output_dir, "test-run", 2, 30
                )

            self.assertEqual(1, run.call_count)
            self.assertTrue(report_path.is_file())
            self.assertTrue(scoring_path.is_file())
            self.assertEqual(1, report["summary"]["passedSamples"])
            self.assertEqual("passed", report["samples"][0]["status"])
            scoring = json.loads(scoring_path.read_text(encoding="utf-8"))
            self.assertEqual(
                "أنا مش موافق على الرقم 42",
                scoring["cases"][0]["hypotheses"][MODULE.ENGINE_ID],
            )
            self.assertEqual(original, json.loads(manifest.read_text(encoding="utf-8")))
            self.assertFalse(any(output_dir.glob("layal-whisper-*")))

    def test_invalid_resources_block_subprocess_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_resources(root)
            manifest = root / "speech-reference-set.json"
            output = root / "evaluation"
            (root / MODULE.WHISPER_MODEL_RELATIVE_PATH).write_bytes(b"wrong bytes")
            with mock.patch.object(MODULE.subprocess, "run") as run:
                with self.assertRaises(ValueError):
                    MODULE.run_benchmark(root, manifest, output, "test-run", 2, 30)
                run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
