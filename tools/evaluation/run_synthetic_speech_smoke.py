#!/usr/bin/env python3
"""Run Layal's pinned local Whisper against a synthetic-only Egyptian speech smoke subset.

This runner intentionally does not create a WER/CER scoring manifest. The dataset's
prompt text is not a human-verified transcript and must not enter the gold-corpus scorer.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import statistics
import sys
import tempfile
import wave
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any

RUNNER_PATH = Path(__file__).resolve().with_name("run_whisper_benchmark.py")
RUNNER_SPEC = importlib.util.spec_from_file_location("layal_whisper_benchmark_for_synthetic_smoke", RUNNER_PATH)
if RUNNER_SPEC is None or RUNNER_SPEC.loader is None:
    raise RuntimeError(f"Cannot load the shared Whisper benchmark helpers from {RUNNER_PATH}.")
RUNNER = importlib.util.module_from_spec(RUNNER_SPEC)
sys.modules[RUNNER_SPEC.name] = RUNNER
RUNNER_SPEC.loader.exec_module(RUNNER)

REPO_ROOT = Path(__file__).resolve().parents[2]
DATASET_REPO_ID = "MohamedGomaa30/Synthetic-Egy-Speech-Dataset"
DATASET_REVISION = "bf7ff28bd1737bc78d154ee9f9180a701f88cd1e"
ENGINE_ID = RUNNER.ENGINE_ID
CASE_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
MAX_SAMPLES = 40


def validate_manifest_and_audio(manifest_path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    RUNNER._require_plain_file(manifest_path, "Synthetic speech smoke manifest")
    try:
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Cannot read synthetic speech smoke manifest: {error}") from error

    if not isinstance(payload, dict) or payload.get("schemaVersion") != 1:
        raise ValueError("Synthetic smoke manifest schemaVersion must be 1.")
    if payload.get("evaluationMode") != "synthetic-speech-smoke-only":
        raise ValueError("Manifest is not explicitly marked as a synthetic-only smoke evaluation.")
    if payload.get("scoreEligibility") != "not-eligible-for-WER-CER":
        raise ValueError("Synthetic smoke manifests must be ineligible for WER/CER scoring.")

    dataset = payload.get("dataset")
    if not isinstance(dataset, dict):
        raise ValueError("Synthetic smoke manifest requires dataset provenance metadata.")
    if dataset.get("repoId") != DATASET_REPO_ID or dataset.get("revision") != DATASET_REVISION:
        raise ValueError("Synthetic smoke manifest source repository or pinned revision does not match.")
    if dataset.get("declaredLicense") != "MIT" or dataset.get("audioKind") != "synthetic TTS output, not human-recorded speech":
        raise ValueError("Synthetic smoke manifest is missing its declared licence or synthetic-audio marker.")

    cases = payload.get("cases")
    if not isinstance(cases, list) or not 1 <= len(cases) <= MAX_SAMPLES:
        raise ValueError(f"Synthetic smoke manifest must contain between 1 and {MAX_SAMPLES} samples.")

    manifest_root = manifest_path.resolve(strict=True).parent
    audio_root = manifest_root / "audio"
    if RUNNER._is_reparse_point(audio_root) or not audio_root.is_dir():
        raise ValueError("Synthetic smoke audio directory is missing or unsafe.")
    resolved_audio_root = audio_root.resolve(strict=True)

    seen_ids: set[str] = set()
    samples: list[dict[str, Any]] = []
    for case in cases:
        if not isinstance(case, dict):
            raise ValueError("Every synthetic smoke case must be a JSON object.")
        case_id = case.get("id")
        if not isinstance(case_id, str) or not CASE_ID_PATTERN.fullmatch(case_id) or case_id in seen_ids:
            raise ValueError("Synthetic smoke case IDs must be unique, non-identifying filename tokens.")
        seen_ids.add(case_id)

        if case.get("locale") != "ar-EG":
            raise ValueError(f"Case {case_id} has an unsupported locale; expected ar-EG.")
        prompt_text = case.get("promptText")
        model = case.get("sourceModel")
        source_sample_rate = case.get("sourceSampleRate")
        source_sample_format = case.get("sourceSampleFormat", "pcm-s16le")
        benchmark_sample_rate = case.get("sampleRate")
        source_audio_hash = case.get("sourceAudioSha256")
        conversion = case.get("conversion")
        if source_sample_rate not in (16000, 24000) or benchmark_sample_rate != 16000:
            raise ValueError(f"Case {case_id} has unsupported source or benchmark sample-rate provenance.")
        if source_sample_format not in ("pcm-s16le", "ieee-float32"):
            raise ValueError(f"Case {case_id} has unsupported source sample-format provenance.")
        if not isinstance(source_audio_hash, str) or not re.fullmatch(r"[a-f0-9]{64}", source_audio_hash):
            raise ValueError(f"Case {case_id} has an invalid source audio SHA-256.")
        conversion_markers = {
            (16000, "pcm-s16le"): "none",
            (24000, "pcm-s16le"): "binomial-5tap-linear-24000-to-16000",
            (16000, "ieee-float32"): "ieee-float32-to-pcm16",
            (24000, "ieee-float32"): (
                "ieee-float32-to-pcm16-binomial-5tap-linear-24000-to-16000"
            ),
        }
        expected_conversion = conversion_markers[(source_sample_rate, source_sample_format)]
        if conversion != expected_conversion:
            raise ValueError(f"Case {case_id} has an unexpected sample-format or sample-rate conversion marker.")
        if not isinstance(prompt_text, str) or not prompt_text.strip():
            raise ValueError(f"Case {case_id} has no promptText.")
        if model not in {"lahgtna", "chatterbox_egyptian", "egtts_v01", "habibi"}:
            raise ValueError(f"Case {case_id} has an unsupported sourceModel.")

        audio_asset = case.get("audioAsset")
        if not isinstance(audio_asset, str) or "\\" in audio_asset:
            raise ValueError(f"Case {case_id} has an invalid audioAsset path.")
        relative = PurePosixPath(audio_asset)
        if (
            relative.is_absolute()
            or len(relative.parts) != 2
            or relative.parts[0] != "audio"
            or relative.suffix.lower() != ".wav"
            or any(part in {"", ".", ".."} for part in relative.parts)
        ):
            raise ValueError(f"Case {case_id} audioAsset must be a WAV directly below audio/.")

        current_path = manifest_root
        for segment in relative.parts:
            current_path = current_path / segment
            if RUNNER._is_reparse_point(current_path):
                raise ValueError(f"Case {case_id} audio path contains a reparse point.")
        audio_path = current_path.resolve(strict=True)
        if not audio_path.is_relative_to(resolved_audio_root):
            raise ValueError(f"Case {case_id} audio resolves outside the smoke audio directory.")
        RUNNER._require_plain_file(audio_path, f"Audio for case {case_id}")

        expected_hash = case.get("audioSha256")
        actual_hash = RUNNER.sha256_file(audio_path)
        if not isinstance(expected_hash, str) or not re.fullmatch(r"[a-f0-9]{64}", expected_hash):
            raise ValueError(f"Case {case_id} has an invalid audio SHA-256.")
        if actual_hash != expected_hash:
            raise ValueError(f"Case {case_id} audio SHA-256 does not match the manifest.")

        if audio_path.stat().st_size <= 44 or audio_path.stat().st_size > RUNNER.MAX_AUDIO_BYTES:
            raise ValueError(f"Case {case_id} WAV size is outside the accepted range.")
        try:
            with wave.open(str(audio_path), "rb") as audio:
                channels = audio.getnchannels()
                sample_rate = audio.getframerate()
                sample_width = audio.getsampwidth()
                compression = audio.getcomptype()
                duration = audio.getnframes() / float(sample_rate)
        except (wave.Error, EOFError, OSError) as error:
            raise ValueError(f"Case {case_id} is not a readable WAV: {error}") from error
        if compression != "NONE" or (channels, sample_rate, sample_width) != (1, 16000, 2):
            raise ValueError(f"Case {case_id} WAV must be uncompressed PCM, mono, 16 kHz, and 16-bit.")
        if duration <= 0 or duration > RUNNER.MAX_AUDIO_SECONDS:
            raise ValueError(f"Case {case_id} duration must be greater than zero and at most 120 seconds.")

        samples.append({
            "id": case_id,
            "locale": "ar-EG",
            "prompt_text": prompt_text,
            "source_model": model,
            "source_sample_rate": source_sample_rate,
            "source_sample_format": source_sample_format,
            "source_audio_sha256": source_audio_hash,
            "benchmark_sample_rate": benchmark_sample_rate,
            "conversion": conversion,
            "audio_path": audio_path,
            "audio_sha256": actual_hash,
            "audio_duration_seconds": duration,
        })

    return payload, samples


def run_benchmark(
    data_root: Path,
    manifest_path: Path,
    output_dir: Path,
    prefix: str = "whisper-synthetic-smoke",
    threads: int = 4,
    timeout_seconds: int = RUNNER.DEFAULT_TIMEOUT_SECONDS,
    force: bool = False,
) -> tuple[Path, dict[str, Any]]:
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", prefix):
        raise ValueError("prefix may contain only letters, digits, dashes, and underscores.")
    if not 1 <= threads <= 8:
        raise ValueError("threads must be between 1 and 8.")
    if not 10 <= timeout_seconds <= 3600:
        raise ValueError("timeout_seconds must be between 10 and 3600.")

    manifest, samples = validate_manifest_and_audio(manifest_path)

    requested_output = output_dir.expanduser().absolute()
    for component in (requested_output, *requested_output.parents):
        if RUNNER._is_reparse_point(component):
            raise ValueError("Synthetic smoke reports must not contain symbolic links or Windows reparse points.")

    output_root = requested_output.resolve()
    if output_root == REPO_ROOT or output_root.is_relative_to(REPO_ROOT):
        raise ValueError("Synthetic smoke reports must be stored outside the Git repository.")

    # Pin the verified resources for this run, then re-verify them before every sample.
    resources = RUNNER.verify_whisper_resources(data_root)
    resource_hash_keys = ("archive_sha256", "model_sha256", "executable_sha256")
    resource_fingerprint = tuple(resources.get(key) for key in resource_hash_keys)
    if not all(isinstance(value, str) and re.fullmatch(r"[a-f0-9]{64}", value) for value in resource_fingerprint):
        raise RuntimeError("Whisper resource verification did not return all pinned SHA-256 digests.")

    output_parent = output_root.parent
    output_parent.mkdir(parents=True, exist_ok=True)
    if RUNNER._is_reparse_point(output_parent):
        raise ValueError("Synthetic smoke output parent must not be a reparse point.")
    output_root.mkdir(parents=True, exist_ok=True)
    if RUNNER._is_reparse_point(output_root):
        raise ValueError("Synthetic smoke output directory must not be a reparse point.")
    output_root = output_root.resolve(strict=True)

    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + os.urandom(4).hex()
    report_path = output_root / f"{prefix}-{run_id}.json"
    results: list[dict[str, Any]] = []
    failures: list[dict[str, str]] = []

    with tempfile.TemporaryDirectory(prefix="layal-synthetic-smoke-") as temporary:
        temporary_root = Path(temporary)
        for sample_index, sample in enumerate(samples):
            # A resource verification failure stops the run; do not use a changed runtime/model.
            try:
                current_resources = RUNNER.verify_whisper_resources(data_root)
                current_fingerprint = tuple(current_resources.get(key) for key in resource_hash_keys)
                if current_fingerprint != resource_fingerprint:
                    raise RuntimeError("Pinned Whisper resources changed during the synthetic smoke run.")
            except Exception as error:
                safe_message = RUNNER._redact_local_paths(
                    str(error), [manifest_path, manifest_path.parent, data_root, output_root]
                )
                failures.append({"caseId": sample["id"], "error": safe_message[-1600:]})
                for blocked_sample in samples[sample_index + 1:]:
                    failures.append({
                        "caseId": blocked_sample["id"],
                        "error": "Skipped because Whisper resource integrity verification failed before this sample.",
                    })
                break

            try:
                result = RUNNER.transcribe_one(
                    current_resources, sample, temporary_root, threads, timeout_seconds
                )
                result["sourceModel"] = sample["source_model"]
                result["sourceSampleRate"] = sample["source_sample_rate"]
                result["sourceSampleFormat"] = sample["source_sample_format"]
                result["benchmarkSampleRate"] = sample["benchmark_sample_rate"]
                result["sourceAudioSha256"] = sample["source_audio_sha256"]
                result["conversion"] = sample["conversion"]
                result["promptText"] = sample["prompt_text"]
                results.append(result)
            except Exception as error:
                safe_message = RUNNER._redact_local_paths(
                    str(error), [manifest_path, manifest_path.parent, data_root, output_root]
                )
                failures.append({"caseId": sample["id"], "error": safe_message[-1600:]})

    elapsed = [item["wallClockMs"] for item in results]
    rtf_values = [item["realTimeFactor"] for item in results]
    report = {
        "schemaVersion": 1,
        "benchmarkType": "synthetic-speech-smoke",
        "scoreEligibility": "not-eligible-for-WER-CER",
        "createdAtUtc": datetime.now(timezone.utc).isoformat(),
        "engine": ENGINE_ID,
        "whisperRuntimeArchiveSha256": resources["archive_sha256"],
        "whisperModelSha256": resources["model_sha256"],
        "whisperExecutableSha256": resources["executable_sha256"],
        "dataset": manifest["dataset"],
        "selection": manifest["selection"],
        "limitations": manifest["limitations"],
        "summary": {
            "eligibleSamples": len(samples),
            "passedSamples": len(results),
            "failedSamples": len(failures),
            "medianWallClockMs": round(statistics.median(elapsed), 2) if elapsed else None,
            "medianRealTimeFactor": round(statistics.median(rtf_values), 6) if rtf_values else None,
        },
        "samples": results,
        "failures": failures,
        "metrics": {
            "wer": None,
            "cer": None,
            "reason": "Synthetic prompt text is not an independently human-verified transcript.",
        },
    }

    RUNNER._write_json_atomically(report_path, report, force)
    return report_path, report


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-root", type=Path, required=True,
                        help="Layal LocalAi data directory containing the pinned Whisper resources.")
    parser.add_argument("--manifest", type=Path, required=True,
                        help="synthetic-smoke-manifest.json created by prepare_synthetic_speech_smoke.py.")
    parser.add_argument("--output-dir", type=Path,
                        help="Local report directory; defaults to <data-root>/evaluation/whisper-synthetic-smoke.")
    parser.add_argument("--prefix", default="whisper-synthetic-smoke")
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--timeout-seconds", type=int, default=RUNNER.DEFAULT_TIMEOUT_SECONDS)
    parser.add_argument("--force", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    output_dir = args.output_dir or (args.data_root / "evaluation" / "whisper-synthetic-smoke")
    try:
        report_path, report = run_benchmark(
            args.data_root, args.manifest, output_dir,
            args.prefix, args.threads, args.timeout_seconds, args.force,
        )
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        print(f"Synthetic Whisper smoke test blocked: {error}", file=sys.stderr)
        return 2

    summary = report["summary"]
    print(f"Report: {report_path}")
    print(
        "Completed: "
        f"{summary['passedSamples']}/{summary['eligibleSamples']} samples; "
        f"{summary['failedSamples']} failed."
    )
    print("WER/CER: NOT CALCULATED (synthetic smoke subset is not gold data).")
    return 0 if summary["failedSamples"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
