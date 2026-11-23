import importlib.util
import json
import sys
import tempfile
import wave
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
PREP_PATH = ROOT / "tools" / "evaluation" / "prepare_synthetic_speech_smoke.py"
RUN_PATH = ROOT / "tools" / "evaluation" / "run_synthetic_speech_smoke.py"


def load_module(module_name, path):
    spec = importlib.util.spec_from_file_location(module_name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


PREP = load_module("prepare_synthetic_speech_smoke", PREP_PATH)
RUN = load_module("run_synthetic_speech_smoke", RUN_PATH)


class SyntheticSpeechSmokeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.repository = self.root / "repository"
        self.repository.mkdir()
        self.write_dataset()

    def tearDown(self):
        self.temporary.cleanup()

    @staticmethod
    def write_wav(path, channels=1, sample_rate=16000, sample_width=2):
        path.parent.mkdir(parents=True, exist_ok=True)
        frames = bytes(320 * channels * sample_width)
        with wave.open(str(path), "wb") as audio:
            audio.setnchannels(channels)
            audio.setsampwidth(sample_width)
            audio.setframerate(sample_rate)
            audio.writeframes(frames)

    def make_rows(self):
        models = ["lahgtna", "chatterbox_egyptian", "egtts_v01", "habibi"]
        rows = []
        for index in range(1, 1001):
            model = models[(index - 1) % len(models)]
            case_id = f"egy_{index:04d}_{model}"
            rows.append({
                "file_name": f"audio/{case_id}.wav",
                "text": f"نص مصري تجريبي رقم {index}",
                "model": model,
                "mcd": 8.5 + (index / 1000),
            })
        return rows

    def write_dataset(self, rows=None):
        rows = rows if rows is not None else self.make_rows()
        (self.source / "README.md").write_text(
            "---\nlicense: mit\n---\n\n"
            "# Synthetic Egyptian Speech Dataset\n\n"
            "A curated dataset of 1000 Egyptian Arabic speech samples — synthetic TTS output.\n",
            encoding="utf-8",
        )
        (self.source / "metadata.jsonl").write_text(
            "".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows),
            encoding="utf-8",
        )
        for row in rows:
            self.write_wav(self.source / row["file_name"])

    def prepare(self, output_name="smoke", limit=20):
        return PREP.prepare_workspace(
            self.source,
            self.root / output_name,
            limit=limit,
            seed=20261123,
            repository_root=self.repository,
        )

    def test_prepares_balanced_synthetic_subset_without_claiming_human_consent(self):
        summary = self.prepare(limit=20)
        manifest_path = summary["manifest_path"]
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

        self.assertEqual(20, summary["sample_count"])
        self.assertEqual(1000, summary["source_rows"])
        self.assertEqual(
            {
                "chatterbox_egyptian": 5,
                "egtts_v01": 5,
                "habibi": 5,
                "lahgtna": 5,
            },
            summary["model_counts"],
        )
        self.assertEqual("synthetic-speech-smoke-only", manifest["evaluationMode"])
        self.assertEqual("not-eligible-for-WER-CER", manifest["scoreEligibility"])
        self.assertEqual("MIT", manifest["dataset"]["declaredLicense"])
        self.assertEqual(20, len(manifest["cases"]))
        self.assertEqual(20, len(list((summary["output_dir"] / "audio").glob("*.wav"))))
        self.assertTrue((summary["output_dir"] / "SOURCE_DATASET_README.md").is_file())
        self.assertFalse(any("consent" in case for case in manifest["cases"]))
        self.assertFalse(any("referenceReview" in case for case in manifest["cases"]))
        self.assertFalse(any("referenceText" in case for case in manifest["cases"]))
        self.assertTrue(all(case["locale"] == "ar-EG" for case in manifest["cases"]))
        self.assertTrue(all(case["sourceSampleRate"] == 16000 for case in manifest["cases"]))
        self.assertTrue(all(case["sampleRate"] == 16000 for case in manifest["cases"]))
        self.assertTrue(all(case["conversion"] == "none" for case in manifest["cases"]))

    def test_resamples_24khz_source_audio_and_records_both_audio_hashes(self):
        rows = self.make_rows()
        self.write_dataset(rows)
        _, _, normalized_rows = PREP._read_source(self.source)
        selected = PREP._select_balanced(normalized_rows, 4, 20261123)
        selected_source_paths = {}
        for row in selected:
            source_path = self.source / row["file_name"]
            self.write_wav(source_path, sample_rate=24000)
            selected_source_paths[row["id"]] = source_path

        summary = self.prepare(limit=4)
        manifest = json.loads(summary["manifest_path"].read_text(encoding="utf-8"))
        _, validated_samples = RUN.validate_manifest_and_audio(summary["manifest_path"])

        self.assertEqual(4, len(validated_samples))
        for case in manifest["cases"]:
            self.assertEqual(24000, case["sourceSampleRate"])
            self.assertEqual(16000, case["sampleRate"])
            self.assertEqual("binomial-5tap-linear-24000-to-16000", case["conversion"])
            self.assertEqual(
                PREP.sha256_file(selected_source_paths[case["id"]]),
                case["sourceAudioSha256"],
            )
            output_audio = summary["output_dir"] / case["audioAsset"]
            with wave.open(str(output_audio), "rb") as audio:
                self.assertEqual(16000, audio.getframerate())
                self.assertEqual(1, audio.getnchannels())
                self.assertEqual(2, audio.getsampwidth())

    def test_selection_is_deterministic_for_same_seed(self):
        first = self.prepare(output_name="smoke-a", limit=8)
        second = self.prepare(output_name="smoke-b", limit=8)
        first_manifest = json.loads(first["manifest_path"].read_text(encoding="utf-8"))
        second_manifest = json.loads(second["manifest_path"].read_text(encoding="utf-8"))
        self.assertEqual(
            [case["id"] for case in first_manifest["cases"]],
            [case["id"] for case in second_manifest["cases"]],
        )

    def test_refuses_dataset_without_mit_license_declaration(self):
        (self.source / "README.md").write_text(
            "# Synthetic Egyptian Speech Dataset\n1000 Egyptian Arabic speech samples\n",
            encoding="utf-8",
        )
        output = self.root / "blocked"
        with self.assertRaisesRegex(ValueError, "does not declare license: mit"):
            self.prepare(output_name="blocked", limit=4)
        self.assertFalse(output.exists())

    def test_rejects_metadata_path_traversal(self):
        rows = self.make_rows()
        for index, row in enumerate(rows, start=1):
            row["file_name"] = f"audio/../outside_{index:04d}.wav"
        self.write_dataset(rows)
        output = self.root / "blocked"
        with self.assertRaisesRegex(ValueError, "unsafe or unsupported audio path"):
            self.prepare(output_name="blocked", limit=4)
        self.assertFalse(output.exists())

    def test_rejects_audio_outside_required_whisper_wav_format(self):
        rows = self.make_rows()
        self.write_dataset(rows)
        _, _, normalized_rows = PREP._read_source(self.source)
        selected = PREP._select_balanced(normalized_rows, 4, 20261123)
        bad = selected[0]
        self.write_wav(
            self.source / bad["file_name"],
            channels=2,
            sample_rate=44100,
            sample_width=2,
        )
        output = self.root / "blocked"
        with self.assertRaisesRegex(ValueError, "must be uncompressed PCM WAV, mono, 16-bit"):
            self.prepare(output_name="blocked", limit=4)
        self.assertFalse(output.exists())

    def test_refuses_output_inside_repository(self):
        output = self.repository / "synthetic-audio"
        with self.assertRaisesRegex(ValueError, "outside the Git repository"):
            PREP.prepare_workspace(
                self.source, output, limit=4, repository_root=self.repository
            )
        self.assertFalse(output.exists())

    def test_smoke_manifest_validator_checks_audio_hash_and_synthetic_classification(self):
        summary = self.prepare(limit=4)
        manifest, samples = RUN.validate_manifest_and_audio(summary["manifest_path"])
        self.assertEqual(4, len(samples))
        self.assertEqual("synthetic-speech-smoke-only", manifest["evaluationMode"])
        self.assertTrue(all(sample["source_model"] in PREP.ALLOWED_MODELS for sample in samples))

        audio_path = samples[0]["audio_path"]
        audio_path.write_bytes(audio_path.read_bytes() + b"tamper")
        with self.assertRaisesRegex(ValueError, "SHA-256 does not match"):
            RUN.validate_manifest_and_audio(summary["manifest_path"])


    def test_smoke_runner_rejects_repository_output_before_creating_it(self):
        summary = self.prepare(limit=4)
        data_root = self.root / "layal-data"
        data_root.mkdir()
        output_dir = RUN.REPO_ROOT / f".synthetic-smoke-blocked-{self.root.name}"

        with mock.patch.object(RUN.RUNNER, "verify_whisper_resources", return_value={}):
            with self.assertRaisesRegex(ValueError, "outside the Git repository"):
                RUN.run_benchmark(data_root, summary["manifest_path"], output_dir)

        self.assertFalse(output_dir.exists())

    def test_smoke_runner_outputs_transcripts_without_wer_cer_or_scoring_manifest(self):
        summary = self.prepare(limit=4)
        data_root = self.root / "layal-data"
        data_root.mkdir()
        output_dir = self.root / "reports"

        def fake_transcribe(resources, sample, temporary_root, threads, timeout_seconds):
            return {
                "caseId": sample["id"],
                "locale": sample["locale"],
                "audioSha256": sample["audio_sha256"],
                "audioDurationSeconds": sample["audio_duration_seconds"],
                "wallClockMs": 25.0,
                "realTimeFactor": 0.25,
                "transcript": "تفريغ تجريبي",
                "status": "passed",
            }

        with (
            mock.patch.object(RUN.RUNNER, "verify_whisper_resources", return_value={
                "archive_sha256": "a" * 64,
                "model_sha256": "b" * 64,
                "executable_sha256": "c" * 64,
            }),
            mock.patch.object(RUN.RUNNER, "transcribe_one", side_effect=fake_transcribe),
        ):
            report_path, report = RUN.run_benchmark(
                data_root,
                summary["manifest_path"],
                output_dir,
            )

        self.assertTrue(report_path.is_file())
        self.assertEqual(4, report["summary"]["passedSamples"])
        self.assertEqual("not-eligible-for-WER-CER", report["scoreEligibility"])
        self.assertIsNone(report["metrics"]["wer"])
        self.assertIsNone(report["metrics"]["cer"])
        self.assertTrue(all("promptText" in sample for sample in report["samples"]))
        self.assertTrue(all("sourceModel" in sample for sample in report["samples"]))
        self.assertFalse(list(output_dir.glob("*.scoring-manifest.json")))


if __name__ == "__main__":
    unittest.main()
