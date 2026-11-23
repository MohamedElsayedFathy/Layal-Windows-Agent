#!/usr/bin/env python3
"""Prepare a small, local-only smoke-test subset from the pinned synthetic Egyptian speech dataset.

The resulting manifest is deliberately incompatible with the consented human-speech
WER/CER workflow. Synthetic prompt text is not treated as a human-verified transcript.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import random
import re
import shutil
import stat
import sys
import tempfile
import wave
from collections import Counter
from pathlib import Path, PurePosixPath
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
DATASET_REPO_ID = "MohamedGomaa30/Synthetic-Egy-Speech-Dataset"
DATASET_REVISION = "bf7ff28bd1737bc78d154ee9f9180a701f88cd1e"
DATASET_URL = "https://huggingface.co/datasets/MohamedGomaa30/Synthetic-Egy-Speech-Dataset"
EXPECTED_ROWS = 1000
ALLOWED_MODELS = {"lahgtna", "chatterbox_egyptian", "egtts_v01", "habibi"}
CASE_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
MAX_AUDIO_BYTES = 64 * 1024 * 1024
MAX_AUDIO_SECONDS = 120.0
WHISPER_SAMPLE_RATE = 16000
SUPPORTED_SOURCE_SAMPLE_RATES = {16000, 24000}
DEFAULT_LIMIT = 20
MAX_LIMIT = 40


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


def _is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _require_plain_file(path: Path, label: str) -> None:
    if _is_reparse_point(path):
        raise ValueError(f"{label} must not be a symbolic link or Windows reparse point: {path}")
    if not path.is_file():
        raise FileNotFoundError(f"{label} was not found: {path}")


def _safe_source_audio(source_root: Path, relative_name: str, case_id: str) -> Path:
    normalized = relative_name.replace(chr(92), "/")
    relative = PurePosixPath(normalized)
    if (
        normalized != relative_name
        or relative.is_absolute()
        or len(relative.parts) != 2
        or relative.parts[0] != "audio"
        or relative.suffix.lower() != ".wav"
        or any(part in {"", ".", ".."} for part in relative.parts)
    ):
        raise ValueError(f"Case {case_id} has an unsafe or unsupported audio path: {relative_name!r}")

    root = source_root.resolve(strict=True)
    candidate = root.joinpath(*relative.parts)
    current = root
    for segment in relative.parts:
        current = current / segment
        if _is_reparse_point(current):
            raise ValueError(f"Case {case_id} audio path contains a symbolic link or reparse point.")
    resolved = candidate.resolve(strict=True)
    if not _is_relative_to(resolved, root):
        raise ValueError(f"Case {case_id} audio path resolves outside the source dataset.")
    _require_plain_file(resolved, f"Audio for case {case_id}")
    return resolved


def _validate_wav(path: Path, case_id: str) -> tuple[float, int]:
    size = path.stat().st_size
    if size <= 44 or size > MAX_AUDIO_BYTES:
        raise ValueError(
            f"Case {case_id} WAV must be greater than 44 bytes and no more than 64 MiB."
        )
    try:
        with wave.open(str(path), "rb") as audio:
            channels = audio.getnchannels()
            sample_rate = audio.getframerate()
            sample_width = audio.getsampwidth()
            frame_count = audio.getnframes()
            compression = audio.getcomptype()
    except (wave.Error, EOFError, OSError) as error:
        raise ValueError(f"Case {case_id} is not a readable PCM WAV: {error}") from error

    if (
        compression != "NONE"
        or channels != 1
        or sample_width != 2
        or sample_rate not in SUPPORTED_SOURCE_SAMPLE_RATES
    ):
        supported_rates = ", ".join(str(rate) for rate in sorted(SUPPORTED_SOURCE_SAMPLE_RATES))
        raise ValueError(
            f"Case {case_id} must be uncompressed PCM WAV, mono, 16-bit, at "
            f"{supported_rates} Hz; found compression={compression}, channels={channels}, "
            f"sampleRate={sample_rate}, sampleWidth={sample_width * 8}-bit."
        )
    duration = frame_count / float(sample_rate)
    if duration <= 0 or duration > MAX_AUDIO_SECONDS:
        raise ValueError(
            f"Case {case_id} duration must be greater than zero and at most {MAX_AUDIO_SECONDS:g} seconds."
        )
    return duration, sample_rate


def _write_whisper_compatible_wav(
    source_path: Path,
    target_path: Path,
    source_sample_rate: int,
    case_id: str,
) -> str:
    """Copy 16 kHz input unchanged or resample 24 kHz mono PCM to Whisper's 16 kHz input."""
    if source_sample_rate == WHISPER_SAMPLE_RATE:
        shutil.copyfile(source_path, target_path)
        return "none"

    if source_sample_rate != 24000:
        raise ValueError(f"Case {case_id} has an unsupported source sample rate: {source_sample_rate}.")

    try:
        import audioop
    except ImportError as error:
        raise RuntimeError(
            "24 kHz source audio requires Python's audioop module for local resampling. "
            "On Python 3.13+, install the drop-in compatibility package with "
            "'python -m pip install --user audioop-lts', then rerun preparation."
        ) from error

    with wave.open(str(source_path), "rb") as source:
        if (
            source.getcomptype() != "NONE"
            or source.getnchannels() != 1
            or source.getsampwidth() != 2
            or source.getframerate() != source_sample_rate
        ):
            raise ValueError(f"Case {case_id} source WAV changed after validation.")
        frames = source.readframes(source.getnframes())

    converted_frames, _ = audioop.ratecv(
        frames, 2, 1, source_sample_rate, WHISPER_SAMPLE_RATE, None
    )
    with wave.open(str(target_path), "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(WHISPER_SAMPLE_RATE)
        target.writeframes(converted_frames)
    return "audioop.ratecv-24000-to-16000"


def _read_source(source_dir: Path) -> tuple[Path, Path, list[dict[str, Any]]]:
    source = source_dir.expanduser().resolve(strict=True)
    if _is_reparse_point(source_dir) or _is_reparse_point(source):
        raise ValueError("Source dataset directory must not be a symbolic link or Windows reparse point.")
    if not source.is_dir():
        raise NotADirectoryError(f"Source dataset path is not a directory: {source}")

    readme_path = source / "README.md"
    metadata_path = source / "metadata.jsonl"
    _require_plain_file(readme_path, "Dataset README.md")
    _require_plain_file(metadata_path, "Dataset metadata.jsonl")

    if readme_path.stat().st_size > 2 * 1024 * 1024:
        raise ValueError("Dataset README.md is unexpectedly large.")
    readme = readme_path.read_text(encoding="utf-8-sig")
    if not re.search(r"(?im)^\s*license\s*:\s*mit\s*$", readme):
        raise ValueError("Dataset README does not declare license: mit; refusing to import it.")
    if "Synthetic Egyptian Speech Dataset" not in readme:
        raise ValueError("Dataset README title does not match the expected source.")
    if "1000 Egyptian Arabic speech samples" not in readme:
        raise ValueError("Dataset README does not match the expected 1,000-sample release.")

    if metadata_path.stat().st_size > 4 * 1024 * 1024:
        raise ValueError("Dataset metadata.jsonl is unexpectedly large.")

    rows: list[dict[str, Any]] = []
    seen_ids: set[str] = set()
    with metadata_path.open("r", encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, start=1):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"Invalid JSON on metadata line {line_number}: {error}") from error
            if not isinstance(row, dict):
                raise ValueError(f"Metadata line {line_number} must be a JSON object.")

            file_name = row.get("file_name")
            prompt_text = row.get("text")
            model = row.get("model")
            if not isinstance(file_name, str) or not isinstance(prompt_text, str) or not prompt_text.strip():
                raise ValueError(f"Metadata line {line_number} requires file_name and non-empty text.")
            if not isinstance(model, str) or model not in ALLOWED_MODELS:
                raise ValueError(f"Metadata line {line_number} has an unsupported generator model.")

            relative = PurePosixPath(file_name.replace(chr(92), "/"))
            case_id = relative.stem
            if not CASE_ID_PATTERN.fullmatch(case_id):
                raise ValueError(f"Metadata line {line_number} has an unsafe case id.")
            if case_id in seen_ids:
                raise ValueError(f"Dataset metadata contains a duplicate case id: {case_id}")
            seen_ids.add(case_id)

            mcd = row.get("mcd")
            if mcd is not None and (
                isinstance(mcd, bool) or not isinstance(mcd, (int, float)) or not math.isfinite(float(mcd))
            ):
                raise ValueError(f"Metadata line {line_number} has an invalid mcd value.")

            rows.append({
                "id": case_id,
                "file_name": file_name,
                "prompt_text": prompt_text.strip(),
                "source_model": model,
                "mcd": float(mcd) if mcd is not None else None,
            })

    if len(rows) != EXPECTED_ROWS:
        raise ValueError(
            f"Expected {EXPECTED_ROWS} rows in the pinned dataset metadata; found {len(rows)}."
        )
    return readme_path, metadata_path, rows


def _select_balanced(rows: list[dict[str, Any]], limit: int, seed: int) -> list[dict[str, Any]]:
    grouped: dict[str, list[dict[str, Any]]] = {model: [] for model in sorted(ALLOWED_MODELS)}
    for row in rows:
        grouped[row["source_model"]].append(row)

    rng = random.Random(seed)
    for candidates in grouped.values():
        rng.shuffle(candidates)

    selected: list[dict[str, Any]] = []
    while len(selected) < limit:
        added = False
        for model in sorted(grouped):
            if grouped[model] and len(selected) < limit:
                selected.append(grouped[model].pop())
                added = True
        if not added:
            break
    return selected


def prepare_workspace(
    source_dir: Path,
    output_dir: Path,
    limit: int = DEFAULT_LIMIT,
    seed: int = 20261123,
    repository_root: Path = REPO_ROOT,
) -> dict[str, Any]:
    if not 4 <= limit <= MAX_LIMIT:
        raise ValueError(f"--limit must be between 4 and {MAX_LIMIT}.")
    if not 0 <= seed <= 2**32 - 1:
        raise ValueError("--seed must be between 0 and 4294967295.")

    source = source_dir.expanduser().resolve(strict=True)
    target = output_dir.expanduser().resolve()
    repo_root = repository_root.expanduser().resolve()
    if target == repo_root or _is_relative_to(target, repo_root):
        raise ValueError("Output directory must be outside the Git repository.")
    if target.exists():
        raise FileExistsError(f"Output directory already exists; choose a new path: {target}")

    readme_path, metadata_path, rows = _read_source(source)
    selected = _select_balanced(rows, limit, seed)

    target.parent.mkdir(parents=True, exist_ok=True)
    if _is_reparse_point(target.parent):
        raise ValueError("Output parent directory must not be a reparse point.")
    staging = Path(tempfile.mkdtemp(prefix=f".{target.name}.staging-", dir=target.parent))
    try:
        audio_target = staging / "audio"
        audio_target.mkdir()
        cases: list[dict[str, Any]] = []
        for row in selected:
            source_audio = _safe_source_audio(source, row["file_name"], row["id"])
            duration, source_sample_rate = _validate_wav(source_audio, row["id"])
            target_audio = audio_target / f'{row["id"]}.wav'
            source_hash = sha256_file(source_audio)
            conversion = _write_whisper_compatible_wav(
                source_audio, target_audio, source_sample_rate, row["id"]
            )
            if sha256_file(source_audio) != source_hash:
                raise RuntimeError(f"Source WAV changed during preparation for case {row['id']}.")
            converted_hash = sha256_file(target_audio)
            converted_duration, converted_sample_rate = _validate_wav(target_audio, row["id"])
            if (
                converted_sample_rate != WHISPER_SAMPLE_RATE
                or abs(converted_duration - duration) > 0.01
            ):
                raise RuntimeError(f"Whisper WAV conversion verification failed for case {row['id']}.")

            cases.append({
                "id": row["id"],
                "locale": "ar-EG",
                "promptText": row["prompt_text"],
                "sourceModel": row["source_model"],
                "mcd": row["mcd"],
                "audioAsset": f'audio/{row["id"]}.wav',
                "sourceAudioSha256": source_hash,
                "sourceSampleRate": source_sample_rate,
                "sampleRate": converted_sample_rate,
                "conversion": conversion,
                "audioSha256": converted_hash,
                "audioDurationSeconds": round(converted_duration, 4),
            })

        model_counts = dict(sorted(Counter(case["sourceModel"] for case in cases).items()))
        manifest = {
            "schemaVersion": 1,
            "evaluationMode": "synthetic-speech-smoke-only",
            "scoreEligibility": "not-eligible-for-WER-CER",
            "createdByTool": "prepare_synthetic_speech_smoke.py",
            "selection": {
                "strategy": "deterministic-round-robin-by-generator-model",
                "seed": seed,
                "requestedLimit": limit,
                "selectedCount": len(cases),
                "sourceRows": len(rows),
                "selectedByModel": model_counts,
            },
            "dataset": {
                "repoId": DATASET_REPO_ID,
                "revision": DATASET_REVISION,
                "sourceUrl": DATASET_URL,
                "cardUrl": DATASET_URL,
                "declaredLicense": "MIT",
                "licenseEvidence": "README.md front matter declares license: mit; not an independent rights audit.",
                "readmeSha256": sha256_file(readme_path),
                "metadataSha256": sha256_file(metadata_path),
                "audioKind": "synthetic TTS output, not human-recorded speech",
                "attribution": "Mohamed Gomaa Elsafy, Synthetic Egyptian Speech Dataset (2026).",
            },
            "limitations": [
                "Synthetic TTS outputs are not human-recorded speech and do not establish real-speaker recognition accuracy.",
                "The source dataset describes a curated best-selected sample set; it is not an unbiased gold evaluation corpus.",
                "promptText is the supplied synthesis text, not a human-verified transcript of the generated audio.",
                "Do not calculate or publish WER/CER from this smoke subset.",
                "Review the source dataset license and upstream text/audio provenance before any redistribution or non-evaluation use.",
            ],
            "cases": cases,
        }

        (staging / "synthetic-smoke-manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        shutil.copyfile(readme_path, staging / "SOURCE_DATASET_README.md")
        (staging / "README_AR.md").write_text(
            "\n".join([
                "# مجموعة اختبار Whisper الأولية — صوت مصري اصطناعي",
                "",
                "هذه المساحة مخصصة لاختبار سلامة تشغيل Whisper على عينات TTS مصرية اصطناعية فقط.",
                "ليست مجموعة أصوات بشرية، وليست مجموعة ذهبية لقياس WER/CER.",
                "",
                f"- المستودع: {DATASET_REPO_ID}",
                f"- Revision مثبت: {DATASET_REVISION}",
                "- الترخيص المعلن في بطاقة البيانات: MIT؛ لم يُجرَ تدقيق قانوني مستقل لمصادر المحتوى السابقة.",
                f"- العينات المختارة: {len(cases)} من أصل {len(rows)}.",
                f"- التوزيع حسب محرك التوليد: {json.dumps(model_counts, ensure_ascii=False)}",
                "",
                "راجع SOURCE_DATASET_README.md وبيانات المصدر قبل أي استخدام آخر. احتفظ بهذه الملفات محليًا، ولا ترفع الصوت أو نتائج التفريغ إلى GitHub.",
                "",
                "لا تستخدم promptText باعتباره تفريغًا بشريًا معتمدًا. لا تمرر هذا الملف إلى أداة WER/CER البشرية.",
            ]) + "\n",
            encoding="utf-8",
            newline="\n",
        )

        if target.exists():
            raise FileExistsError(f"Output directory appeared during preparation: {target}")
        os.replace(staging, target)
    except Exception:
        if staging.exists():
            shutil.rmtree(staging, ignore_errors=True)
        raise

    return {
        "output_dir": target,
        "sample_count": len(cases),
        "source_rows": len(rows),
        "model_counts": model_counts,
        "manifest_path": target / "synthetic-smoke-manifest.json",
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path, required=True,
                        help="Local pinned dataset snapshot containing README.md, metadata.jsonl, and audio/.")
    parser.add_argument("--output-dir", type=Path, required=True,
                        help="New local output directory outside the Git repository.")
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT,
                        help=f"Balanced sample count, 4..{MAX_LIMIT} (default: {DEFAULT_LIMIT}).")
    parser.add_argument("--seed", type=int, default=20261123,
                        help="Deterministic selection seed (default: 20261123).")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        summary = prepare_workspace(args.source_dir, args.output_dir, args.limit, args.seed)
    except (OSError, ValueError, RuntimeError) as error:
        print(f"Synthetic speech preparation blocked: {error}", file=sys.stderr)
        return 2

    print(f"Workspace: {summary['output_dir']}")
    print(f"Selected samples: {summary['sample_count']} / {summary['source_rows']}")
    print("Generator model counts: " + ", ".join(
        f"{model}={count}" for model, count in summary["model_counts"].items()
    ))
    print(f"Manifest: {summary['manifest_path']}")
    print("Synthetic-only smoke subset created; no human speech consent or WER/CER eligibility is implied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
