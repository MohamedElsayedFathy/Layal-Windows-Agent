import importlib.util
import json
import sys
import tempfile
import wave
import struct
from array import array
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

    @staticmethod
    def write_float_wav(path, sample_rate=16000, samples=None):
        path.parent.mkdir(parents=True, exist_ok=True)
        if samples is None:
            samples = [0.0] * 320
        frames = struct.pack("<" + "f" * len(samples), *samples)
        fmt = struct.pack("<HHIIHH", 3, 1, sample_rate, sample_rate * 4, 4, 32)
        chunks = (
            b"fmt " + struct.pack("<I", len(fmt)) + fmt
            + b"data" + struct.pack("<I", len(frames)) + frames
        )
        riff = b"WAVE" + chunks
        path.write_bytes(b"RIFF" + struct.pack("<I", len(riff)) + riff)

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

    def test_normalizes_ieee_float32_wav_sources_and_resamples_24khz(self):
        rows = self.make_rows()
        self.write_dataset(rows)
        _, _, normalized_rows = PREP._read_source(self.source)
        selected = PREP._select_balanced(normalized_rows, 4, 20261123)
        float_16k = selected[0]
        float_24k = selected[1]
        pattern = [-1.0, -0.5, 0.0, 0.5, 1.0]
        self.write_float_wav(
            self.source / float_16k["file_name"],
            sample_rate=16000,
            samples=pattern * 64,
        )
        self.write_float_wav(
            self.source / float_24k["file_name"],
            sample_rate=24000,
            samples=pattern * 96,
        )

        summary = self.prepare(limit=4)
        manifest = json.loads(summary["manifest_path"].read_text(encoding="utf-8"))
        cases = {case["id"]: case for case in manifest["cases"]}
        _, runner_samples = RUN.validate_manifest_and_audio(summary["manifest_path"])
        self.assertEqual(4, len(runner_samples))
        runner_by_id = {sample["id"]: sample for sample in runner_samples}

        self.assertEqual("ieee-float32", cases[float_16k["id"]]["sourceSampleFormat"])
        self.assertEqual("ieee-float32", runner_by_id[float_16k["id"]]["source_sample_format"])
        self.assertEqual("ieee-float32", runner_by_id[float_24k["id"]]["source_sample_format"])
        self.assertEqual("ieee-float32-to-pcm16", cases[float_16k["id"]]["conversion"])
        self.assertEqual("ieee-float32", cases[float_24k["id"]]["sourceSampleFormat"])
        self.assertEqual(
            "ieee-float32-to-pcm16-binomial-5tap-linear-24000-to-16000",
            cases[float_24k["id"]]["conversion"],
        )

        for case_id in (float_16k["id"], float_24k["id"]):
            output_audio = summary["output_dir"] / cases[case_id]["audioAsset"]
            with wave.open(str(output_audio), "rb") as audio:
                self.assertEqual("NONE", audio.getcomptype())
                self.assertEqual(1, audio.getnchannels())
                self.assertEqual(16000, audio.getframerate())
                self.assertEqual(2, audio.getsampwidth())

        output_16k = summary["output_dir"] / cases[float_16k["id"]]["audioAsset"]
        with wave.open(str(output_16k), "rb") as audio:
            converted = array("h")
            converted.frombytes(audio.readframes(audio.getnframes()))
        self.assertEqual([-32768, -16384, 0, 16384, 32767], list(converted[:5]))

        original = self.source / float_16k["file_name"]
        self.assertEqual(b"\x03\x00", original.read_bytes()[20:22])
        self.assertEqual(
            PREP.sha256_file(original),
            cases[float_16k["id"]]["sourceAudioSha256"],
        )

    def test_rejects_non_finite_ieee_float_samples_without_creating_output(self):
        rows = self.make_rows()
        self.write_dataset(rows)
        _, _, normalized_rows = PREP._read_source(self.source)
        selected = PREP._select_balanced(normalized_rows, 4, 20261123)
        bad = selected[0]
        self.write_float_wav(
            self.source / bad["file_name"],
            sample_rate=16000,
            samples=[float("nan")] * 320,
        )

        output = self.root / "blocked-float"
        with self.assertRaisesRegex(ValueError, "non-finite sample"):
            PREP.prepare_workspace(
                self.source, output, limit=4, seed=20261123, repository_root=self.repository
            )
        self.assertFalse(output.exists())

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

    def test_runner_keeps_legacy_pcm_manifest_without_source_sample_format_compatible(self):
        summary = self.prepare(limit=4)
        manifest_path = summary["manifest_path"]
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        for case in manifest["cases"]:
            case.pop("sourceSampleFormat", None)
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False), encoding="utf-8")

        _, samples = RUN.validate_manifest_and_audio(manifest_path)
        self.assertEqual(4, len(samples))
        self.assertTrue(all(sample["source_sample_format"] == "pcm-s16le" for sample in samples))

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
        with self.assertRaisesRegex(ValueError, "must be mono"):
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
            }) as verify_resources,
            mock.patch.object(RUN.RUNNER, "transcribe_one", side_effect=fake_transcribe) as transcribe,
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
        self.assertTrue(all(sample["sourceSampleFormat"] == "pcm-s16le" for sample in report["samples"]))
        self.assertFalse(list(output_dir.glob("*.scoring-manifest.json")))
        self.assertEqual(5, verify_resources.call_count)  # initial pin + one verification per sample
        self.assertEqual(4, transcribe.call_count)

    def test_smoke_runner_stops_after_whisper_resource_integrity_changes(self):
        summary = self.prepare(limit=4)
        data_root = self.root / "layal-data"
        data_root.mkdir()
        output_dir = self.root / "reports"

        pinned = {
            "archive_sha256": "a" * 64,
            "model_sha256": "b" * 64,
            "executable_sha256": "c" * 64,
        }
        changed = {**pinned, "model_sha256": "d" * 64}
        transcribe = mock.Mock(return_value={
            "caseId": "placeholder",
            "locale": "ar-EG",
            "audioSha256": "e" * 64,
            "audioDurationSeconds": 1.0,
            "wallClockMs": 20.0,
            "realTimeFactor": 0.02,
            "transcript": "تفريغ تجريبي",
            "status": "passed",
        })

        with (
            mock.patch.object(
                RUN.RUNNER, "verify_whisper_resources", side_effect=[pinned, pinned, changed]
            ) as verify_resources,
            mock.patch.object(RUN.RUNNER, "transcribe_one", side_effect=transcribe),
        ):
            report_path, report = RUN.run_benchmark(
                data_root, summary["manifest_path"], output_dir
            )

        self.assertTrue(report_path.is_file())
        self.assertEqual(1, report["summary"]["passedSamples"])
        self.assertEqual(3, report["summary"]["failedSamples"])
        self.assertEqual(3, len(report["failures"]))
        self.assertIn("changed during the synthetic smoke run", report["failures"][0]["error"])
        self.assertIn("Skipped because", report["failures"][1]["error"])
        self.assertEqual(3, verify_resources.call_count)  # initial pin, sample 1, sample 2
        self.assertEqual(1, transcribe.call_count)


import csv
import importlib.util

REVIEW_PATH = ROOT / "tools" / "evaluation" / "export_synthetic_speech_review.py"
REVIEW_SPEC = importlib.util.spec_from_file_location("export_synthetic_speech_review", REVIEW_PATH)
REVIEW = importlib.util.module_from_spec(REVIEW_SPEC)
assert REVIEW_SPEC and REVIEW_SPEC.loader
sys.modules[REVIEW_SPEC.name] = REVIEW
REVIEW_SPEC.loader.exec_module(REVIEW)


class SyntheticSpeechReviewExportTests(unittest.TestCase):
    def setUp(self):
        self.fixture = SyntheticSpeechSmokeTests()
        self.fixture.setUp()
        summary = self.fixture.prepare(limit=4)
        self.manifest_path = summary["manifest_path"]
        self.manifest = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        self.report_path = self.fixture.root / "synthetic-report.json"
        self.write_report()

    def tearDown(self):
        self.fixture.tearDown()

    def write_report(self, wer=None, cer=None, tamper_case=None):
        samples = []
        for case in self.manifest["cases"]:
            audio_hash = "0" * 64 if case["id"] == tamper_case else case["audioSha256"]
            samples.append({
                "caseId": case["id"],
                "locale": "ar-EG",
                "audioSha256": audio_hash,
                "sourceAudioSha256": case["sourceAudioSha256"],
                "audioDurationSeconds": case["audioDurationSeconds"],
                "sourceModel": case["sourceModel"],
                "promptText": case["promptText"],
                "transcript": "تفريغ اختباري",
                "status": "passed",
            })
        self.report = {
            "schemaVersion": 1,
            "benchmarkType": "synthetic-speech-smoke",
            "scoreEligibility": "not-eligible-for-WER-CER",
            "dataset": self.manifest["dataset"],
            "summary": {
                "eligibleSamples": len(self.manifest["cases"]),
                "passedSamples": len(samples),
                "failedSamples": 0,
            },
            "samples": samples,
            "failures": [],
            "metrics": {"wer": wer, "cer": cer},
        }
        self.report_path.write_text(json.dumps(self.report, ensure_ascii=False), encoding="utf-8")

    def test_exports_local_qualitative_review_sheet_without_scoring_manifest(self):
        output = self.fixture.root / "review.csv"
        csv_path, guide_path, count = REVIEW.export_review_sheet(
            self.manifest_path, self.report_path, output
        )
        self.assertEqual(4, count)
        self.assertTrue(csv_path.is_file())
        self.assertTrue(guide_path.is_file())
        with csv_path.open("r", encoding="utf-8-sig", newline="") as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual(4, len(rows))
        self.assertEqual("NOT_REVIEWED", rows[0]["manual_review_status"])
        self.assertEqual("تفريغ اختباري", rows[0]["whisper_transcript"])
        self.assertTrue(rows[0]["audio_asset_relative_to_manifest"].startswith("audio/"))
        self.assertEqual("", rows[0]["reviewer_heard_text_non_reference"])
        self.assertFalse(list(self.fixture.root.glob("*.scoring-manifest.json")))
        guide = guide_path.read_text(encoding="utf-8")
        self.assertIn("ليس مرجعًا ذهبيًا", guide)
        self.assertIn("لا تحسب WER أو CER", guide)

    def test_refuses_any_report_that_contains_wer_or_cer(self):
        self.write_report(wer=0.12)
        with self.assertRaisesRegex(ValueError, "refuses reports containing WER/CER"):
            REVIEW.export_review_sheet(
                self.manifest_path, self.report_path, self.fixture.root / "blocked.csv"
            )
        self.assertFalse((self.fixture.root / "blocked.csv").exists())

    def test_refuses_report_audio_hash_that_does_not_match_manifest(self):
        case_id = self.manifest["cases"][0]["id"]
        self.write_report(tamper_case=case_id)
        with self.assertRaisesRegex(ValueError, "audio hash does not match"):
            REVIEW.export_review_sheet(
                self.manifest_path, self.report_path, self.fixture.root / "blocked.csv"
            )
        self.assertFalse((self.fixture.root / "blocked.csv").exists())

    def test_refuses_review_output_inside_repository(self):
        output = REVIEW.REPO_ROOT / ".synthetic-review-blocked.csv"
        with self.assertRaisesRegex(ValueError, "outside the Git repository"):
            REVIEW.export_review_sheet(self.manifest_path, self.report_path, output)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
