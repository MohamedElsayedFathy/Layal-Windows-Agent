#!/usr/bin/env python3
"""Create a local qualitative listening-review sheet for a synthetic Whisper smoke run.

This report is not a gold transcript, does not create a scoring manifest, and must
never be used to calculate WER/CER.
"""
from __future__ import annotations

import argparse
import csv
import importlib.util
import json
import os
import sys
from pathlib import Path
from typing import Any

RUNNER_PATH = Path(__file__).resolve().with_name("run_synthetic_speech_smoke.py")
SPEC = importlib.util.spec_from_file_location("layal_synthetic_speech_smoke_for_review", RUNNER_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Cannot load synthetic smoke validators from {RUNNER_PATH}.")
RUNNER = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = RUNNER
SPEC.loader.exec_module(RUNNER)

REPO_ROOT = Path(__file__).resolve().parents[2]
CSV_FIELDS = [
    "case_id",
    "locale",
    "source_tts_model",
    "audio_asset_relative_to_manifest",
    "audio_sha256",
    "audio_duration_seconds",
    "prompt_text_context_only_not_reference",
    "whisper_transcript",
    "manual_review_status",
    "reviewer_heard_text_non_reference",
    "reviewer_notes",
]


def _require_outside_repository(path: Path, label: str) -> Path:
    absolute = path.expanduser().absolute()
    current = Path(absolute.anchor)
    for component in absolute.parts[1:]:
        current = current / component
        if RUNNER.RUNNER._is_reparse_point(current):
            raise ValueError(f"{label} path must not contain symbolic links or Windows reparse points.")
    resolved = absolute.resolve(strict=False)
    if resolved == REPO_ROOT or resolved.is_relative_to(REPO_ROOT):
        raise ValueError(f"{label} must be outside the Git repository.")
    return resolved


def _read_json(path: Path, label: str) -> dict[str, Any]:
    RUNNER.RUNNER._require_plain_file(path, label)
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Cannot read {label}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{label} must contain a JSON object.")
    return value


def build_review_rows(manifest_path: Path, report_path: Path) -> list[dict[str, str]]:
    manifest, cases = RUNNER.validate_manifest_and_audio(manifest_path)
    report = _read_json(report_path, "Synthetic Whisper smoke report")

    if report.get("benchmarkType") != "synthetic-speech-smoke":
        raise ValueError("Report is not a synthetic-speech-smoke result.")
    if report.get("scoreEligibility") != "not-eligible-for-WER-CER":
        raise ValueError("Report is missing the mandatory no-WER/CER eligibility marker.")
    metrics = report.get("metrics")
    if not isinstance(metrics, dict) or metrics.get("wer") is not None or metrics.get("cer") is not None:
        raise ValueError("Qualitative review export refuses reports containing WER/CER values.")
    if report.get("dataset") != manifest.get("dataset"):
        raise ValueError("Report and manifest dataset provenance do not match.")

    case_by_id = {case["id"]: case for case in manifest["cases"]}
    sample_results = report.get("samples")
    failures = report.get("failures")
    summary = report.get("summary")
    if not isinstance(sample_results, list) or not isinstance(failures, list) or not isinstance(summary, dict):
        raise ValueError("Report must contain samples, failures, and summary objects.")
    if summary.get("eligibleSamples") != len(cases):
        raise ValueError("Report sample count does not match the synthetic smoke manifest.")
    if summary.get("passedSamples") != len(sample_results) or summary.get("failedSamples") != len(failures):
        raise ValueError("Report summary counts do not match its samples and failures.")

    seen: set[str] = set()
    passed_by_id: dict[str, dict[str, Any]] = {}
    for sample in sample_results:
        if not isinstance(sample, dict):
            raise ValueError("Every report sample must be a JSON object.")
        case_id = sample.get("caseId")
        if not isinstance(case_id, str) or case_id not in case_by_id or case_id in seen:
            raise ValueError("Report contains an unknown or duplicate case ID.")
        seen.add(case_id)
        case = case_by_id[case_id]
        if sample.get("status") != "passed":
            raise ValueError(f"Report sample {case_id} is not marked passed.")
        transcript = sample.get("transcript")
        if not isinstance(transcript, str) or not transcript.strip():
            raise ValueError(f"Report sample {case_id} has no Whisper transcript.")
        if sample.get("audioSha256") != case.get("audioSha256"):
            raise ValueError(f"Report sample {case_id} audio hash does not match the manifest.")
        if sample.get("sourceAudioSha256") != case.get("sourceAudioSha256"):
            raise ValueError(f"Report sample {case_id} source audio hash does not match the manifest.")
        if sample.get("sourceModel") != case.get("sourceModel"):
            raise ValueError(f"Report sample {case_id} source TTS model does not match the manifest.")
        passed_by_id[case_id] = sample

    for failure in failures:
        if not isinstance(failure, dict):
            raise ValueError("Every report failure must be a JSON object.")
        case_id = failure.get("caseId")
        if not isinstance(case_id, str) or case_id not in case_by_id or case_id in seen:
            raise ValueError("Report contains an unknown or duplicate failed case ID.")
        seen.add(case_id)

    if seen != set(case_by_id):
        raise ValueError("Report does not account for every case in the synthetic smoke manifest.")

    rows: list[dict[str, str]] = []
    for case in cases:
        case_id = case["id"]
        sample = passed_by_id.get(case_id)
        rows.append({
            "case_id": case_id,
            "locale": "ar-EG",
            "source_tts_model": case["source_model"],
            "audio_asset_relative_to_manifest": case["audio_path"].relative_to(manifest_path.resolve(strict=True).parent).as_posix(),
            "audio_sha256": case["audio_sha256"],
            "audio_duration_seconds": f"{case['audio_duration_seconds']:.4f}",
            "prompt_text_context_only_not_reference": case["prompt_text"],
            "whisper_transcript": sample["transcript"] if sample else "",
            "manual_review_status": "NOT_REVIEWED" if sample else "RUN_FAILED",
            "reviewer_heard_text_non_reference": "",
            "reviewer_notes": "",
        })
    return rows


def export_review_sheet(manifest_path: Path, report_path: Path, output_csv: Path) -> tuple[Path, Path, int]:
    manifest_path = _require_outside_repository(manifest_path, "Synthetic smoke manifest")
    report_path = _require_outside_repository(report_path, "Synthetic smoke report")
    output_csv = _require_outside_repository(output_csv, "Human review CSV")
    rows = build_review_rows(manifest_path, report_path)

    if output_csv.suffix.casefold() != ".csv":
        raise ValueError("Output review sheet must use a .csv extension.")
    readme_path = output_csv.with_suffix(".README_AR.md")
    if output_csv.exists() or readme_path.exists():
        raise FileExistsError("Refusing to overwrite an existing human-review output file.")

    output_csv.parent.mkdir(parents=True, exist_ok=True)
    if RUNNER.RUNNER._is_reparse_point(output_csv.parent):
        raise ValueError("Review output directory must not be a reparse point.")
    temporary_csv = output_csv.with_name(output_csv.name + "." + os.urandom(6).hex() + ".tmp")
    try:
        with temporary_csv.open("x", encoding="utf-8-sig", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=CSV_FIELDS, extrasaction="raise")
            writer.writeheader()
            writer.writerows(rows)
        os.replace(temporary_csv, output_csv)
        readme_path.write_text(
            "\n".join([
                "# ورقة مراجعة استماع نوعية — Whisper على صوت مصري اصطناعي",
                "",
                "هذه الورقة لمراجعة قابلية سماع وفهم مخرجات Whisper يدويًا فقط.",
                "",
                "- افتح ملف WAV المحلي المشار إليه في عمود audio_asset_relative_to_manifest واستمع إليه.",
                "- قارن التفريغ بوضوح مع الصوت؛ عمود prompt_text_context_only_not_reference سياق التوليد فقط وليس مرجعًا ذهبيًا.",
                "- اكتب ما سمعته في reviewer_heard_text_non_reference عند الحاجة، وسجّل ملاحظاتك في reviewer_notes.",
                "- غيّر manual_review_status إلى CLEAR أو PARTIALLY_CLEAR أو UNCLEAR أو TRANSCRIPT_MISMATCH أو AUDIO_PROBLEM.",
                "- لا تحسب WER أو CER، ولا تنقل هذه الورقة إلى manifest الكلام البشري أو إلى أدوات scoring.",
                "- احتفظ بالصوت والتقرير وهذه الورقة محليًا خارج GitHub؛ لا ترفعها إلى المستودع أو artifacts العامة.",
                "",
                "أي نص يكتبه المراجع هنا ملاحظة نوعية غير معتمدة كمرجع ذهبي ولا يمنح موافقة بشرية أو حقوق إعادة استخدام.",
            ]) + "\n",
            encoding="utf-8",
            newline="\n",
        )
    except Exception:
        try:
            temporary_csv.unlink()
        except FileNotFoundError:
            pass
        raise
    return output_csv, readme_path, len(rows)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True,
                        help="Synthetic smoke manifest created by prepare_synthetic_speech_smoke.py.")
    parser.add_argument("--report", type=Path, required=True,
                        help="JSON report created by run_synthetic_speech_smoke.py.")
    parser.add_argument("--output-csv", type=Path, required=True,
                        help="New CSV path outside the Git repository; existing files are never overwritten.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        csv_path, guide_path, count = export_review_sheet(args.manifest, args.report, args.output_csv)
    except (OSError, ValueError, RuntimeError) as error:
        print(f"Synthetic human-review export blocked: {error}", file=sys.stderr)
        return 2
    print(f"Review sheet: {csv_path}")
    print(f"Arabic review guide: {guide_path}")
    print(f"Samples exported: {count}")
    print("Qualitative review only; promptText is not a gold reference and WER/CER are not calculated.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
