#!/usr/bin/env python3
"""Render Layal's fixed Piper baseline prompts without downloading or installing anything.

The renderer uses only Python's standard library and the exact Piper files already
pinned by LocalVoiceService. It writes PCM WAV files plus a score_tts_candidates.py
compatible run manifest. It never records microphone audio or changes production files.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
import platform
import re
import shutil
import stat
import subprocess
import sys
import time
import uuid
import wave
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ENGINE_ID = "piper-baseline"
PIPER_RELEASE = "2023.11.14-2"
PIPER_RELEASE_URL = "https://github.com/rhasspy/piper/releases/tag/2023.11.14-2"
ARABIC_MODEL_URL = (
    "https://huggingface.co/rhasspy/piper-voices/blob/main/"
    "ar/ar_JO/kareem/medium/ar_JO-kareem-medium.onnx"
)
ENGLISH_MODEL_URL = (
    "https://huggingface.co/rhasspy/piper-voices/blob/main/"
    "en/en_US/lessac/medium/en_US-lessac-medium.onnx"
)
DEFAULT_PROMPTS = (
    Path(__file__).resolve().parents[2]
    / "tests" / "TestAssets" / "Speech" / "voice-persona-evaluation.json"
)
LANGUAGES = ("EgyptianArabic", "ArabicStandard", "English", "MixedArabicEnglish")
STYLES = (
    "natural", "gentle", "bright", "clear",
    "empathetic", "serious", "energetic", "playful",
)
MAX_WAV_BYTES = 64 * 1024 * 1024
MAX_TEXT_LENGTH = 6000
REPARSE_POINT_ATTRIBUTE = 0x400


class AssetSpec:
    def __init__(self, relative_path: str, size_bytes: int, sha256: str, label: str):
        self.relative_path = relative_path
        self.size_bytes = size_bytes
        self.sha256 = sha256
        self.label = label


# These values intentionally mirror the integrity pins in LocalVoiceService.cs.
PIPER_RUNTIME_ASSETS = (
    AssetSpec(
        "piper/espeak-ng.dll", 380_928,
        "9588480f8197df62fd8461a8431f8eaec6e8e7749c5ffcbe7fee656fe40a2189",
        "eSpeak-NG runtime",
    ),
    AssetSpec(
        "piper/libtashkeel_model.ort", 10_261_536,
        "9f27090af3e0f661913af048a739632a3f577b3233512551d8c90580ccad4aa8",
        "Piper tashkeel runtime",
    ),
    AssetSpec(
        "piper/onnxruntime_providers_shared.dll", 22_424,
        "71705b9bd76baced583eb37fe5ec2101946b4de50f1e9bdb156109f7e8082980",
        "ONNX Runtime shared provider",
    ),
    AssetSpec(
        "piper/onnxruntime.dll", 9_271_704,
        "a630f67f4a82b6689e4178bf81d362d945522dd907b2a92cb43cebf72c83a06f",
        "ONNX Runtime",
    ),
    AssetSpec(
        "piper/piper_phonemize.dll", 407_040,
        "4b5f344b2f31204a8a94a0bf485f93e4971671e81188a0d67f326e113bfb0b2e",
        "Piper phonemizer",
    ),
    AssetSpec(
        "piper/piper.exe", 509_952,
        "96f3da3811151580073e40bb4dd20eb0fb8115f5f5f76e2fb54282b3edfa5c1f",
        "Piper executable",
    ),
)
VOICE_ASSETS = (
    AssetSpec(
        "ar_JO-kareem-medium.onnx", 63_201_294,
        "9e95cab07b679da603bba17c4dec7ab3111320571964ee95c0379603c086491e",
        "Arabic Piper voice model",
    ),
    AssetSpec(
        "ar_JO-kareem-medium.onnx.json", 5_024,
        "ea6d9b9d9076dbdb6bf5c98c6a141ef154959d2359709b37855727964e7d6c4d",
        "Arabic Piper voice config",
    ),
    AssetSpec(
        "en_US-lessac-medium.onnx", 63_201_294,
        "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f",
        "English Piper voice model",
    ),
    AssetSpec(
        "en_US-lessac-medium.onnx.json", 4_885,
        "efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0",
        "English Piper voice config",
    ),
)
VOICE_MODELS = {
    True: ("ar_JO-kareem-medium.onnx", "ar_JO-kareem-medium.onnx.json", "ar_JO-kareem-medium"),
    False: ("en_US-lessac-medium.onnx", "en_US-lessac-medium.onnx.json", "en_US-lessac-medium"),
}

# Exactly mirrors VoiceDeliveryStylePolicy.GetLocalLengthScale and
# GetLocalSentenceSilenceSeconds. These settings change pacing, not emotion or timbre.
STYLE_SETTINGS = {
    "natural": (1.00, 0.20),
    "gentle": (1.10, 0.36),
    "bright": (0.95, 0.16),
    "clear": (1.03, 0.28),
    "empathetic": (1.08, 0.40),
    "serious": (1.05, 0.34),
    "energetic": (0.92, 0.12),
    "playful": (0.97, 0.14),
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def reject_reparse_points(path: Path) -> None:
    """Reject symlinks/junctions in every existing component of a supplied path."""
    cursor = Path(os.path.abspath(path))
    while True:
        try:
            info = cursor.lstat()
        except FileNotFoundError:
            pass
        else:
            attributes = getattr(info, "st_file_attributes", 0)
            if stat.S_ISLNK(info.st_mode) or attributes & REPARSE_POINT_ATTRIBUTE:
                raise ValueError(f"رفض استخدام مسار يحتوي على رابط رمزي أو reparse point: {cursor}")
        if cursor.parent == cursor:
            break
        cursor = cursor.parent


def verify_asset(root: Path, asset: AssetSpec) -> str:
    path = root / Path(asset.relative_path)
    reject_reparse_points(path)
    if not path.is_file():
        raise FileNotFoundError(f"{asset.label} غير موجود: {path}")
    actual_size = path.stat().st_size
    if actual_size != asset.size_bytes:
        raise ValueError(
            f"حجم {asset.label} غير مطابق: expected={asset.size_bytes}, actual={actual_size}, path={path}"
        )
    actual_hash = sha256_file(path)
    if actual_hash.lower() != asset.sha256.lower():
        raise ValueError(
            f"SHA-256 الخاص بـ {asset.label} غير مطابق للبصمة المثبتة. "
            "لن يتم تشغيل ملف غير متحقق منه."
        )
    return actual_hash


def verify_assets(piper_root: Path, models_dir: Path) -> dict[str, Any]:
    piper_root = piper_root.expanduser().absolute()
    models_dir = models_dir.expanduser().absolute()
    reject_reparse_points(piper_root)
    reject_reparse_points(models_dir)
    if not piper_root.is_dir():
        raise NotADirectoryError(f"Piper root غير موجود: {piper_root}")
    if not models_dir.is_dir():
        raise NotADirectoryError(f"مجلد نماذج Piper غير موجود: {models_dir}")

    runtime_hashes: dict[str, str] = {}
    for asset in PIPER_RUNTIME_ASSETS:
        runtime_hashes[asset.relative_path] = verify_asset(piper_root, asset)
    voice_hashes: dict[str, str] = {}
    for asset in VOICE_ASSETS:
        voice_hashes[asset.relative_path] = verify_asset(models_dir, asset)

    for config_name in ("ar_JO-kareem-medium.onnx.json", "en_US-lessac-medium.onnx.json"):
        config = json.loads((models_dir / config_name).read_text(encoding="utf-8"))
        sample_rate = config.get("audio", {}).get("sample_rate")
        if sample_rate != 22050:
            raise ValueError(
                f"معدل عينة {config_name} تغيّر عن القيمة المثبتة 22050 Hz: {sample_rate!r}"
            )

    return {
        "piperExecutable": piper_root / "piper" / "piper.exe",
        "runtimeSha256": runtime_hashes,
        "voiceSha256": voice_hashes,
        "sampleRateHz": 22050,
    }


def normalize_text_for_speech(text: str) -> str:
    """Match LocalVoiceService.NormalizeTextForSpeech for the fixed prompt corpus."""
    value = text or ""
    tick = chr(96)
    value = re.sub(r"(?s)" + re.escape(tick * 3) + r".*?" + re.escape(tick * 3), " ", value)
    inline_code = re.escape(tick) + r"([^" + re.escape(tick) + r"]*)" + re.escape(tick)
    value = re.sub(inline_code, r"\1", value)
    value = re.sub(r"!\[([^\]]*)\]\([^)]+\)", r"\1", value)
    value = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", value)
    value = re.sub(r"https?://\S+", " ", value)
    value = re.sub(r"(?m)^\s{0,3}#{1,6}\s*", "", value)
    value = re.sub(r"(?m)^\s*[-*+]\s+", "", value)
    value = re.sub(r"(?m)^\s*\d+[.)]\s+", "", value)
    value = re.sub(r"[*_~]", "", value)
    value = re.sub(r"\s+", " ", value).strip()
    return value[:MAX_TEXT_LENGTH]


def split_text_by_language(text: str) -> list[tuple[str, bool]]:
    """Mirror LocalVoiceService.SplitTextByLanguage; True selects the Arabic voice."""
    normalized = normalize_text_for_speech(text)
    if not normalized:
        return []

    segments: list[tuple[str, bool]] = []
    current: list[str] = []
    current_language: bool | None = None
    pending: list[str] = []

    for token in re.findall(
        r"[\u0600-\u06FF]+|[A-Za-z0-9]+|[^A-Za-z0-9\u0600-\u06FF]+",
        normalized,
    ):
        has_arabic = any("\u0600" <= character <= "\u06FF" for character in token)
        has_latin_or_number = any(
            "A" <= character <= "Z"
            or "a" <= character <= "z"
            or "0" <= character <= "9"
            for character in token
        )
        if not has_arabic and not has_latin_or_number:
            if current_language is None:
                pending.append(token)
            else:
                current.append(token)
            continue

        next_language = has_arabic
        if current_language is None:
            current_language = next_language
            current.extend(pending)
            pending.clear()
        elif current_language != next_language:
            part = "".join(current).strip()
            if part:
                segments.append((part, current_language))
            current.clear()
            current_language = next_language
        current.append(token)

    if current_language is not None and current:
        part = "".join(current).strip()
        if part:
            segments.append((part, current_language))
    elif pending:
        part = "".join(pending).strip()
        if part:
            segments.append((part, False))
    return segments


def collect_total_memory_mb() -> int:
    if sys.platform == "win32":
        class MemoryStatusEx(ctypes.Structure):
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

        status = MemoryStatusEx()
        status.dwLength = ctypes.sizeof(status)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)  # type: ignore[attr-defined]
        api = kernel32.GlobalMemoryStatusEx
        api.argtypes = [ctypes.POINTER(MemoryStatusEx)]
        api.restype = ctypes.c_int
        if not api(ctypes.byref(status)):
            raise OSError(ctypes.get_last_error(), "GlobalMemoryStatusEx failed")
        total = int(status.ullTotalPhys)
    else:
        total = int(os.sysconf("SC_PAGE_SIZE")) * int(os.sysconf("SC_PHYS_PAGES"))
    if total <= 0:
        raise RuntimeError("تعذر قراءة إجمالي RAM من نظام التشغيل.")
    return int(round(total / (1024 * 1024)))


def collect_gpu_label() -> str | None:
    try:
        result = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=name,memory.total",
                "--format=csv,noheader,nounits",
            ],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=5,
        )
    except (FileNotFoundError, OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0:
        return None
    entries = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    if not entries:
        return None
    return "; ".join(f"{line} MiB VRAM" for line in entries)


def collect_device() -> dict[str, Any]:
    cpu = platform.processor().strip() or platform.machine() or "Processor model unavailable"
    return {
        "os": f"{platform.system()} {platform.release()} ({platform.version()})",
        "cpu": cpu,
        "ramMb": collect_total_memory_mb(),
        "gpu": collect_gpu_label(),
    }


def load_prompts(path: Path) -> list[dict[str, str]]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict) or payload.get("schemaVersion") != 1:
        raise ValueError("ملف العبارات يجب أن يستخدم schemaVersion 1.")
    cases = payload.get("testCases")
    if not isinstance(cases, list) or not cases:
        raise ValueError("ملف العبارات لا يحتوي على testCases صالحة.")
    result: list[dict[str, str]] = []
    seen: set[str] = set()
    for index, case in enumerate(cases, start=1):
        if not isinstance(case, dict):
            raise ValueError(f"العبارة رقم {index} ليست كائن JSON.")
        case_id = case.get("id")
        language = case.get("language")
        text = case.get("text")
        if not isinstance(case_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", case_id):
            raise ValueError(f"معرّف العبارة رقم {index} غير صالح.")
        if case_id in seen:
            raise ValueError(f"معرّف العبارة مكرر: {case_id}")
        if language not in LANGUAGES or not isinstance(text, str) or not text.strip():
            raise ValueError(f"بيانات العبارة {case_id} غير صالحة.")
        seen.add(case_id)
        result.append({"id": case_id, "language": language, "text": text})
    return result


def validate_pcm_wav(path: Path) -> tuple[Any, bytes]:
    if not path.is_file() or path.stat().st_size <= 44:
        raise ValueError(f"Piper لم ينشئ ملف WAV صالحًا: {path}")
    if path.stat().st_size > MAX_WAV_BYTES:
        raise ValueError(f"ملف WAV تجاوز الحد الآمن {MAX_WAV_BYTES} bytes: {path}")
    try:
        with wave.open(str(path), "rb") as audio:
            params = audio.getparams()
            if params.comptype != "NONE":
                raise ValueError(f"WAV ليس PCM غير مضغوط: {path}")
            if params.nchannels != 1 or params.sampwidth != 2:
                raise ValueError(f"WAV يجب أن يكون mono PCM 16-bit: {path}")
            if params.framerate <= 0 or params.nframes <= 0:
                raise ValueError(f"WAV لا يحتوي على عينات صالحة: {path}")
            frames = audio.readframes(params.nframes)
            if len(frames) != params.nframes * params.nchannels * params.sampwidth:
                raise ValueError(f"بيانات WAV غير مكتملة: {path}")
            return params, frames
    except (wave.Error, EOFError) as error:
        raise ValueError(f"تعذر قراءة WAV الناتج من Piper: {error}") from error


def write_joined_wav(output_path: Path, parts: list[tuple[Any, bytes]]) -> None:
    if not parts:
        raise ValueError("لا توجد مقاطع صوتية لدمجها.")
    expected = parts[0][0]
    for params, _frames in parts[1:]:
        if (
            params.nchannels != expected.nchannels
            or params.sampwidth != expected.sampwidth
            or params.framerate != expected.framerate
            or params.comptype != expected.comptype
        ):
            raise ValueError(
                "مقاطع Piper ليست متوافقة في تنسيق WAV؛ أوقفنا الدمج بدل تغيير الصوت أو معدل العينات."
            )
    with wave.open(str(output_path), "wb") as output:
        output.setnchannels(expected.nchannels)
        output.setsampwidth(expected.sampwidth)
        output.setframerate(expected.framerate)
        output.setcomptype("NONE", "not compressed")
        for _params, frames in parts:
            output.writeframes(frames)


def run_piper_segment(
    piper_executable: Path,
    model_path: Path,
    config_path: Path,
    text: str,
    output_path: Path,
    length_scale: float,
    sentence_silence: float,
    timeout_seconds: float,
) -> tuple[float, tuple[Any, bytes]]:
    arguments = [
        str(piper_executable),
        "--model", str(model_path),
        "--config", str(config_path),
        "--output_file", str(output_path),
        "--length_scale", f"{length_scale:.2f}",
        "--sentence_silence", f"{sentence_silence:.2f}",
    ]
    started = time.perf_counter()
    try:
        result = subprocess.run(
            arguments,
            input=text + "\n",
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_seconds,
            cwd=str(piper_executable.parent),
            shell=False,
        )
    except subprocess.TimeoutExpired as error:
        raise TimeoutError(
            f"تجاوز Piper مهلة {timeout_seconds:g} ثانية أثناء توليد مقطع صوتي."
        ) from error
    elapsed_ms = max(0.0, (time.perf_counter() - started) * 1000.0)
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "").strip()[:700]
        raise RuntimeError(f"Piper انتهى برمز {result.returncode}. {detail}")
    params, frames = validate_pcm_wav(output_path)
    return elapsed_ms, (params, frames)


def style_settings(style: str) -> tuple[float, float]:
    try:
        return STYLE_SETTINGS[style]
    except KeyError as error:
        raise ValueError(f"نمط إلقاء غير معروف: {style}") from error


def render_prompt(
    *,
    prompt: dict[str, str],
    style: str,
    piper_executable: Path,
    models_dir: Path,
    temporary_dir: Path,
    output_path: Path,
    timeout_seconds: float = 120.0,
) -> dict[str, Any]:
    text = normalize_text_for_speech(prompt["text"])
    segments = split_text_by_language(text)
    if not segments:
        raise ValueError(f"العبارة {prompt['id']} أصبحت فارغة بعد التطبيع.")

    length_scale, sentence_silence = style_settings(style)
    parts: list[tuple[Any, bytes]] = []
    segment_routes: list[str] = []
    first_audio_ms: float | None = None
    started = time.perf_counter()

    for index, (segment_text, is_arabic) in enumerate(segments, start=1):
        model_name, config_name, route_name = VOICE_MODELS[is_arabic]
        segment_output = temporary_dir / f"{prompt['id']}__{style}__{index}.wav"
        segment_output.unlink(missing_ok=True)
        try:
            _elapsed_ms, wav_part = run_piper_segment(
                piper_executable=piper_executable,
                model_path=models_dir / model_name,
                config_path=models_dir / config_name,
                text=segment_text,
                output_path=segment_output,
                length_scale=length_scale,
                sentence_silence=sentence_silence,
                timeout_seconds=timeout_seconds,
            )
            parts.append(wav_part)
            segment_routes.append(route_name)
            if first_audio_ms is None:
                first_audio_ms = (time.perf_counter() - started) * 1000.0
        finally:
            segment_output.unlink(missing_ok=True)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    write_joined_wav(output_path, parts)
    render_ms = max(1, math.ceil((time.perf_counter() - started) * 1000.0))
    ttfa_ms = max(0, min(render_ms, round(first_audio_ms or 0.0)))
    digest = sha256_file(output_path)
    return {
        "caseId": prompt["id"],
        "engineId": ENGINE_ID,
        "language": prompt["language"],
        "style": style,
        "audioPath": output_path.relative_to(output_path.parents[1]).as_posix(),
        "audioSha256": digest,
        "renderTimeMs": render_ms,
        "timeToFirstAudioMs": ttfa_ms,
        "ratings": [],
        "segmentCount": len(segments),
        "voiceRoutes": segment_routes,
        "lengthScale": length_scale,
        "sentenceSilenceSeconds": sentence_silence,
    }


def build_engine_record(asset_info: dict[str, Any]) -> dict[str, Any]:
    ar_hash = asset_info["voiceSha256"]["ar_JO-kareem-medium.onnx"]
    en_hash = asset_info["voiceSha256"]["en_US-lessac-medium.onnx"]
    return {
        "id": ENGINE_ID,
        "displayName": "Piper baseline (ar_JO-kareem-medium + en_US-lessac-medium)",
        "sourceUrl": PIPER_RELEASE_URL,
        "revision": (
            f"Piper {PIPER_RELEASE}; Arabic ONNX SHA-256 {ar_hash}; "
            f"English ONNX SHA-256 {en_hash}"
        ),
        "revisionIsImmutable": True,
        "speakerId": "piper-dual-voice-baseline-arjo-kareem-enus-lessac",
        "licenseStatus": "pending",
        "licenseEvidenceUrl": None,
        "speakerRightsStatus": "pending",
        "speakerRightsEvidenceUrl": None,
        "manualAcceptanceStatus": "pending",
        "voiceCloningEnabled": False,
        "modelSha256": [ar_hash, en_hash],
        "supportedLanguages": list(LANGUAGES),
        "supportedStyles": list(STYLES),
        "runtimeSha256": asset_info["runtimeSha256"],
        "voiceConfigSha256": {
            key: value for key, value in asset_info["voiceSha256"].items()
            if key.endswith(".onnx.json")
        },
        "voiceModelNames": ["ar_JO-kareem-medium", "en_US-lessac-medium"],
        "voiceIdentityNote": (
            "This is a composite application baseline with two different Piper voice models, "
            "not one shared speaker identity across languages."
        ),
        "coverageNote": (
            "Language entries describe application routing coverage for this benchmark only. "
            "They do not assert Cairene dialect quality or native support for code-switching."
        ),
        "styleControlNote": (
            "The eight presets change length_scale and sentence_silence only. "
            "Piper's bundled voices do not expose semantic emotion or timbre controls."
        ),
    }


def write_json_atomic(path: Path, payload: dict[str, Any]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    temporary.replace(path)


def create_run_manifest(
    *,
    run_id: str,
    device: dict[str, Any],
    engine: dict[str, Any],
    prompts_path: Path,
) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "runId": run_id,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "device": device,
        "engines": [engine],
        "samples": [],
        "renderer": {
            "name": "render_piper_baseline.py",
            "version": 1,
            "piperRelease": PIPER_RELEASE,
            "promptsFile": str(prompts_path),
            "mode": "local-only; no model downloads, package installs, or microphone capture",
            "sampleJoin": "PCM segments are concatenated in the same order as LocalVoiceService playback; no extra inter-language pause is inserted",
            "timeToFirstAudioDefinition": (
                "Elapsed time from sample start until the first language segment WAV is ready. "
                "This is the app's first-segment synthesis readiness, not a streaming token metric."
            ),
            "qualityLimitations": [
                "Arabic prompts use the bundled Jordanian Arabic voice, not a dedicated Cairene voice.",
                "English prompts use a different speaker model; identity continuity across languages is not provided.",
                "The eight styles change pacing and sentence silence only, not emotional tone or speaker timbre.",
                "Human ratings, license review, and speaker-rights review remain pending.",
            ],
        },
        "generationStatus": "in_progress",
        "expectedSamples": None,
        "renderedSamples": 0,
    }


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--piper-root", type=Path, required=True,
                        help="Existing Layal LocalAi/tools/piper extraction root containing piper/piper.exe")
    parser.add_argument("--models-dir", type=Path, required=True,
                        help="Existing Layal LocalAi/speech-models directory")
    parser.add_argument("--output-dir", type=Path, required=True,
                        help="New, non-existing run directory for WAV files and run.json")
    parser.add_argument("--prompts", type=Path, default=DEFAULT_PROMPTS,
                        help="Fixed voice persona prompt JSON")
    parser.add_argument("--timeout-seconds", type=float, default=120.0,
                        help="Maximum time for each Piper segment (default: 120)")
    parser.add_argument("--limit-cases", type=int, default=None,
                        help="Optional smoke test: render only the first N prompts across all eight styles")
    parser.add_argument("--run-id", default=None,
                        help="Optional safe run identifier; otherwise generated from UTC time")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
            raise ValueError("--timeout-seconds يجب أن يكون رقمًا موجبًا.")
        if args.limit_cases is not None and args.limit_cases <= 0:
            raise ValueError("--limit-cases يجب أن يكون عددًا موجبًا.")
        run_id = args.run_id or (
            "piper-baseline-" + datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
            + "-" + uuid.uuid4().hex[:8]
        )
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,100}", run_id) or run_id in {".", ".."}:
            raise ValueError("--run-id يحتوي على أحرف غير مسموح بها.")

        prompts_path = args.prompts.expanduser().absolute()
        reject_reparse_points(prompts_path)
        prompts = load_prompts(prompts_path)
        if args.limit_cases is not None:
            prompts = prompts[:args.limit_cases]
        expected_samples = len(prompts) * len(STYLES)

        # Verify every local runtime/model artifact before creating outputs or launching Piper.
        asset_info = verify_assets(args.piper_root, args.models_dir)
        device = collect_device()
        output_dir = args.output_dir.expanduser().absolute()
        reject_reparse_points(output_dir)
        if output_dir.exists():
            raise FileExistsError(
                f"مجلد التجربة موجود بالفعل؛ لن نستبدل أو نمسح ملفات سابقة: {output_dir}"
            )
        output_dir.parent.mkdir(parents=True, exist_ok=True)
        output_dir.mkdir(parents=False, exist_ok=False)
        samples_dir = output_dir / "samples"
        temporary_dir = output_dir / "tmp"
        samples_dir.mkdir()
        temporary_dir.mkdir()

        manifest_path = output_dir / "run.json"
        manifest = create_run_manifest(
            run_id=run_id,
            device=device,
            engine=build_engine_record(asset_info),
            prompts_path=prompts_path,
        )
        manifest["expectedSamples"] = expected_samples
        write_json_atomic(manifest_path, manifest)

        try:
            piper_executable = asset_info["piperExecutable"]
            models_dir = args.models_dir.expanduser().absolute()
            for prompt in prompts:
                for style in STYLES:
                    file_name = f"piper-baseline__{prompt['id']}__{style}.wav"
                    output_path = samples_dir / file_name
                    record = render_prompt(
                        prompt=prompt,
                        style=style,
                        piper_executable=piper_executable,
                        models_dir=models_dir,
                        temporary_dir=temporary_dir,
                        output_path=output_path,
                        timeout_seconds=args.timeout_seconds,
                    )
                    manifest["samples"].append(record)
                    manifest["renderedSamples"] = len(manifest["samples"])
                    write_json_atomic(manifest_path, manifest)
                    print(
                        f"[{manifest['renderedSamples']}/{expected_samples}] "
                        f"{prompt['id']} / {style}: {record['renderTimeMs']} ms"
                    )
            manifest["generationStatus"] = "completed"
            write_json_atomic(manifest_path, manifest)
        except Exception as error:
            manifest["generationStatus"] = "failed"
            manifest["failure"] = f"{error.__class__.__name__}: {error}"
            write_json_atomic(manifest_path, manifest)
            raise
        finally:
            shutil.rmtree(temporary_dir, ignore_errors=True)

        print(f"\nاكتمل توليد {len(manifest['samples'])} عينة.")
        print(f"Manifest: {manifest_path}")
        print("Human ratings and license/speaker-rights acceptance remain pending.")
        return 0
    except (OSError, RuntimeError, ValueError, TimeoutError, json.JSONDecodeError) as error:
        print(f"تعذر إكمال توليد Piper baseline: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
