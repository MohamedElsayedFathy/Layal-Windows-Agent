#!/usr/bin/env python3
"""Score ASR hypotheses against Layal's consented, human-reviewed corpus.

Audio is never inferred from transcript-only cases. Recorded cases require explicit
speaker consent and a pseudonymous speaker identifier; scoring hypotheses require an
approved human reference transcript.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
import wave
from collections import defaultdict
from datetime import datetime
from pathlib import Path, PurePosixPath

SUPPORTED_LOCALES = {"ar-EG", "ar", "en", "mixed"}
NEGATION_TOKENS = {"مش", "ما", "لا", "لم", "لن", "ليس", "not", "no", "never", "without", "cannot"}
PSEUDONYMOUS_ID = re.compile(r"^(spk|rev)-[a-z0-9][a-z0-9-]{2,30}$")


def normalize(text: str) -> str:
    """Conservatively normalize Arabic/English text for reproducible WER/CER."""
    value = unicodedata.normalize("NFKC", text).lower()
    output: list[str] = []
    for character in value:
        category = unicodedata.category(character)
        if character == "\u0640" or category in {"Mn", "Mc", "Me"}:
            continue
        if character in "\u061c\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2066\u2067\u2068\u2069":
            continue
        if character in "أإآٱ":
            character = "ا"
        elif character in "ىی":
            character = "ي"
        elif character == "ک":
            character = "ك"
        if character.isalnum():
            output.append(character)
        elif output and output[-1] != " ":
            output.append(" ")
    return "".join(output).strip()


def edit_distance(left: list[str], right: list[str]) -> int:
    """Levenshtein distance using O(len(right)) memory."""
    previous = list(range(len(right) + 1))
    for row_index, left_item in enumerate(left, start=1):
        current = [row_index]
        for column_index, right_item in enumerate(right, start=1):
            substitution = previous[column_index - 1] + (left_item != right_item)
            insertion = current[column_index - 1] + 1
            deletion = previous[column_index] + 1
            current.append(min(substitution, insertion, deletion))
        previous = current
    return previous[-1]


def score_pair(reference: str, hypothesis: str) -> tuple[int, int, int, int]:
    """Return WER/CER numerators and denominators for one transcript pair."""
    ref = normalize(reference)
    hyp = normalize(hypothesis)
    ref_words = ref.split()
    hyp_words = hyp.split()
    reference_characters = ref.replace(" ", "")
    hypothesis_characters = hyp.replace(" ", "")
    return (
        edit_distance(ref_words, hyp_words),
        len(ref_words),
        edit_distance(list(reference_characters), list(hypothesis_characters)),
        len(reference_characters),
    )


def is_critical_token(token: str) -> bool:
    """Identify meaning-sensitive negations and numeric tokens in a reference."""
    return token in NEGATION_TOKENS or any(character.isdigit() for character in token)


def score_critical_tokens(reference: str, hypothesis: str) -> tuple[int, int]:
    """Return (critical reference-token errors, critical reference-token count)."""
    ref, hyp = normalize(reference).split(), normalize(hypothesis).split()
    critical = {i for i, token in enumerate(ref) if is_critical_token(token)}
    if not critical:
        return 0, 0
    rows, columns = len(ref) + 1, len(hyp) + 1
    costs = [[0] * columns for _ in range(rows)]
    for i in range(rows):
        costs[i][0] = i
    for j in range(columns):
        costs[0][j] = j
    for i in range(1, rows):
        for j in range(1, columns):
            costs[i][j] = min(
                costs[i - 1][j] + 1,
                costs[i][j - 1] + 1,
                costs[i - 1][j - 1] + (ref[i - 1] != hyp[j - 1]),
            )
    erroneous = set()
    i, j = len(ref), len(hyp)
    while i > 0 or j > 0:
        if i > 0 and j > 0 and ref[i - 1] == hyp[j - 1] and costs[i][j] == costs[i - 1][j - 1]:
            i -= 1
            j -= 1
        elif i > 0 and j > 0 and costs[i][j] == costs[i - 1][j - 1] + 1:
            erroneous.add(i - 1)
            i -= 1
            j -= 1
        elif i > 0 and costs[i][j] == costs[i - 1][j] + 1:
            erroneous.add(i - 1)
            i -= 1
        else:
            j -= 1
    return len(critical & erroneous), len(critical)


def _valid_timestamp(value: object) -> bool:
    if not isinstance(value, str) or not value.strip():
        return False
    try:
        datetime.fromisoformat(value.replace("Z", "+00:00"))
        return True
    except ValueError:
        return False


def _validate_recording(case: dict, prefix: str, errors: list[str]) -> None:
    status = case.get("recordingStatus", "not-recorded")
    if not isinstance(status, str) or status not in {"not-recorded", "recorded"}:
        errors.append(f"{prefix} has unsupported recordingStatus '{status}'.")
        return
    if status == "not-recorded":
        if case.get("audioAsset") is not None:
            errors.append(f"{prefix} must not reference audioAsset while recordingStatus is not-recorded.")
        if case.get("consent") is not None:
            errors.append(f"{prefix} must not store consent metadata without a recording.")
        return

    audio_asset = case.get("audioAsset")
    if not isinstance(audio_asset, str) or not audio_asset.strip():
        errors.append(f"{prefix} recorded case requires an audioAsset path.")
    else:
        audio_path = PurePosixPath(audio_asset)
        if (
            audio_path.is_absolute()
            or "\\" in audio_asset
            or ".." in audio_path.parts
            or not audio_asset.startswith("audio/")
            or audio_path.suffix.lower() != ".wav"
        ):
            errors.append(f"{prefix} audioAsset must be a relative WAV path below Speech/audio/.")

    speaker_id = case.get("speakerId")
    if not isinstance(speaker_id, str) or not PSEUDONYMOUS_ID.fullmatch(speaker_id) or not speaker_id.startswith("spk-"):
        errors.append(f"{prefix} recorded case requires a pseudonymous speakerId such as 'spk-001'.")

    consent = case.get("consent")
    if not isinstance(consent, dict) or consent.get("granted") is not True:
        errors.append(f"{prefix} recorded case requires explicit consent.granted=true.")
    else:
        if consent.get("scope") != "research-evaluation":
            errors.append(f"{prefix} consent.scope must be 'research-evaluation'.")
        if not _valid_timestamp(consent.get("recordedAt")):
            errors.append(f"{prefix} consent.recordedAt must be an ISO-8601 timestamp.")

    review = case.get("referenceReview")
    review_status = review.get("status") if isinstance(review, dict) else None
    review_approved = review_status == "approved"
    if not isinstance(review_status, str) or review_status not in {"pending", "approved"}:
        errors.append(f"{prefix} recorded case requires referenceReview.status 'pending' or 'approved'.")
    elif review_status == "approved":
        reviewer_id = review.get("reviewerId")
        if not isinstance(reviewer_id, str) or not PSEUDONYMOUS_ID.fullmatch(reviewer_id) or not reviewer_id.startswith("rev-"):
            errors.append(f"{prefix} approved reference requires a pseudonymous reviewerId such as 'rev-001'.")
        if not _valid_timestamp(review.get("reviewedAt")):
            errors.append(f"{prefix} approved reference requires an ISO-8601 reviewedAt timestamp.")

    hypotheses = case.get("hypotheses", {})
    if isinstance(hypotheses, dict) and hypotheses and not review_approved:
        errors.append(f"{prefix} cannot contain ASR hypotheses until its human referenceReview is approved.")


def validate_manifest(payload: object) -> list[str]:
    errors: list[str] = []
    if not isinstance(payload, dict):
        return ["The manifest root must be a JSON object."]
    if payload.get("schemaVersion") != 1:
        errors.append("Unsupported manifest schemaVersion; expected 1.")
    cases = payload.get("cases")
    if not isinstance(cases, list):
        errors.append("'cases' must be a JSON array.")
        return errors
    if not cases:
        errors.append("'cases' must contain at least one reference transcript.")
        return errors

    seen_ids: set[str] = set()
    for index, case in enumerate(cases, start=1):
        prefix = f"Case #{index}"
        if not isinstance(case, dict):
            errors.append(f"{prefix} must be a JSON object.")
            continue
        case_id = case.get("id")
        if not isinstance(case_id, str) or not case_id.strip():
            errors.append(f"{prefix} has no non-empty string 'id'.")
        elif case_id in seen_ids:
            errors.append(f"{prefix} duplicates id '{case_id}'.")
        else:
            seen_ids.add(case_id)

        locale = case.get("locale")
        if not isinstance(locale, str) or locale not in SUPPORTED_LOCALES:
            errors.append(f"{prefix} has unsupported locale '{locale}'.")
        reference = case.get("referenceText")
        if not isinstance(reference, str) or not normalize(reference):
            errors.append(f"{prefix} must have a non-empty 'referenceText'.")
        _validate_recording(case, prefix, errors)

        hypotheses = case.get("hypotheses", {})
        if not isinstance(hypotheses, dict):
            errors.append(f"{prefix} 'hypotheses' must be an object keyed by engine id.")
        else:
            for engine, hypothesis in hypotheses.items():
                if not isinstance(engine, str) or not engine.strip():
                    errors.append(f"{prefix} contains an empty engine id.")
                if not isinstance(hypothesis, str):
                    errors.append(f"{prefix} hypothesis for '{engine}' must be a string.")
    return errors


def validate_audio_files(payload: dict, manifest_path: Path) -> list[str]:
    """Check referenced WAV files exist and cannot escape the manifest's audio folder."""
    errors: list[str] = []
    audio_root = (manifest_path.parent / "audio").resolve()
    for case in payload["cases"]:
        if case.get("recordingStatus", "not-recorded") != "recorded":
            continue
        relative = PurePosixPath(case["audioAsset"])
        candidate = (manifest_path.parent / Path(*relative.parts)).resolve()
        if not candidate.is_relative_to(audio_root):
            errors.append(f"Case '{case['id']}' audioAsset resolves outside the Speech/audio directory.")
        elif not candidate.is_file():
            errors.append(f"Case '{case['id']}' audioAsset file does not exist: {case['audioAsset']}.")
        else:
            try:
                with wave.open(str(candidate), "rb") as audio:
                    channels = audio.getnchannels()
                    sample_rate = audio.getframerate()
                    sample_width = audio.getsampwidth()
                if (channels, sample_rate, sample_width) != (1, 16000, 2):
                    errors.append(
                        f"Case '{case['id']}' WAV must be mono 16 kHz / 16-bit PCM; "
                        f"found {channels} channel(s), {sample_rate} Hz, {sample_width * 8}-bit."
                    )
            except (wave.Error, OSError) as error:
                errors.append(f"Case '{case['id']}' audioAsset is not a readable PCM WAV: {error}.")
    return errors


def main() -> int:
    default_manifest = (
        Path(__file__).resolve().parents[2]
        / "tests"
        / "TestAssets"
        / "Speech"
        / "speech-reference-set.json"
    )
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=default_manifest)
    parser.add_argument("--engine", help="Score one engine key; defaults to all observed engine keys.")
    parser.add_argument(
        "--validate-only",
        action="store_true",
        help="Validate the manifest and report reference coverage without calculating metrics.",
    )
    parser.add_argument(
        "--check-audio-files",
        action="store_true",
        help="Also verify every recorded WAV exists under the manifest's audio/ directory.",
    )
    args = parser.parse_args()

    try:
        payload = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        print(f"Could not read manifest: {error}", file=sys.stderr)
        return 2

    errors = validate_manifest(payload)
    if not errors and args.check_audio_files:
        errors.extend(validate_audio_files(payload, args.manifest))
    if errors:
        for error in errors:
            print(f"Manifest error: {error}", file=sys.stderr)
        return 2

    cases = payload["cases"]
    locale_counts: dict[str, int] = defaultdict(int)
    for case in cases:
        locale_counts[case["locale"]] += 1

    recorded = sum(case.get("recordingStatus", "not-recorded") == "recorded" for case in cases)
    approved = sum(
        case.get("recordingStatus") == "recorded"
        and case.get("referenceReview", {}).get("status") == "approved"
        for case in cases
    )
    if args.validate_only:
        print(f"Manifest valid: {len(cases)} reference cases; {recorded} recorded; {approved} human-reviewed.")
        for locale, count in sorted(locale_counts.items()):
            print(f"{locale}\t{count}")
        return 0

    observed = sorted({
        engine
        for case in cases
        for engine in case.get("hypotheses", {})
    })
    engines = [args.engine] if args.engine else observed
    if not engines:
        print(
            f"Manifest valid: {len(cases)} reference cases; no ASR hypotheses are recorded yet. "
            "Collect consented recordings, approve human-reviewed transcripts, then add engine hypotheses."
        )
        return 0

    totals: dict[tuple[str, str], list[int]] = defaultdict(lambda: [0, 0, 0, 0, 0, 0, 0])
    for case in cases:
        locale = case["locale"]
        reference = case["referenceText"]
        hypotheses = case.get("hypotheses", {})
        for engine in engines:
            if engine not in hypotheses:
                continue
            hypothesis = hypotheses[engine]
            word_errors, word_count, char_errors, char_count = score_pair(reference, hypothesis)
            critical_errors, critical_count = score_critical_tokens(reference, hypothesis)
            total = totals[(locale, engine)]
            total[0] += word_errors
            total[1] += word_count
            total[2] += char_errors
            total[3] += char_count
            total[4] += 1
            total[5] += critical_errors
            total[6] += critical_count

    if not totals:
        print(f"No hypotheses found for the requested engine(s): {', '.join(engines)}.", file=sys.stderr)
        return 2

    print("locale\tengine\tcases\tWER\tCER\tcritical_tokens\tcritical_errors\tcritical_error_rate")
    for (locale, engine), values in sorted(totals.items()):
        word_errors, words, char_errors, characters, count, critical_errors, critical_count = values
        wer = word_errors / words
        cer = char_errors / characters
        critical_rate = critical_errors / critical_count if critical_count else 0.0
        print(f"{locale}\t{engine}\t{count}\t{wer:.4f}\t{cer:.4f}\t{critical_count}\t{critical_errors}\t{critical_rate:.4f}")
    print("WER/CER are corpus-weighted within each locale. Critical-token error rate tracks substitutions/deletions of negations and numeric tokens; inspect names and file paths manually.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
