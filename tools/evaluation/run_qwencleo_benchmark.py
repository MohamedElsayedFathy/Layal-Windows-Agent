#!/usr/bin/env python3
"""Run the optional local QwenCleo-ASR challenger on Layal's consented corpus.

This is an evaluation-only path. It never downloads models or packages, starts a
server, makes network requests, uploads audio, or changes the source manifest.
The model must already exist as a standalone local snapshot and its immutable
40-character Hugging Face revision must be supplied by the operator.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import importlib.util
import json
import os
import platform
import re
import shutil
import stat
import statistics
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

ENGINE_ID = "qwencleo_asr_17b"
MODEL_ID = "mohammedaly22/QwenCleo-ASR"
UPSTREAM_MODEL_CARD = "https://huggingface.co/mohammedaly22/QwenCleo-ASR"
MODEL_REVISION_PATTERN = re.compile(r"^[0-9a-fA-F]{40}$")
MAX_MODEL_FILES = 4096
MAX_MODEL_BYTES = 32 * 1024 * 1024 * 1024


def _load_whisper_runner():
    """Reuse the consent, transcript-review, WAV and path validator from D1."""
    module_path = Path(__file__).resolve().with_name("run_whisper_benchmark.py")
    spec = importlib.util.spec_from_file_location("layal_whisper_benchmark_for_qwencleo", module_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load Layal's shared speech validator at {module_path}.")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


WHISPER_RUNNER = _load_whisper_runner()


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


def _require_no_reparse_components(path: Path, label: str) -> None:
    """Reject links in any existing component of a path before resolving it."""
    absolute = path.expanduser().absolute()
    current = Path(absolute.anchor)
    for part in absolute.parts[1:]:
        current = current / part
        if _is_reparse_point(current):
            raise ValueError(f"{label} path contains a symbolic link or Windows reparse point.")


def inspect_model_snapshot(model_dir: Path, model_revision: str) -> tuple[Path, list[dict[str, Any]]]:
    """Validate and hash an already-present model snapshot without following links."""
    if not isinstance(model_revision, str) or not MODEL_REVISION_PATTERN.fullmatch(model_revision):
        raise ValueError("--model-revision must be the immutable 40-character Hugging Face commit SHA.")
    _require_no_reparse_components(model_dir, "Model directory")
    root = model_dir.expanduser().resolve(strict=True)
    if not root.is_dir():
        raise ValueError("--model-dir must point to an existing local directory.")

    if MODEL_REVISION_PATTERN.fullmatch(root.name) and root.name.casefold() != model_revision.casefold():
        raise ValueError("Model directory snapshot SHA does not match --model-revision.")

    config_path = root / "config.json"
    if _is_reparse_point(config_path) or not config_path.is_file():
        raise ValueError("Model snapshot must contain a regular local config.json file.")
    try:
        config = json.loads(config_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Cannot read local model config.json: {error}") from error
    if not isinstance(config, dict):
        raise ValueError("Model config.json must contain a JSON object.")
    model_type = str(config.get("model_type") or "").casefold()
    architectures = config.get("architectures", [])
    architecture_names = architectures if isinstance(architectures, list) else []
    is_qwen3_asr = "qwen3" in model_type or any(
        isinstance(name, str) and "qwen3asr" in name.casefold().replace("_", "")
        for name in architecture_names
    )
    if not is_qwen3_asr:
        raise ValueError("Local model config does not identify a Qwen3-ASR architecture.")

    inventory: list[dict[str, Any]] = []
    total_bytes = 0
    for directory, subdirectories, filenames in os.walk(root, followlinks=False):
        directory_path = Path(directory)
        if _is_reparse_point(directory_path):
            raise ValueError("Model snapshot contains a symbolic-link or reparse-point directory.")
        for name in list(subdirectories):
            child = directory_path / name
            if _is_reparse_point(child):
                raise ValueError("Model snapshot contains a symbolic-link or reparse-point directory.")
        for name in filenames:
            path = directory_path / name
            if _is_reparse_point(path) or not path.is_file():
                raise ValueError("Model snapshot contains a symbolic-link, reparse point, or non-regular file.")
            relative = path.relative_to(root).as_posix()
            size = path.stat().st_size
            total_bytes += size
            if total_bytes > MAX_MODEL_BYTES:
                raise ValueError("Model snapshot exceeds the 32 GiB evaluation safety limit.")
            inventory.append({"path": relative, "sizeBytes": size, "sha256": sha256_file(path)})
            if len(inventory) > MAX_MODEL_FILES:
                raise ValueError("Model snapshot contains too many files for an evaluation inventory.")

    weight_files = [item for item in inventory if item["path"].lower().endswith((".safetensors", ".bin", ".pt", ".pth"))]
    if not weight_files:
        raise ValueError("Model snapshot must contain at least one local model-weight file (.safetensors/.bin/.pt/.pth).")
    inventory.sort(key=lambda item: item["path"].casefold())
    return root, inventory


def language_hint(locale: str) -> str | None:
    if locale in {"ar-EG", "ar", "mixed"}:
        return "Arabic"
    if locale == "en":
        # The upstream model card says pure-English audio is not its target use;
        # let the model auto-detect it and keep this subgroup explicitly visible.
        return None
    raise ValueError(f"Unsupported speech locale for QwenCleo evaluation: {locale}")


def load_local_model(model_dir: Path, device: str) -> tuple[Any, dict[str, Any]]:
    """Load Qwen3-ASR only from local files; do not invoke a model downloader."""
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import torch
    from qwen_asr import Qwen3ASRModel

    if device == "auto":
        device = "cuda:0" if torch.cuda.is_available() else "cpu"
    if device == "cuda:0":
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA was requested but torch.cuda.is_available() is false.")
        supports_bf16 = bool(getattr(torch.cuda, "is_bf16_supported", lambda: False)())
        dtype = torch.bfloat16 if supports_bf16 else torch.float16
        properties = torch.cuda.get_device_properties(0)
        gpu = {
            "name": properties.name,
            "totalMemoryBytes": int(properties.total_memory),
            "bf16Supported": supports_bf16,
        }
    elif device == "cpu":
        dtype = torch.float32
        gpu = None
    else:
        raise ValueError("Supported devices are auto, cpu, and cuda:0.")

    model = Qwen3ASRModel.from_pretrained(
        str(model_dir),
        dtype=dtype,
        device_map=device,
        local_files_only=True,
    )
    try:
        qwen_asr_version = importlib.metadata.version("qwen-asr")
    except importlib.metadata.PackageNotFoundError:
        qwen_asr_version = "unknown"
    return model, {
        "device": device,
        "dtype": str(dtype).replace("torch.", ""),
        "torchVersion": str(torch.__version__),
        "qwenAsrVersion": qwen_asr_version,
        "cudaVersion": getattr(torch.version, "cuda", None),
        "gpu": gpu,
        "torchModule": torch,
    }


def _transcribe_one(model: Any, sample: dict[str, Any]) -> dict[str, Any]:
    hint = language_hint(sample["locale"])
    started = time.perf_counter()
    output = model.transcribe(audio=str(sample["audio_path"]), language=hint)
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    if not isinstance(output, (list, tuple)) or not output:
        raise RuntimeError("QwenCleo returned no transcription result.")
    transcript = getattr(output[0], "text", None)
    if not isinstance(transcript, str) or not transcript.strip():
        raise RuntimeError("QwenCleo returned an empty or invalid transcript.")
    duration = float(sample["audio_duration_seconds"])
    return {
        "caseId": sample["id"],
        "locale": sample["locale"],
        "speakerId": sample["speaker_id"],
        "audioSha256": sample["audio_sha256"],
        "audioDurationSeconds": round(duration, 4),
        "languageHint": hint,
        "wallClockMs": round(elapsed_ms, 2),
        "realTimeFactor": round((elapsed_ms / 1000.0) / duration, 6),
        "transcript": transcript.strip(),
        "status": "passed",
    }


def _redact_paths(message: str, paths: list[Path]) -> str:
    redacted = message
    for path in paths:
        try:
            values = {str(path), str(path.resolve(strict=False)), path.as_posix(), path.resolve(strict=False).as_posix()}
        except (OSError, RuntimeError):
            values = {str(path), path.as_posix()}
        for value in sorted((item for item in values if item), key=len, reverse=True):
            redacted = redacted.replace(value, "<local path>")
    return redacted


def _write_json_atomically(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if _is_reparse_point(path.parent):
        raise ValueError("Output directory must not be a symbolic link or Windows reparse point.")
    if path.exists():
        raise FileExistsError(f"Refusing to overwrite existing evaluation evidence: {path}")
    temporary = path.with_name(path.name + "." + os.urandom(6).hex() + ".tmp")
    try:
        temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _scoring_manifest(source: dict[str, Any], results: list[dict[str, Any]]) -> dict[str, Any]:
    result_by_id = {item["caseId"]: item["transcript"] for item in results if item["status"] == "passed"}
    safe_fields = (
        "id", "locale", "referenceText", "recordingStatus", "audioAsset",
        "speakerId", "consent", "referenceReview", "hypotheses",
    )
    cases = []
    for source_case in source["cases"]:
        case = {key: source_case[key] for key in safe_fields if key in source_case}
        hypotheses = case.get("hypotheses")
        if not isinstance(hypotheses, dict):
            hypotheses = {}
        else:
            hypotheses = dict(hypotheses)
        # A failed sample in this run must not inherit an older result.
        hypotheses.pop(ENGINE_ID, None)
        case["hypotheses"] = hypotheses
        if case.get("id") in result_by_id:
            case["hypotheses"][ENGINE_ID] = result_by_id[case["id"]]
        cases.append(case)
    return {
        "schemaVersion": 1,
        "description": "Local scoring copy produced by Layal's optional QwenCleo-ASR challenger; source manifest unchanged.",
        "cases": cases,
    }


def run_benchmark(
    manifest_path: Path,
    model_dir: Path,
    model_revision: str,
    output_dir: Path,
    device: str = "auto",
    model_loader: Callable[[Path, str], tuple[Any, dict[str, Any]]] | None = None,
) -> tuple[Path, Path, dict[str, Any]]:
    model_root, model_inventory = inspect_model_snapshot(model_dir, model_revision)
    _require_no_reparse_components(manifest_path, "Speech manifest")
    source_manifest, samples = WHISPER_RUNNER.validate_manifest_and_audio(manifest_path)
    if device not in {"auto", "cpu", "cuda:0"}:
        raise ValueError("Supported devices are auto, cpu, and cuda:0.")

    load_started = time.perf_counter()
    model, runtime = (model_loader or load_local_model)(model_root, device)
    model_load_ms = round((time.perf_counter() - load_started) * 1000.0, 2)
    torch_module = runtime.get("torchModule") if isinstance(runtime, dict) else None

    _require_no_reparse_components(output_dir, "Output directory")
    output_dir.mkdir(parents=True, exist_ok=True)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + os.urandom(4).hex()
    report_path = output_dir / f"qwencleo-benchmark-{run_id}.json"
    scoring_path = output_dir / f"qwencleo-benchmark-{run_id}.scoring-manifest.json"
    results: list[dict[str, Any]] = []
    failures: list[dict[str, str]] = []

    with tempfile.TemporaryDirectory(prefix="layal-qwencleo-") as temporary:
        temporary_root = Path(temporary)
        for sample in samples:
            token = hashlib.sha256(sample["id"].encode("utf-8")).hexdigest()[:20]
            audio_snapshot = temporary_root / f"{token}-input.wav"
            try:
                shutil.copyfile(sample["audio_path"], audio_snapshot)
                if sha256_file(audio_snapshot) != sample["audio_sha256"]:
                    raise RuntimeError("The source WAV changed after validation; the sample was not benchmarked.")
                current = dict(sample)
                current["audio_path"] = audio_snapshot
                result = _transcribe_one(model, current)
                results.append(result)
                print(
                    f"{result['caseId']}: {result['audioDurationSeconds']:.2f}s audio | "
                    f"{result['wallClockMs']:.0f}ms | RTF {result['realTimeFactor']:.3f}"
                )
            except Exception as error:
                failures.append({
                    "caseId": sample["id"],
                    "locale": sample["locale"],
                    "error": _redact_paths(str(error), [manifest_path, model_root, sample["audio_path"], audio_snapshot]),
                })
            finally:
                try:
                    audio_snapshot.unlink()
                except FileNotFoundError:
                    pass

    elapsed = [item["wallClockMs"] for item in results]
    rtf_values = [item["realTimeFactor"] for item in results]
    peak_cuda_bytes = None
    if torch_module is not None:
        try:
            if runtime.get("device") == "cuda:0":
                peak_cuda_bytes = int(torch_module.cuda.max_memory_allocated(0))
        except Exception:
            peak_cuda_bytes = None
    if isinstance(runtime, dict):
        runtime = {key: value for key, value in runtime.items() if key != "torchModule"}

    report = {
        "schemaVersion": 1,
        "runId": run_id,
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "engine": {
            "id": ENGINE_ID,
            "displayName": "QwenCleo-ASR 1.7B (optional evaluation challenger)",
            "modelId": MODEL_ID,
            "modelRevision": model_revision.lower(),
            "modelRevisionProvidedByOperator": True,
            "modelFiles": model_inventory,
            "upstreamModelCard": UPSTREAM_MODEL_CARD,
            "declaredLicense": "Apache-2.0 (upstream model card claim)",
            "licenseReviewStatus": "pending-independent-review",
            "trainingDataProvenanceStatus": "pending; model card describes hundreds of hours of Egyptian podcast speech without a detailed source inventory",
            "productionEligible": False,
        },
        "device": {
            "os": platform.platform(),
            "architecture": platform.machine(),
            "processor": platform.processor() or platform.machine(),
            "logicalCpuCount": os.cpu_count(),
            **runtime,
        },
        "timing": {
            "modelLoadMs": model_load_ms,
            "medianWallClockMs": round(statistics.median(elapsed), 2) if elapsed else None,
            "medianRealTimeFactor": round(statistics.median(rtf_values), 6) if rtf_values else None,
            "peakCudaAllocatedBytes": peak_cuda_bytes,
        },
        "policy": {
            "evaluationOnly": True,
            "localOnly": True,
            "networkAccess": False,
            "modelDownloadAttempted": False,
            "packageInstallAttempted": False,
            "audioUploaded": False,
            "sourceManifestModified": False,
            "humanConsentRequired": True,
            "humanApprovedReferenceRequired": True,
            "modelLoadedWithLocalFilesOnly": True,
            "productionIntegrationChanged": False,
            "pureEnglishCaveat": "Upstream model card says the model is not optimized for pure-English audio.",
        },
        "summary": {
            "eligibleSamples": len(samples),
            "passedSamples": len(results),
            "failedSamples": len(failures),
        },
        "samples": results,
        "failures": failures,
    }
    scoring_manifest = _scoring_manifest(source_manifest, results)
    _write_json_atomically(report_path, report)
    _write_json_atomically(scoring_path, scoring_manifest)
    return report_path, scoring_path, report


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", type=Path, required=True,
                        help="Existing standalone local QwenCleo snapshot directory; no download is attempted.")
    parser.add_argument("--model-revision", required=True,
                        help="Exact 40-character Hugging Face model commit SHA for the local snapshot.")
    parser.add_argument("--manifest", type=Path, required=True,
                        help="Local manifest containing consented WAVs and human-approved references.")
    parser.add_argument("--output-dir", type=Path, required=True,
                        help="Local evidence folder outside Git; existing report files are never overwritten.")
    parser.add_argument("--device", choices=("auto", "cpu", "cuda:0"), default="auto")
    args = parser.parse_args()
    if not MODEL_REVISION_PATTERN.fullmatch(args.model_revision):
        parser.error("--model-revision must be the immutable 40-character Hugging Face commit SHA.")
    return args


def main() -> int:
    args = parse_args()
    try:
        report_path, scoring_path, report = run_benchmark(
            args.manifest, args.model_dir, args.model_revision,
            args.output_dir, args.device,
        )
    except (OSError, ValueError, RuntimeError, ImportError) as error:
        print(f"QwenCleo benchmark blocked: {error}", file=sys.stderr)
        return 2

    summary = report["summary"]
    print(f"Report: {report_path}")
    print(f"Scoring manifest: {scoring_path}")
    print(f"Completed: {summary['passedSamples']}/{summary['eligibleSamples']} samples; {summary['failedSamples']} failed.")
    print("Next: python tools/evaluation/score_speech_transcripts.py --manifest "
          f"\"{scoring_path}\" --engine {ENGINE_ID}")
    print("Candidate only: do not replace Whisper; model license and training-data provenance remain pending review.")
    return 0 if summary["failedSamples"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
