#!/usr/bin/env python3
"""Validate and score local TTS candidate renders for Layal.

This tool never downloads models, starts inference processes, or uploads audio.
It reads a user-prepared JSON run manifest and WAV files already on disk.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
import sys
import wave
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from pathlib import PureWindowsPath
from statistics import mean
from urllib.parse import urlparse

DEFAULT_PROMPTS = (
    Path(__file__).resolve().parents[2]
    / "tests" / "TestAssets" / "Speech" / "voice-persona-evaluation.json"
)
LANGUAGES = {"EgyptianArabic", "ArabicStandard", "English", "MixedArabicEnglish"}
STYLES = {
    "natural", "gentle", "bright", "clear",
    "empathetic", "serious", "energetic", "playful",
}
RATING_FIELDS = (
    "naturalness", "intelligibility", "softness", "accentFit",
    "identityConsistency", "prosodyFit", "pronunciationAccuracy",
)
HASH_LENGTH = 64
MIN_HUMAN_RATERS_PER_SAMPLE = 2


def _is_number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _is_https_url(value: object) -> bool:
    if not isinstance(value, str):
        return False
    parsed = urlparse(value)
    return parsed.scheme == "https" and bool(parsed.netloc)


def _is_sha256(value: object) -> bool:
    if not isinstance(value, str) or len(value) != HASH_LENGTH:
        return False
    try:
        int(value, 16)
        return True
    except ValueError:
        return False


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def validate_prompt_set(payload: object) -> tuple[list[str], dict[str, dict]]:
    errors: list[str] = []
    if not isinstance(payload, dict) or payload.get("schemaVersion") != 1:
        return ["Prompt set must be a schemaVersion 1 JSON object."], {}
    cases = payload.get("testCases")
    if not isinstance(cases, list) or not cases:
        return ["Prompt set 'testCases' must be a non-empty array."], {}
    indexed: dict[str, dict] = {}
    for index, case in enumerate(cases, start=1):
        prefix = f"Prompt #{index}"
        if not isinstance(case, dict):
            errors.append(f"{prefix} must be an object.")
            continue
        case_id = case.get("id")
        language = case.get("language")
        text = case.get("text")
        if not isinstance(case_id, str) or not case_id.strip():
            errors.append(f"{prefix} has no non-empty id.")
            continue
        if case_id in indexed:
            errors.append(f"Duplicate prompt id '{case_id}'.")
            continue
        if not isinstance(language, str) or language not in LANGUAGES:
            errors.append(f"Prompt '{case_id}' has unsupported language '{language}'.")
        if not isinstance(text, str) or not text.strip():
            errors.append(f"Prompt '{case_id}' has empty text.")
        indexed[case_id] = case
    return errors, indexed


def validate_manifest(payload: object, prompt_cases: dict[str, dict]) -> tuple[list[str], dict[str, dict]]:
    errors: list[str] = []
    if not isinstance(payload, dict):
        return ["Run manifest root must be a JSON object."], {}
    if payload.get("schemaVersion") != 1:
        errors.append("Unsupported manifest schemaVersion; expected 1.")
    run_id = payload.get("runId")
    if not isinstance(run_id, str) or not run_id.strip():
        errors.append("'runId' must be a non-empty string.")

    engines = payload.get("engines")
    if not isinstance(engines, list) or not engines:
        errors.append("'engines' must be a non-empty array.")
        engines = []
    engine_index: dict[str, dict] = {}
    allowed_status = {"pending", "verified", "blocked"}
    allowed_acceptance = {"pending", "accepted", "rejected"}
    for index, engine in enumerate(engines, start=1):
        prefix = f"Engine #{index}"
        if not isinstance(engine, dict):
            errors.append(f"{prefix} must be an object.")
            continue
        engine_id = engine.get("id")
        if not isinstance(engine_id, str) or not engine_id.strip():
            errors.append(f"{prefix} has no non-empty id.")
            continue
        if engine_id in engine_index:
            errors.append(f"Duplicate engine id '{engine_id}'.")
            continue
        engine_index[engine_id] = engine
        for field in ("displayName", "revision", "speakerId"):
            if not isinstance(engine.get(field), str) or not engine[field].strip():
                errors.append(f"Engine '{engine_id}' needs a non-empty '{field}'.")
        if not _is_https_url(engine.get("sourceUrl")):
            errors.append(f"Engine '{engine_id}' needs an HTTPS sourceUrl.")
        if not isinstance(engine.get("revisionIsImmutable"), bool):
            errors.append(f"Engine '{engine_id}' needs boolean revisionIsImmutable.")
        for field in ("licenseStatus", "speakerRightsStatus"):
            value = engine.get(field)
            if not isinstance(value, str) or value not in allowed_status:
                errors.append(f"Engine '{engine_id}' has invalid {field}; use pending, verified, or blocked.")
        manual_status = engine.get("manualAcceptanceStatus")
        if not isinstance(manual_status, str) or manual_status not in allowed_acceptance:
            errors.append(f"Engine '{engine_id}' must set manualAcceptanceStatus to pending, accepted, or rejected.")
        for status_field, evidence_field in (
            ("licenseStatus", "licenseEvidenceUrl"),
            ("speakerRightsStatus", "speakerRightsEvidenceUrl"),
        ):
            if engine.get(status_field) == "verified" and not _is_https_url(engine.get(evidence_field)):
                errors.append(f"Engine '{engine_id}' marks {status_field} verified without an HTTPS {evidence_field}.")
        if engine.get("voiceCloningEnabled") is not False:
            errors.append(f"Engine '{engine_id}' must set voiceCloningEnabled to false.")
        hashes = engine.get("modelSha256")
        if not isinstance(hashes, list) or not hashes or any(not _is_sha256(item) for item in hashes):
            errors.append(f"Engine '{engine_id}' needs one or more 64-character modelSha256 values.")

        languages = engine.get("supportedLanguages")
        if not isinstance(languages, list) or not languages or any(not isinstance(value, str) or value not in LANGUAGES for value in languages):
            errors.append(f"Engine '{engine_id}' has invalid supportedLanguages.")
        elif len(set(languages)) != len(languages):
            errors.append(f"Engine '{engine_id}' repeats a supported language.")
        styles = engine.get("supportedStyles")
        if not isinstance(styles, list) or not styles or any(not isinstance(value, str) or value not in STYLES for value in styles):
            errors.append(f"Engine '{engine_id}' has invalid supportedStyles.")
        elif len(set(styles)) != len(styles):
            errors.append(f"Engine '{engine_id}' repeats a supported style.")

    samples = payload.get("samples")
    if not isinstance(samples, list):
        errors.append("'samples' must be an array.")
        samples = []
    seen_samples: set[tuple[str, str, str]] = set()
    for index, sample in enumerate(samples, start=1):
        prefix = f"Sample #{index}"
        if not isinstance(sample, dict):
            errors.append(f"{prefix} must be an object.")
            continue
        case_id = sample.get("caseId")
        engine_id = sample.get("engineId")
        style = sample.get("style")
        if not isinstance(case_id, str) or case_id not in prompt_cases:
            errors.append(f"{prefix} refers to unknown caseId '{case_id}'.")
            continue
        engine = engine_index.get(engine_id) if isinstance(engine_id, str) else None
        if engine is None:
            errors.append(f"{prefix} refers to unknown engineId '{engine_id}'.")
            continue
        language = prompt_cases[case_id].get("language")
        supported_languages = engine.get("supportedLanguages", [])
        supported_styles = engine.get("supportedStyles", [])
        if not isinstance(supported_languages, list) or language not in supported_languages:
            errors.append(f"{prefix} uses language '{language}' not declared by engine '{engine_id}'.")
        if not isinstance(style, str):
            errors.append(f"{prefix} style must be a string.")
            continue
        if not isinstance(supported_styles, list) or style not in supported_styles:
            errors.append(f"{prefix} uses unsupported style '{style}' for engine '{engine_id}'.")
        key = (case_id, engine_id, style)
        if key in seen_samples:
            errors.append(f"{prefix} duplicates the case/engine/style combination {key}.")
        seen_samples.add(key)
        for field in ("renderTimeMs", "timeToFirstAudioMs"):
            value = sample.get(field)
            if not _is_number(value) or value < 0:
                errors.append(f"{prefix} needs a non-negative numeric '{field}'.")
        if _is_number(sample.get("renderTimeMs")) and _is_number(sample.get("timeToFirstAudioMs")):
            if sample["timeToFirstAudioMs"] > sample["renderTimeMs"]:
                errors.append(f"{prefix} timeToFirstAudioMs cannot exceed renderTimeMs.")
        if not isinstance(sample.get("audioPath"), str) or not sample["audioPath"].strip():
            errors.append(f"{prefix} needs a relative audioPath.")
        if not _is_sha256(sample.get("audioSha256")):
            errors.append(f"{prefix} needs a 64-character audioSha256.")
        ratings = sample.get("ratings", [])
        if not isinstance(ratings, list):
            errors.append(f"{prefix} ratings must be an array.")
            continue
        raters: set[str] = set()
        for rating_index, rating in enumerate(ratings, start=1):
            rating_prefix = f"{prefix} rating #{rating_index}"
            if not isinstance(rating, dict):
                errors.append(f"{rating_prefix} must be an object.")
                continue
            rater_id = rating.get("raterId")
            if not isinstance(rater_id, str) or not rater_id.strip():
                errors.append(f"{rating_prefix} needs an anonymous non-empty raterId.")
            elif rater_id in raters:
                errors.append(f"{rating_prefix} duplicates raterId '{rater_id}'.")
            else:
                raters.add(rater_id)
            scores = rating.get("scores")
            if not isinstance(scores, dict):
                errors.append(f"{rating_prefix} needs a scores object.")
                continue
            for field in RATING_FIELDS:
                value = scores.get(field)
                if not _is_number(value) or value < 1 or value > 5:
                    errors.append(f"{rating_prefix} score '{field}' must be between 1 and 5.")
    return errors, engine_index


def resolve_audio_file(root: Path, relative_path: str) -> Path:
    candidate = Path(relative_path)
    windows_path = PureWindowsPath(relative_path)
    if candidate.is_absolute() or windows_path.is_absolute() or bool(windows_path.drive) or "\\" in relative_path or ".." in candidate.parts:
        raise ValueError("audioPath must stay inside the evaluation run directory.")
    resolved_root = root.resolve()
    resolved = (resolved_root / candidate).resolve(strict=True)
    try:
        resolved.relative_to(resolved_root)
    except ValueError as error:
        raise ValueError("audioPath resolves outside the evaluation run directory.") from error
    if not resolved.is_file():
        raise ValueError("audioPath must reference a regular file.")
    return resolved


def inspect_wav(path: Path, expected_sha256: str) -> dict:
    actual_hash = sha256_file(path)
    if actual_hash.lower() != expected_sha256.lower():
        raise ValueError("audioSha256 does not match the WAV file.")
    try:
        with wave.open(str(path), "rb") as audio:
            if audio.getcomptype() != "NONE":
                raise ValueError("WAV must use uncompressed PCM audio.")
            if audio.getnchannels() != 1 or audio.getsampwidth() != 2:
                raise ValueError("WAV must be normalized to mono, signed 16-bit PCM.")
            rate = audio.getframerate()
            frames = audio.getnframes()
            if rate <= 0 or frames <= 0:
                raise ValueError("WAV must contain audio frames and a valid sample rate.")
            total = 0
            squared_sum = 0
            peak = 0
            clipped = 0
            while True:
                block = audio.readframes(65536)
                if not block:
                    break
                usable = len(block) - (len(block) % 2)
                for (value,) in struct.iter_unpack("<h", block[:usable]):
                    magnitude = abs(value)
                    total += 1
                    squared_sum += value * value
                    peak = max(peak, magnitude)
                    if magnitude >= 32760:
                        clipped += 1
    except (wave.Error, EOFError) as error:
        raise ValueError(f"Could not read PCM WAV: {error}") from error
    if total != frames:
        raise ValueError("WAV data length is inconsistent with its frame count.")
    duration_ms = frames * 1000.0 / rate
    return {
        "sha256": actual_hash,
        "durationMs": round(duration_ms, 3),
        "sampleRateHz": rate,
        "rmsFullScale": round(math.sqrt(squared_sum / total) / 32768.0, 6),
        "peakFullScale": round(peak / 32768.0, 6),
        "clippedSamplesPercent": round(clipped * 100.0 / total, 6),
    }


def _average(values: list[float]) -> float | None:
    return round(mean(values), 4) if values else None


def _metrics_for(records: list[dict]) -> dict:
    fields = (
        "renderTimeMs", "timeToFirstAudioMs", "durationMs", "realTimeFactor",
        "rmsFullScale", "peakFullScale", "clippedSamplesPercent",
    )
    return {field: _average([record[field] for record in records]) for field in fields}


def _ratings_for(records: list[dict]) -> dict:
    collected: dict[str, list[float]] = defaultdict(list)
    for record in records:
        for rating in record["ratings"]:
            for field in RATING_FIELDS:
                collected[field].append(float(rating["scores"][field]))
    return {field: _average(collected[field]) for field in RATING_FIELDS}


def build_report(payload: dict, prompt_cases: dict[str, dict], engine_index: dict[str, dict], audio_records: list[dict]) -> dict:
    engine_reports = []
    for engine_id, engine in engine_index.items():
        languages = engine["supportedLanguages"]
        styles = engine["supportedStyles"]
        expected_keys = {
            (case_id, style)
            for case_id, case in prompt_cases.items()
            if case["language"] in languages
            for style in styles
        }
        engine_records = [record for record in audio_records if record["engineId"] == engine_id]
        observed_keys = {(record["caseId"], record["style"]) for record in engine_records}
        missing_count = max(0, len(expected_keys - observed_keys))
        production_gates = {
            "immutableRevision": engine.get("revisionIsImmutable") is True,
            "licenseVerified": engine.get("licenseStatus") == "verified" and _is_https_url(engine.get("licenseEvidenceUrl")),
            "speakerRightsVerified": engine.get("speakerRightsStatus") == "verified" and _is_https_url(engine.get("speakerRightsEvidenceUrl")),
            "modelHashesRecorded": bool(engine.get("modelSha256")) and all(_is_sha256(item) for item in engine.get("modelSha256", [])),
            "voiceCloningDisabled": engine.get("voiceCloningEnabled") is False,
            "completeCoverage": missing_count == 0 and bool(expected_keys),
            "humanReviewComplete": missing_count == 0 and bool(engine_records) and all(len(record["ratings"]) >= MIN_HUMAN_RATERS_PER_SAMPLE for record in engine_records),
            "manualAcceptanceRecorded": engine.get("manualAcceptanceStatus") == "accepted",
        }
        by_language = {}
        by_style = {}
        for language in sorted(languages):
            subset = [record for record in engine_records if record["language"] == language]
            expected = sum(1 for case in prompt_cases.values() if case["language"] == language) * len(styles)
            by_language[language] = {
                "observedSamples": len(subset), "expectedSamples": expected,
                "metrics": _metrics_for(subset), "humanRatings": _ratings_for(subset),
            }
        for style in sorted(styles):
            subset = [record for record in engine_records if record["style"] == style]
            expected = sum(1 for case in prompt_cases.values() if case["language"] in languages)
            by_style[style] = {
                "observedSamples": len(subset), "expectedSamples": expected,
                "metrics": _metrics_for(subset), "humanRatings": _ratings_for(subset),
            }
        engine_reports.append({
            "id": engine_id,
            "displayName": engine["displayName"],
            "revision": engine["revision"],
            "licenseStatus": engine["licenseStatus"],
            "speakerRightsStatus": engine["speakerRightsStatus"],
            "observedSamples": len(engine_records),
            "expectedSamples": len(expected_keys),
            "missingSamples": missing_count,
            "coveragePercent": round(len(observed_keys & expected_keys) * 100.0 / len(expected_keys), 2) if expected_keys else 0.0,
            "productionGates": production_gates,
            "eligibleForProduction": all(production_gates.values()),
            "metrics": _metrics_for(engine_records),
            "humanRatings": _ratings_for(engine_records),
            "byLanguage": by_language,
            "byStyle": by_style,
        })
    return {
        "schemaVersion": 1,
        "runId": payload["runId"],
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "promptCount": len(prompt_cases),
        "sampleCount": len(audio_records),
        "engines": engine_reports,
        "interpretation": [
            "This report scores recorded artifacts only; it does not run or approve a TTS model.",
            "Missing samples are not zero-quality scores; they reduce coverage only.",
            "Human ratings are reported per dimension and are not collapsed into an arbitrary overall score.",
            "Production eligibility requires every provenance, rights, voice-cloning, human-review and acceptance gate to pass.",
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path, help="TTS run manifest JSON")
    parser.add_argument("--prompts", type=Path, default=DEFAULT_PROMPTS, help="Fixed voice prompt set")
    parser.add_argument("--validate-only", action="store_true", help="Validate and inspect outputs without writing a report")
    parser.add_argument("--strict-complete", action="store_true", help="Return 3 when any declared engine/prompt/style sample is missing")
    parser.add_argument("--report-json", type=Path, help="Write the machine-readable report to this path")
    args = parser.parse_args()

    try:
        prompt_payload = json.loads(args.prompts.read_text(encoding="utf-8"))
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        print(f"Could not read evaluation input: {error}", file=sys.stderr)
        return 2

    prompt_errors, prompt_cases = validate_prompt_set(prompt_payload)
    if prompt_errors:
        for error in prompt_errors:
            print(f"Prompt set error: {error}", file=sys.stderr)
        return 2
    errors, engine_index = validate_manifest(manifest, prompt_cases)
    if errors:
        for error in errors:
            print(f"Manifest error: {error}", file=sys.stderr)
        return 2

    audio_records = []
    for index, sample in enumerate(manifest.get("samples", []), start=1):
        try:
            audio_path = resolve_audio_file(args.manifest.parent, sample["audioPath"])
            audio = inspect_wav(audio_path, sample["audioSha256"])
        except (OSError, ValueError, KeyError) as error:
            print(f"Sample #{index} audio error: {error}", file=sys.stderr)
            return 2
        case = prompt_cases[sample["caseId"]]
        duration_ms = audio["durationMs"]
        render_ms = float(sample["renderTimeMs"])
        audio_records.append({
            "caseId": sample["caseId"], "engineId": sample["engineId"],
            "language": case["language"], "style": sample["style"],
            "renderTimeMs": render_ms,
            "timeToFirstAudioMs": float(sample["timeToFirstAudioMs"]),
            "realTimeFactor": render_ms / duration_ms if duration_ms > 0 else 0.0,
            **audio, "ratings": sample.get("ratings", []),
        })

    report = build_report(manifest, prompt_cases, engine_index, audio_records)
    print(f"Run: {report['runId']} | prompts: {report['promptCount']} | recorded WAV samples: {report['sampleCount']}")
    for engine in report["engines"]:
        print(
            f"{engine['displayName']} ({engine['id']}): "
            f"coverage {engine['observedSamples']}/{engine['expectedSamples']} "
            f"({engine['coveragePercent']}%), mean RTF={engine['metrics']['realTimeFactor']}, "
            f"mean TTFA={engine['metrics']['timeToFirstAudioMs']} ms, "
            f"production eligible={engine['eligibleForProduction']}"
        )
        if engine["humanRatings"] and all(value is not None for value in engine["humanRatings"].values()):
            print("  human ratings (1-5): " + ", ".join(f"{key}={value}" for key, value in engine["humanRatings"].items()))
        else:
            print("  human ratings: pending or incomplete")
    if not audio_records:
        print("No TTS audio samples are recorded yet; no quality or performance result can be claimed.")
    if args.report_json:
        try:
            args.report_json.parent.mkdir(parents=True, exist_ok=True)
            args.report_json.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        except OSError as error:
            print(f"Could not write report: {error}", file=sys.stderr)
            return 2
    if args.validate_only:
        print("Manifest and referenced WAV artifacts are valid.")
    incomplete = any(engine["missingSamples"] > 0 for engine in report["engines"])
    if args.strict_complete and incomplete:
        print("Evaluation matrix is incomplete.", file=sys.stderr)
        return 3
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
