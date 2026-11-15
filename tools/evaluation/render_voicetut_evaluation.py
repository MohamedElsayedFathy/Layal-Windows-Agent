#!/usr/bin/env python3
"""Render a pinned VoiceTut Voice Design evaluation set for Layal.

This tool uses Voice Design only. It deliberately excludes built-in reference
speaker files, zero-shot reference audio and all cloning paths. ArabicStandard
is rendered only as an explicitly exploratory case: VoiceTut documents Egyptian
Arabic and English, not a separate MSA model.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import platform
import re
import sys
import time
from pathlib import Path
from typing import Any

MODEL_ID = "mohammedaly22/VoiceTut-TTS"
MODEL_REVISION = "b36bdf4416864d84f47ca77ce3decf34f6ab16cc"
DEFAULT_INSTRUCTION = "female, child, very high pitch"
SUPPORTED_LANGUAGES = {"EgyptianArabic", "English", "MixedArabicEnglish"}
LANGUAGE_CODES = {
    "EgyptianArabic": "arz",
    "ArabicStandard": "arz",  # exploratory only; not declared as supported
    "English": "en",
    "MixedArabicEnglish": "arz",
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def normalize_language_code(language: str) -> tuple[str, bool]:
    if language not in LANGUAGE_CODES:
        raise ValueError(f"Unsupported evaluation language: {language}")
    return LANGUAGE_CODES[language], language in SUPPORTED_LANGUAGES


def safe_case_id(value: str) -> str:
    result = re.sub(r"[^A-Za-z0-9_-]+", "-", value).strip("-")
    if not result:
        raise ValueError("Prompt case id cannot be empty.")
    return result


def system_ram_mb() -> int:
    try:
        import psutil
        value = int(psutil.virtual_memory().total // (1024 * 1024))
        if value > 0:
            return value
    except Exception:
        pass

    if os.name == "nt":
        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [
                ("dwLength", ctypes.c_ulong),
                ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
            ]
        status = MEMORYSTATUSEX()
        status.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return int(status.ullTotalPhys // (1024 * 1024))

    if hasattr(os, "sysconf"):
        try:
            return int(os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES") // (1024 * 1024))
        except (ValueError, OSError):
            pass
    raise RuntimeError("Could not detect system RAM; provide --ram-mb.")


def default_hf_home() -> Path:
    if os.environ.get("HF_HOME"):
        return Path(os.environ["HF_HOME"])
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"]) / "Layal" / "LocalAi" / "hf-cache"
    return Path.home() / ".cache" / "huggingface"


def load_prompt_set(path: Path) -> dict[str, Any]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if payload.get("schemaVersion") != 1 or not isinstance(payload.get("testCases"), list):
        raise ValueError("Prompt set must be schemaVersion 1 and contain testCases.")
    if not payload["testCases"]:
        raise ValueError("Prompt set contains no test cases.")
    seen: set[str] = set()
    for case in payload["testCases"]:
        case_id = safe_case_id(str(case.get("id") or ""))
        if case_id in seen:
            raise ValueError(f"Duplicate prompt id: {case_id}")
        seen.add(case_id)
        if case.get("language") not in LANGUAGE_CODES or not str(case.get("text") or "").strip():
            raise ValueError(f"Invalid prompt case: {case_id}")
    return payload


def load_engine(hf_home: Path, device: str | None = None):
    import torch
    from huggingface_hub import snapshot_download
    from voicetut_tts import VoiceTutTTS

    hf_home.mkdir(parents=True, exist_ok=True)
    snapshot = snapshot_download(
        repo_id=MODEL_ID,
        revision=MODEL_REVISION,
        cache_dir=str(hf_home / "hub"),
        allow_patterns=[
            "config.json",
            "model.safetensors",
            "tokenizer.json",
            "tokenizer_config.json",
            "chat_template.jinja",
        ],
    )
    snapshot_path = Path(snapshot)
    weights = sorted(snapshot_path.glob("*.safetensors"))
    if not weights:
        raise RuntimeError("Pinned VoiceTut snapshot has no safetensors model weights.")
    active_device = device or ("cuda:0" if torch.cuda.is_available() else "cpu")
    dtype = "float16" if active_device.startswith("cuda") else "float32"
    disabled_refs = hf_home / "layal-reference-speakers-disabled.json"
    engine = VoiceTutTTS.from_pretrained(
        str(snapshot_path),
        references=str(disabled_refs),
        device=active_device,
        dtype=dtype,
        language="arz",
    )
    return engine, active_device, snapshot_path, weights


def render_one(
    engine: Any,
    text: str,
    language_code: str,
    instruction: str,
    num_steps: int,
    guidance_scale: float,
    speed: float,
    normalize: bool,
) -> tuple[int, Any, float, float]:
    import numpy as np

    started = time.perf_counter()
    first_audio_ms: float | None = None
    chunks: list[Any] = []
    sample_rate: int | None = None
    for current_rate, chunk in engine.stream(
        text,
        instruct=instruction,
        language=language_code,
        normalize=normalize,
        num_step=num_steps,
        guidance_scale=guidance_scale,
        speed=speed,
    ):
        if first_audio_ms is None:
            first_audio_ms = (time.perf_counter() - started) * 1000.0
        if sample_rate is not None and sample_rate != int(current_rate):
            raise RuntimeError("VoiceTut changed its sample rate within a single utterance.")
        sample_rate = int(current_rate)
        audio = np.asarray(chunk, dtype=np.float32).reshape(-1)
        if audio.size:
            chunks.append(audio.copy())
    render_ms = (time.perf_counter() - started) * 1000.0
    if not chunks or sample_rate is None:
        raise RuntimeError("VoiceTut returned no audio chunks for a non-empty prompt.")
    output = np.concatenate(chunks).astype(np.float32, copy=False)
    return sample_rate, output, render_ms, first_audio_ms or render_ms


def build_device_info(active_device: str, ram_mb: int) -> dict[str, Any]:
    gpu = None
    try:
        import torch
        if active_device.startswith("cuda") and torch.cuda.is_available():
            gpu = torch.cuda.get_device_name(0)
    except Exception:
        gpu = active_device if active_device.startswith("cuda") else None
    return {
        "os": platform.platform(),
        "cpu": platform.processor() or platform.machine(),
        "ramMb": ram_mb,
        "gpu": gpu,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, required=True,
                        help="New or empty evaluation directory; WAVs and manifests are written here.")
    parser.add_argument("--prompts", type=Path, default=(
        Path(__file__).resolve().parents[2] / "tests" / "TestAssets" / "Speech" /
        "voice-persona-evaluation.json"))
    parser.add_argument("--hf-home", type=Path, default=default_hf_home())
    parser.add_argument("--device", choices=("cpu", "cuda:0"))
    parser.add_argument("--num-steps", type=int, default=32)
    parser.add_argument("--guidance-scale", type=float, default=2.0)
    parser.add_argument("--speed", type=float, default=1.0)
    parser.add_argument("--instruction", default=DEFAULT_INSTRUCTION)
    parser.add_argument("--ram-mb", type=int, default=0,
                        help="Override automatic system RAM detection if unavailable.")
    args = parser.parse_args()

    if not 4 <= args.num_steps <= 64:
        parser.error("--num-steps must be between 4 and 64.")
    if not 0.0 <= args.guidance_scale <= 6.0:
        parser.error("--guidance-scale must be between 0 and 6.")
    if not 0.5 <= args.speed <= 2.0:
        parser.error("--speed must be between 0.5 and 2.")
    instruction = args.instruction.strip()
    if "female" not in instruction.casefold() or any(
        item in instruction.casefold()
        for item in ("ref_audio", "reference audio", "clone", "speaker=")
    ):
        parser.error("Voice Design instruction must include 'female' and must not request cloning/reference audio.")
    if args.output_dir.exists() and any(args.output_dir.iterdir()):
        parser.error("--output-dir must be new or empty; existing files are never overwritten.")

    try:
        prompt_payload = load_prompt_set(args.prompts)
        ram_mb = args.ram_mb if args.ram_mb > 0 else system_ram_mb()
        engine, device, snapshot_path, weight_files = load_engine(args.hf_home, args.device)
        model_hashes = [sha256_file(path) for path in weight_files]
        import soundfile as sf
        import torch

        sample_root = args.output_dir / "samples"
        sample_root.mkdir(parents=True, exist_ok=True)
        manifest_samples: list[dict[str, Any]] = []
        msa_exploratory: list[dict[str, Any]] = []

        for case in prompt_payload["testCases"]:
            case_id = safe_case_id(case["id"])
            language_code, production_supported = normalize_language_code(case["language"])
            sample_rate, audio, render_ms, ttfa_ms = render_one(
                engine=engine,
                text=case["text"],
                language_code=language_code,
                instruction=instruction,
                num_steps=args.num_steps,
                guidance_scale=args.guidance_scale,
                speed=args.speed,
                normalize=True,
            )
            duration_seconds = len(audio) / float(sample_rate)
            rtf = (render_ms / 1000.0) / duration_seconds if duration_seconds else None
            file_name = f"voicetut__{case_id}__natural.wav"
            audio_path = sample_root / file_name
            sf.write(str(audio_path), audio, sample_rate, subtype="PCM_16")
            digest = sha256_file(audio_path)
            record = {
                "caseId": case_id,
                "audioPath": f"samples/{file_name}",
                "audioSha256": digest,
                "renderTimeMs": round(render_ms, 2),
                "timeToFirstAudioMs": round(ttfa_ms, 2),
                "audioDurationSeconds": round(duration_seconds, 4),
                "sampleRateHz": sample_rate,
                "realTimeFactor": round(rtf, 4) if rtf is not None else None,
                "qualitySteps": args.num_steps,
                "guidanceScale": args.guidance_scale,
                "speed": args.speed,
                "normalizationEnabled": True,
                "humanRatings": [],
            }
            if production_supported:
                manifest_samples.append({
                    "caseId": case_id,
                    "engineId": "voicetut-voice-design",
                    "style": "natural",
                    "audioPath": record["audioPath"],
                    "audioSha256": digest,
                    "renderTimeMs": record["renderTimeMs"],
                    "timeToFirstAudioMs": record["timeToFirstAudioMs"],
                    "ratings": [],
                })
            else:
                record["productionSupported"] = False
                record["note"] = (
                    "Exploratory only: VoiceTut resolves this text through its Egyptian Arabic "
                    "('arz') path; this is not evidence of a separately supported MSA voice."
                )
                msa_exploratory.append(record)

            del audio
            print(
                f"{case_id}: {duration_seconds:.2f}s audio | "
                f"render {render_ms:.0f}ms | TTFA {ttfa_ms:.0f}ms | "
                f"RTF {rtf:.3f}" if rtf is not None else f"{case_id}: rendered"
            )

        manifest = {
            "schemaVersion": 1,
            "runId": "voicetut-" + time.strftime("%Y%m%d-%H%M%S"),
            "device": build_device_info(device, ram_mb),
            "engines": [{
                "id": "voicetut-voice-design",
                "displayName": "VoiceTut-TTS — Voice Design (female / child / very high pitch)",
                "sourceUrl": "https://huggingface.co/mohammedaly22/VoiceTut-TTS",
                "revision": MODEL_REVISION,
                "revisionIsImmutable": True,
                "speakerId": "layal-female-child-high-pitch-voice-design",
                "licenseStatus": "verified",
                "licenseEvidenceUrl": (
                    "https://github.com/MohammedAly22/VoiceTuT-TTS/blob/" +
                    "44cba00cd3ddb04b5213353e19e960045e332da7/LICENSE"
                ),
                "speakerRightsStatus": "pending",
                "speakerRightsEvidenceUrl": None,
                "manualAcceptanceStatus": "pending",
                "voiceCloningEnabled": False,
                "modelSha256": model_hashes,
                "supportedLanguages": [
                    "EgyptianArabic", "English", "MixedArabicEnglish"
                ],
                "supportedStyles": ["natural"],
            }],
            "samples": manifest_samples,
            "exploratoryMsaOutput": (
                "msa-exploratory.json" if msa_exploratory else None
            ),
            "notes": [
                "No built-in reference speaker audio was downloaded or loaded.",
                "Voice Design only; reference audio and voice cloning are disabled.",
                "MSA outputs are exploratory and are excluded from supportedLanguages and production coverage.",
                "The UI delivery presets alter generation speed; they are not verified emotional styles.",
                "Two independent human ratings per sample and manual acceptance are still required.",
            ],
        }
        args.output_dir.mkdir(parents=True, exist_ok=True)
        (args.output_dir / "run.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        (args.output_dir / "msa-exploratory.json").write_text(
            json.dumps({
                "supportedAsProduction": False,
                "reason": "VoiceTut documents Egyptian Arabic and English; this MSA pass used the Egyptian arz route and is exploratory only.",
                "samples": msa_exploratory,
            }, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"\nWrote evaluation files to: {args.output_dir}")
        print("Next: run score_tts_candidates.py on run.json; human reviews remain pending.")
        return 0
    except KeyboardInterrupt:
        print("Evaluation cancelled; partial output is retained and must not be treated as a complete run.", file=sys.stderr)
        return 130
    except Exception as error:
        print(f"VoiceTut evaluation failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
