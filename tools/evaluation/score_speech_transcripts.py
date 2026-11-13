#!/usr/bin/env python3
"""Score Layal speech transcripts with weighted WER/CER, grouped by language variety.

The manifest contains referenceText and per-engine hypotheses. It deliberately stores
no audio. Add hypotheses only after running a local recognizer on consented recordings.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
from collections import defaultdict
from pathlib import Path


def normalize(text: str) -> str:
    value = unicodedata.normalize("NFKC", text or "").lower()
    output: list[str] = []
    for character in value:
        code = ord(character)
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
    ref = normalize(reference)
    hyp = normalize(hypothesis)
    ref_words = ref.split()
    hyp_words = hyp.split()
    word_errors = edit_distance(ref_words, hyp_words)
    char_errors = edit_distance(list(ref.replace(" ", "")), list(hyp.replace(" ", "")))
    return word_errors, max(1, len(ref_words)), char_errors, max(1, len(ref.replace(" ", "")))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=Path("tests/TestAssets/Speech/speech-reference-set.json"))
    parser.add_argument("--engine", help="Score one engine key; defaults to all observed hypothesis keys.")
    args = parser.parse_args()

    try:
        payload = json.loads(args.manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        print(f"Could not read manifest: {error}", file=sys.stderr)
        return 2
    if payload.get("schemaVersion") != 1 or not isinstance(payload.get("cases"), list):
        print("Unsupported or malformed speech evaluation manifest.", file=sys.stderr)
        return 2

    observed = sorted({engine for case in payload["cases"] for engine in case.get("hypotheses", {})})
    engines = [args.engine] if args.engine else observed
    if not engines:
        print("No ASR hypotheses are recorded yet. The transcript manifest is ready for consented recordings.")
        return 0

    totals: dict[tuple[str, str], list[int]] = defaultdict(lambda: [0, 0, 0, 0, 0])
    for case in payload["cases"]:
        locale = str(case.get("locale", "unknown"))
        reference = str(case.get("referenceText", ""))
        for engine in engines:
            hypothesis = case.get("hypotheses", {}).get(engine)
            if not isinstance(hypothesis, str) or not hypothesis.strip():
                continue
            word_errors, word_count, char_errors, char_count = score_pair(reference, hypothesis)
            total = totals[(locale, engine)]
            total[0] += word_errors
            total[1] += word_count
            total[2] += char_errors
            total[3] += char_count
            total[4] += 1

    if not totals:
        print("No matching hypotheses were found; no metrics were calculated.")
        return 0

    print("locale\tengine\tcases\tWER\tCER")
    for (locale, engine), (word_errors, words, char_errors, characters, count) in sorted(totals.items()):
        wer = word_errors / words
        cer = char_errors / characters
        print(f"{locale}\t{engine}\t{count}\t{wer:.4f}\t{cer:.4f}")
    print("Metrics are corpus-weighted after conservative normalization; inspect deletions of negation, numbers and names separately.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
