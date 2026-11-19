#!/usr/bin/env python3
"""Run Layal's pinned local Whisper.cpp build against consented, human-reviewed WAVs.

The runner never downloads resources, uploads audio, edits the source manifest, or
starts a shell. It verifies the exact runtime archive contents and model digest
before launching the same whisper-cli command shape used by LocalVoiceService.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import io
import importlib.util
import json
import os
import platform
import re
import shutil
import stat
import statistics
import subprocess
import sys
import tempfile
import time
import wave
import zipfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any

WHISPER_ARCHIVE_NAME = "whisper-bin-x64-v1.9.1.zip"
WHISPER_ARCHIVE_SIZE = 7_982_101
WHISPER_ARCHIVE_SHA256 = "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539"
WHISPER_MODEL_RELATIVE_PATH = Path("speech-models") / "ggml-base.bin"
WHISPER_MODEL_SIZE = 147_951_465
WHISPER_MODEL_SHA256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"
WHISPER_INSTALL_RELATIVE_PATH = Path("tools") / "whisper"
ENGINE_ID = "whisper_cpp_base"
MAX_AUDIO_BYTES = 64 * 1024 * 1024
MAX_AUDIO_SECONDS = 120.0
DEFAULT_TIMEOUT_SECONDS = 300
PSEUDONYMOUS_SPEAKER_PATTERN = re.compile(r"^spk-[a-z0-9][a-z0-9-]{2,30}$")
CASE_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")


def _load_scorer():
    """Load the repository's consent/manifest/WAV validator without duplicating its policy."""
    module_path = Path(__file__).resolve().with_name("score_speech_transcripts.py")
    spec = importlib.util.spec_from_file_location("layal_speech_transcript_scoring", module_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load the transcript scoring validator at {module_path}.")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


SCORER = _load_scorer()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _is_reparse_point(path: Path) -> bool:
    try:
        metadata = path.lstat()
    except FileNotFoundError:
        return False
    if stat.S_ISLNK(metadata.st_mode):
        return True
    attributes = getattr(metadata, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return bool(attributes & reparse_flag)


def _require_plain_file(path: Path, label: str) -> None:
    if _is_reparse_point(path):
        raise ValueError(f"{label} must not be a symbolic link or Windows reparse point: {path}")
    if not path.is_file():
        raise FileNotFoundError(f"{label} was not found: {path}")


def _require_no_reparse_components(root: Path, path: Path, label: str) -> None:
    current = root
    if _is_reparse_point(current):
        raise ValueError(f"{label} root must not be a reparse point: {current}")
    for segment in path.relative_to(root).parts:
        current = current / segment
        if _is_reparse_point(current):
            raise ValueError(f"{label} path contains a symbolic link or Windows reparse point: {current}")


def _validate_archive_member(name: str) -> str:
    value = name.replace("\\", "/")
    member = PurePosixPath(value)
    if (
        not value
        or member.is_absolute()
        or ":" in value
        or any(part in {"", ".", ".."} for part in value.split("/"))
    ):
        raise ValueError(f"Unsafe path in the pinned Whisper archive: {name!r}")
    return member.as_posix()


def verify_whisper_resources(data_root: Path) -> dict[str, Any]:
    """Verify the shipped archive, every extracted runtime file, and the model bytes."""
    root = data_root.resolve(strict=True)
    if _is_reparse_point(data_root):
        raise ValueError("Layal data root must not be a reparse point.")
    archive_path = root / WHISPER_ARCHIVE_NAME
    install_root = root / WHISPER_INSTALL_RELATIVE_PATH
    model_path = root / WHISPER_MODEL_RELATIVE_PATH

    _require_no_reparse_components(root, archive_path, "Whisper archive")
    _require_no_reparse_components(root, install_root, "Whisper installation")
    _require_no_reparse_components(root, model_path, "Whisper model")
    _require_plain_file(archive_path, "Pinned Whisper runtime archive")
    archive_bytes = archive_path.read_bytes()
    if len(archive_bytes) != WHISPER_ARCHIVE_SIZE:
        raise ValueError("Whisper runtime archive size does not match the pinned Layal resource.")
    archive_digest = hashlib.sha256(archive_bytes).hexdigest()
    if archive_digest != WHISPER_ARCHIVE_SHA256:
        raise ValueError("Whisper runtime archive SHA-256 does not match the pinned Layal resource.")

    _require_plain_file(model_path, "Pinned Whisper model")
    if model_path.stat().st_size != WHISPER_MODEL_SIZE:
        raise ValueError("Whisper model size does not match the pinned Layal resource.")
    model_digest = sha256_file(model_path)
    if model_digest != WHISPER_MODEL_SHA256:
        raise ValueError("Whisper model SHA-256 does not match the pinned Layal resource.")

    if _is_reparse_point(install_root) or not install_root.is_dir():
        raise ValueError(f"Verified Whisper installation directory is missing or unsafe: {install_root}")

    expected: dict[str, zipfile.ZipInfo] = {}
    executable_members: list[str] = []
    with zipfile.ZipFile(io.BytesIO(archive_bytes), "r") as archive:
        for info in archive.infolist():
            if info.is_dir():
                continue
            member = _validate_archive_member(info.filename)
            unix_mode = (info.external_attr >> 16) & 0xF000
            if unix_mode == 0xA000:
                raise ValueError(f"Whisper archive contains a symbolic link: {info.filename}")
            if member in expected:
                raise ValueError(f"Whisper archive contains a duplicate path: {member}")
            expected[member] = info
            if PurePosixPath(member).name.casefold() == "whisper-cli.exe":
                executable_members.append(member)

        if not expected or len(expected) > 512 or len(executable_members) != 1:
            raise ValueError("Whisper archive layout is not the expected Layal runtime layout.")

        actual: dict[str, Path] = {}
        for directory, subdirectories, filenames in os.walk(install_root, followlinks=False):
            directory_path = Path(directory)
            if _is_reparse_point(directory_path):
                raise ValueError(f"Whisper install contains a reparse-point directory: {directory_path}")
            for name in list(subdirectories):
                child = directory_path / name
                if _is_reparse_point(child):
                    raise ValueError(f"Whisper install contains a reparse-point directory: {child}")
            for name in filenames:
                path = directory_path / name
                if _is_reparse_point(path) or not path.is_file():
                    raise ValueError(f"Whisper install contains an unsafe file: {path}")
                relative = path.relative_to(install_root).as_posix()
                if relative in actual:
                    raise ValueError(f"Whisper install contains a duplicate path: {relative}")
                actual[relative] = path

        if actual.keys() != expected.keys():
            missing = sorted(expected.keys() - actual.keys())
            extra = sorted(actual.keys() - expected.keys())
            raise ValueError(f"Whisper install differs from its pinned archive (missing={missing}, extra={extra}).")

        for member, info in expected.items():
            path = actual[member]
            if path.stat().st_size != info.file_size:
                raise ValueError(f"Extracted Whisper file has an unexpected size: {member}")
            installed_digest = sha256_file(path)
            with archive.open(info, "r") as source:
                archived_digest = hashlib.sha256()
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    archived_digest.update(block)
            if installed_digest != archived_digest.hexdigest():
                raise ValueError(f"Extracted Whisper file does not match the pinned archive: {member}")

    executable = install_root.joinpath(*PurePosixPath(executable_members[0]).parts)
    _require_plain_file(executable, "Verified Whisper executable")
    return {
        "executable": executable,
        "model": model_path,
        "archive_sha256": archive_digest,
        "model_sha256": model_digest,
        "executable_sha256": sha256_file(executable),
        "archive_size_bytes": archive_path.stat().st_size,
        "model_size_bytes": model_path.stat().st_size,
    }


def validate_manifest_and_audio(manifest_path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    _require_plain_file(manifest_path, "Speech evaluation manifest")
    try:
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Cannot read speech evaluation manifest: {error}") from error

    errors = SCORER.validate_manifest(payload)
    if errors:
        raise ValueError("Speech manifest validation failed:\n- " + "\n- ".join(errors))
    audio_errors = SCORER.validate_audio_files(payload, manifest_path)
    if audio_errors:
        raise ValueError("Speech audio validation failed:\n- " + "\n- ".join(audio_errors))

    cases = []
    for case in payload["cases"]:
        if not SCORER._is_scoring_eligible(case):
            continue
        if not isinstance(case.get("id"), str) or not CASE_ID_PATTERN.fullmatch(case["id"]):
            raise ValueError("Recorded case IDs must be non-identifying tokens containing only letters, digits, dashes, or underscores.")
        speaker_id = case.get("speakerId", "")
        if not isinstance(speaker_id, str) or not PSEUDONYMOUS_SPEAKER_PATTERN.fullmatch(speaker_id):
            raise ValueError(f"Case {case['id']} has a non-pseudonymous speaker identifier.")
        audio_root = manifest_path.parent / "audio"
        if _is_reparse_point(audio_root):
            raise ValueError("Speech audio directory must not be a symbolic link or Windows reparse point.")
        audio_path = audio_root.joinpath(*PurePosixPath(case["audioAsset"]).parts[1:])
        current_path = audio_root
        for segment in PurePosixPath(case["audioAsset"]).parts[1:]:
            current_path = current_path / segment
            if _is_reparse_point(current_path):
                raise ValueError(f"Case {case['id']} audio path contains a reparse point.")
        audio_path = audio_path.resolve(strict=True)
        if not audio_path.is_relative_to(audio_root.resolve(strict=True)):
            raise ValueError(f"Case {case['id']} resolves outside the manifest audio directory.")
        if audio_path.stat().st_size <= 44 or audio_path.stat().st_size > MAX_AUDIO_BYTES:
            raise ValueError(f"Case {case['id']} WAV size must be greater than 44 bytes and no more than 64 MiB.")
        with wave.open(str(audio_path), "rb") as wav:
            duration = wav.getnframes() / float(wav.getframerate())
            if duration <= 0 or duration > MAX_AUDIO_SECONDS:
                raise ValueError(
                    f"Case {case['id']} duration must be greater than zero and at most {MAX_AUDIO_SECONDS:g} seconds."
                )
        cases.append({
            "id": case["id"],
            "locale": case["locale"],
            "speaker_id": speaker_id,
            "reference_text": case["referenceText"],
            "audio_path": audio_path,
            "audio_sha256": sha256_file(audio_path),
            "audio_duration_seconds": duration,
        })

    if not cases:
        raise ValueError(
            "No eligible recordings were found. Add recorded WAV files with explicit research-evaluation consent "
            "and human-approved reference transcripts; transcript-only starter rows are not benchmark samples."
        )
    return payload, cases


def _extract_transcript(json_path: Path, text_path: Path) -> str:
    if json_path.is_file():
        try:
            payload = json.loads(json_path.read_text(encoding="utf-8-sig"))
            segments = payload.get("transcription") if isinstance(payload, dict) else None
            if isinstance(segments, list):
                transcript = " ".join(
                    item.get("text", "").strip()
                    for item in segments
                    if isinstance(item, dict) and isinstance(item.get("text"), str) and item["text"].strip()
                ).strip()
                if transcript:
                    return transcript
        except (OSError, UnicodeError, json.JSONDecodeError):
            pass
    if text_path.is_file():
        transcript = text_path.read_text(encoding="utf-8-sig").strip()
        if transcript:
            return transcript
    raise RuntimeError("Whisper completed without producing a readable transcript.")


def transcribe_one(
    resources: dict[str, Any],
    sample: dict[str, Any],
    temporary_root: Path,
    threads: int,
    timeout_seconds: int,
) -> dict[str, Any]:
    safe_case_token = hashlib.sha256(sample["id"].encode("utf-8")).hexdigest()[:20]
    output_prefix = temporary_root / (safe_case_token + "-transcription")
    audio_snapshot = temporary_root / (safe_case_token + "-input.wav")
    try:
        shutil.copyfile(sample["audio_path"], audio_snapshot)
        if sha256_file(audio_snapshot) != sample["audio_sha256"]:
            raise RuntimeError("The source WAV changed after validation; the sample was not benchmarked.")

        command = [
            str(resources["executable"]),
            "-m", str(resources["model"]),
            "-f", str(audio_snapshot),
            "-l", "auto",
            "-oj",
            "-otxt",
            "-of", str(output_prefix),
            "-np",
            "-t", str(threads),
        ]
        started = time.perf_counter()
        try:
            completed = subprocess.run(
                command,
                shell=False,
                stdin=subprocess.DEVNULL,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=timeout_seconds,
                check=False,
            )
        except subprocess.TimeoutExpired as error:
            raise TimeoutError(f"Whisper timed out after {timeout_seconds} seconds.") from error
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        if completed.returncode != 0:
            detail = (completed.stderr or completed.stdout or "").strip()
            raise RuntimeError(f"Whisper exited with code {completed.returncode}: {detail[-1200:]}")
        json_path = output_prefix.with_suffix(".json")
        text_path = output_prefix.with_suffix(".txt")
        transcript = _extract_transcript(json_path, text_path)
        duration = sample["audio_duration_seconds"]
        return {
            "caseId": sample["id"],
            "locale": sample["locale"],
            "speakerId": sample["speaker_id"],
            "audioSha256": sample["audio_sha256"],
            "audioDurationSeconds": round(duration, 4),
            "wallClockMs": round(elapsed_ms, 2),
            "realTimeFactor": round((elapsed_ms / 1000.0) / duration, 6),
            "transcript": transcript,
            "status": "passed",
        }
    finally:
        try:
            audio_snapshot.unlink()
        except FileNotFoundError:
            pass


def _write_json_atomically(path: Path, payload: dict[str, Any], force: bool) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if _is_reparse_point(path.parent):
        raise ValueError(f"Output directory must not be a reparse point: {path.parent}")
    if path.exists() and not force:
        raise FileExistsError(f"Refusing to overwrite existing evaluation evidence: {path} (use --force to replace it).")
    temporary = path.with_name(path.name + "." + os.urandom(6).hex() + ".tmp")
    try:
        temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def run_benchmark(
    data_root: Path,
    manifest_path: Path,
    output_dir: Path,
    prefix: str,
    threads: int,
    timeout_seconds: int,
    force: bool = False,
) -> tuple[Path, Path, dict[str, Any]]:
    resources = verify_whisper_resources(data_root)
    manifest, samples = validate_manifest_and_audio(manifest_path)
    output_dir.mkdir(parents=True, exist_ok=True)
    if _is_reparse_point(output_dir):
        raise ValueError("Benchmark output directory must not be a reparse point.")
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + os.urandom(4).hex()
    report_path = output_dir / f"{prefix}-{run_id}.json"
    scoring_path = output_dir / f"{prefix}-{run_id}.scoring-manifest.json"
    results: list[dict[str, Any]] = []
    failures: list[dict[str, str]] = []

    # A private, run-specific temporary directory is removed even after timeout/failure.
    with tempfile.TemporaryDirectory(prefix="layal-whisper-", dir=output_dir) as temporary:
        temporary_root = Path(temporary)
        for sample in samples:
            try:
                result = transcribe_one(resources, sample, temporary_root, threads, timeout_seconds)
                results.append(result)
                print(f"PASS {sample['id']} [{sample['locale']}] {result['wallClockMs']:.0f} ms; RTF {result['realTimeFactor']:.3f}")
            except Exception as error:  # Preserve per-case diagnostics and finish the remaining corpus.
                failures.append({"caseId": sample["id"], "error": f"{type(error).__name__}: {error}"})
                print(f"FAIL {sample['id']}: {type(error).__name__}: {error}", file=sys.stderr)

    scoring_manifest = copy.deepcopy(manifest)
    hypotheses_by_id = {item["caseId"]: item["transcript"] for item in results}
    for case in scoring_manifest["cases"]:
        if case.get("id") in hypotheses_by_id:
            case.setdefault("hypotheses", {})[ENGINE_ID] = hypotheses_by_id[case["id"]]

    elapsed_values = [item["wallClockMs"] for item in results]
    rtf_values = [item["realTimeFactor"] for item in results]
    report = {
        "schemaVersion": 1,
        "runId": run_id,
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "engine": {
            "id": ENGINE_ID,
            "runtimeArchive": WHISPER_ARCHIVE_NAME,
            "runtimeArchiveSha256": resources["archive_sha256"],
            "executableSha256": resources["executable_sha256"],
            "model": WHISPER_MODEL_RELATIVE_PATH.as_posix(),
            "modelSha256": resources["model_sha256"],
            "dataRoot": "Layal LocalAi resource directory (absolute path intentionally omitted)",
        },
        "device": {
            "os": platform.platform(),
            "architecture": platform.machine(),
            "processor": platform.processor(),
            "logicalCpuCount": os.cpu_count(),
        },
        "policy": {
            "localOnly": True,
            "networkAccess": False,
            "audioUploaded": False,
            "sourceManifestModified": False,
            "humanReferenceRequired": True,
            "maxAudioSeconds": MAX_AUDIO_SECONDS,
            "wallClockIncludesProcessStartup": True,
        },
        "summary": {
            "eligibleSamples": len(samples),
            "passedSamples": len(results),
            "failedSamples": len(failures),
            "medianWallClockMs": round(statistics.median(elapsed_values), 2) if elapsed_values else None,
            "medianRealTimeFactor": round(statistics.median(rtf_values), 6) if rtf_values else None,
        },
        "samples": results,
        "failures": failures,
    }
    _write_json_atomically(report_path, report, force)
    _write_json_atomically(scoring_path, scoring_manifest, force)
    return report_path, scoring_path, report


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-root", type=Path, required=True,
                        help="The exact Layal LocalAi data directory containing tools/ and speech-models/.")
    parser.add_argument("--manifest", type=Path, required=True,
                        help="A local copy of speech-reference-set.json with consented recordings and approved references.")
    parser.add_argument("--output-dir", type=Path,
                        help="Local evidence directory; defaults to <data-root>/evaluation/whisper.")
    parser.add_argument("--prefix", default="whisper-benchmark",
                        help="Filename prefix for JSON evidence (letters, digits, dash and underscore only).")
    parser.add_argument("--threads", type=int, default=max(1, min((os.cpu_count() or 2) - 1, 8)))
    parser.add_argument("--timeout-seconds", type=int, default=DEFAULT_TIMEOUT_SECONDS)
    parser.add_argument("--force", action="store_true",
                        help="Allow overwriting the generated report paths (a new run ID is still used).")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", args.prefix):
        parser.error("--prefix may contain only letters, digits, dashes and underscores.")
    if not 1 <= args.threads <= 8:
        parser.error("--threads must be between 1 and 8.")
    if not 10 <= args.timeout_seconds <= 3600:
        parser.error("--timeout-seconds must be between 10 and 3600.")
    args.output_dir = args.output_dir or (args.data_root / "evaluation" / "whisper")
    return args


def main() -> int:
    args = parse_args()
    try:
        report_path, scoring_path, report = run_benchmark(
            args.data_root, args.manifest, args.output_dir, args.prefix,
            args.threads, args.timeout_seconds, args.force,
        )
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile) as error:
        print(f"Whisper benchmark blocked: {error}", file=sys.stderr)
        return 2

    print(f"Report: {report_path}")
    print(f"Scoring manifest: {scoring_path}")
    print(
        "Completed: "
        f"{report['summary']['passedSamples']}/{report['summary']['eligibleSamples']} samples; "
        f"{report['summary']['failedSamples']} failed."
    )
    if report["summary"]["passedSamples"]:
        print("Next: python tools/evaluation/score_speech_transcripts.py --manifest "
              f"\"{scoring_path}\" --engine {ENGINE_ID}")
    return 0 if report["summary"]["failedSamples"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
