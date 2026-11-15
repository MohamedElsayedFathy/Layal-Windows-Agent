#!/usr/bin/env python3
"""Layal's loopback-only VoiceTut bridge.

Only Voice Design is exposed. This bridge never loads reference speaker metadata,
never serves built-in speaker samples, and does not accept reference audio.
"""
from __future__ import annotations

import argparse
import base64
import json
import logging
import math
import os
import threading
import time
import traceback
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

MODEL_ID = "mohammedaly22/VoiceTut-TTS"
MODEL_REVISION = "b36bdf4416864d84f47ca77ce3decf34f6ab16cc"
MAX_TEXT_CHARS = 6000
MAX_BODY_BYTES = 1_000_000
DEFAULT_INSTRUCTION = "female, child, very high pitch"
LOG = logging.getLogger("layal.voicetut")


class EngineState:
    def __init__(self) -> None:
        self.engine = None
        self.device = "unknown"
        self.sample_rate = 24000
        self.error: str | None = None
        self.metrics: deque[dict[str, Any]] = deque(maxlen=30)
        self.lock = threading.Lock()
        self.inference_lock = threading.Lock()


STATE = EngineState()


def load_engine() -> None:
    import torch
    from huggingface_hub import snapshot_download
    from voicetut_tts import VoiceTutTTS

    hf_home = Path(os.environ["HF_HOME"])
    hf_home.mkdir(parents=True, exist_ok=True)
    LOG.info("Downloading/verifying VoiceTut model revision %s (reference audio excluded).", MODEL_REVISION)
    model_path = snapshot_download(
        repo_id=MODEL_ID,
        revision=MODEL_REVISION,
        allow_patterns=[
            "config.json",
            "model.safetensors",
            "tokenizer.json",
            "tokenizer_config.json",
            "chat_template.jinja",
        ],
    )

    device = "cuda:0" if torch.cuda.is_available() else "cpu"
    dtype = "float16" if torch.cuda.is_available() else "float32"
    # A non-existent references path intentionally disables the built-in speaker registry.
    # This avoids downloading or loading any of the model card's example speaker clips.
    disabled_references = str(hf_home / "layal-reference-speakers-disabled.json")
    STATE.engine = VoiceTutTTS.from_pretrained(
        model_path,
        references=disabled_references,
        device=device,
        dtype=dtype,
        language="arz",
    )
    STATE.device = device
    STATE.sample_rate = int(STATE.engine.sampling_rate)
    STATE.error = None
    LOG.info("VoiceTut is ready on %s at %s Hz; voice mode=Voice Design only.", device, STATE.sample_rate)


def validate_request(payload: dict[str, Any]) -> tuple[str, str, str, int, float, float, bool]:
    text = str(payload.get("text") or "").strip()
    if not text:
        raise ValueError("النص فارغ.")
    if len(text) > MAX_TEXT_CHARS:
        raise ValueError(f"النص أطول من الحد المسموح ({MAX_TEXT_CHARS} حرف).")

    language = str(payload.get("language") or "arz").strip().lower()
    if language not in {"arz", "ar", "en"}:
        raise ValueError("اللغة المسموحة هي المصرية/العربية أو الإنجليزية.")

    instruction = str(payload.get("instruct") or DEFAULT_INSTRUCTION).strip()
    if not instruction or len(instruction) > 300:
        raise ValueError("وصف الصوت غير صالح.")
    lowered = instruction.casefold()
    if "female" not in lowered:
        raise ValueError("يجب أن يظل تصميم الصوت أنثويًا (female).")
    forbidden = ("ref_audio", "reference audio", "speaker=", "clone", "voice cloning")
    if any(token in lowered for token in forbidden):
        raise ValueError("هذه الواجهة تسمح بتصميم الصوت فقط، وليس الاستنساخ أو الصوت المرجعي.")

    steps_raw = payload.get("num_steps", 32)
    guidance_raw = payload.get("guidance_scale", 2.0)
    speed_raw = payload.get("speed", 1.0)
    normalize = payload.get("normalize", True)
    if isinstance(steps_raw, bool) or not isinstance(steps_raw, (int, float)) or not float(steps_raw).is_integer():
        raise ValueError("عدد خطوات الجودة يجب أن يكون عددًا صحيحًا.")
    if not isinstance(normalize, bool):
        raise ValueError("خيار التطبيع يجب أن يكون true أو false.")
    steps = int(steps_raw)
    guidance = float(guidance_raw)
    speed = float(speed_raw)
    if not math.isfinite(guidance) or not math.isfinite(speed):
        raise ValueError("قيمة التوجيه والسرعة يجب أن تكون أرقامًا محدودة.")
    if not 4 <= steps <= 64:
        raise ValueError("عدد خطوات الجودة يجب أن يكون بين 4 و64.")
    if not 0.0 <= guidance <= 6.0:
        raise ValueError("قيمة التوجيه يجب أن تكون بين 0 و6.")
    if not 0.5 <= speed <= 2.0:
        raise ValueError("سرعة النطق يجب أن تكون بين 0.5 و2.")
    return text, language, instruction, steps, guidance, speed, normalize


class Handler(BaseHTTPRequestHandler):
    server_version = "LayalVoiceTut/1.0"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: Any) -> None:
        LOG.info("%s - %s", self.client_address[0], fmt % args)

    def _authorized(self) -> bool:
        expected = os.environ.get("LAYAL_VOICETUT_TOKEN", "")
        supplied = self.headers.get("X-Layal-Token", "")
        import hmac
        return bool(expected and supplied and hmac.compare_digest(expected, supplied))

    def _json(self, status: int, payload: dict[str, Any]) -> None:
        raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(raw)

    def _read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length", "0"))
        if length <= 0 or length > MAX_BODY_BYTES:
            raise ValueError("حجم الطلب غير صالح.")
        raw = self.rfile.read(length)
        value = json.loads(raw.decode("utf-8"))
        if not isinstance(value, dict):
            raise ValueError("صيغة الطلب غير صالحة.")
        return value

    def do_GET(self) -> None:
        if not self._authorized():
            self._json(401, {"error": "unauthorized"})
            return
        if self.path == "/healthz":
            self._json(200, {"ready": STATE.engine is not None, "device": STATE.device,
                             "sampleRate": STATE.sample_rate, "modelRevision": MODEL_REVISION})
            return
        if self.path == "/v1/info":
            self._json(200, {"model": MODEL_ID, "revision": MODEL_REVISION, "device": STATE.device,
                             "sampleRate": STATE.sample_rate, "voiceMode": "voice-design-only",
                             "builtInVoicesLoaded": False, "referenceAudioEnabled": False})
            return
        if self.path == "/v1/metrics":
            with STATE.lock:
                rows = list(STATE.metrics)
            self._json(200, {"items": rows})
            return
        self._json(404, {"error": "not_found"})

    def do_POST(self) -> None:
        if not self._authorized():
            self._json(401, {"error": "unauthorized"})
            return
        try:
            payload = self._read_json()
            if self.path == "/v1/normalize":
                text = str(payload.get("text") or "")
                if len(text) > MAX_TEXT_CHARS:
                    raise ValueError("النص أطول من الحد المسموح.")
                language = str(payload.get("language") or "arz")
                if language not in {"arz", "ar", "en"}:
                    raise ValueError("اللغة غير مدعومة.")
                normalized = STATE.engine.normalizer.normalize(text, language=language)
                self._json(200, {"normalizedText": normalized, "language": language})
                return
            if self.path == "/v1/stream":
                self._stream(payload)
                return
            if self.path == "/v1/synthesize":
                self._synthesize(payload)
                return
            self._json(404, {"error": "not_found"})
        except (ValueError, TypeError, json.JSONDecodeError) as exc:
            self._json(400, {"error": str(exc)})
        except (BrokenPipeError, ConnectionResetError):
            LOG.info("Client disconnected during local speech request.")
        except Exception as exc:
            LOG.exception("VoiceTut request failed.")
            try:
                self._json(500, {"error": str(exc)[:500]})
            except (BrokenPipeError, ConnectionResetError):
                pass

    def _stream(self, payload: dict[str, Any]) -> None:
        text, language, instruction, steps, guidance, speed, normalize = validate_request(payload)
        self.send_response(200)
        self.send_header("Content-Type", "application/x-ndjson; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Connection", "close")
        self.close_connection = True
        self.end_headers()

        started = time.perf_counter()
        first_audio_ms: float | None = None
        total_audio_seconds = 0.0
        chunk_count = 0
        pcm_peak = 0.0
        square_sum = 0.0
        sample_count = 0

        def emit(record: dict[str, Any]) -> None:
            line = json.dumps(record, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
            self.wfile.write(line)
            self.wfile.flush()

        emit({"type": "meta", "sampleRate": STATE.sample_rate, "channels": 1, "sampleFormat": "s16le"})
        try:
            import numpy as np
            import torch

            if torch.cuda.is_available():
                torch.cuda.reset_peak_memory_stats()
            with STATE.inference_lock:
                for sample_rate, chunk in STATE.engine.stream(
                    text,
                    instruct=instruction,
                    language=language,
                    normalize=normalize,
                    num_step=steps,
                    guidance_scale=guidance,
                    speed=speed,
                ):
                    wave = np.asarray(chunk, dtype=np.float32).reshape(-1)
                    if sample_rate != STATE.sample_rate:
                        raise RuntimeError("معدل أخذ العينات تغيّر أثناء البث.")
                    if wave.size == 0:
                        continue
                    now = time.perf_counter()
                    if first_audio_ms is None:
                        first_audio_ms = (now - started) * 1000.0
                    total_audio_seconds += wave.size / float(sample_rate)
                    pcm_peak = max(pcm_peak, float(np.max(np.abs(wave))))
                    square_sum += float(np.sum(np.square(wave, dtype=np.float64)))
                    sample_count += int(wave.size)
                    pcm = (np.clip(wave, -1.0, 1.0) * 32767.0).astype("<i2", copy=False).tobytes()
                    chunk_count += 1
                    emit({
                        "type": "chunk",
                        "sequence": chunk_count,
                        "sampleRate": int(sample_rate),
                        "pcm16": base64.b64encode(pcm).decode("ascii"),
                    })

            elapsed = time.perf_counter() - started
            rms = (square_sum / sample_count) ** 0.5 if sample_count else 0.0
            peak_vram_mb = (
                round(torch.cuda.max_memory_allocated() / (1024 ** 2), 2)
                if torch.cuda.is_available() else None
            )
            metrics = {
                "elapsedSeconds": round(elapsed, 4),
                "timeToFirstAudioMs": round(first_audio_ms or elapsed * 1000.0, 2),
                "audioDurationSeconds": round(total_audio_seconds, 4),
                "realTimeFactor": round(elapsed / total_audio_seconds, 4) if total_audio_seconds else None,
                "chunkCount": chunk_count,
                "sampleRate": STATE.sample_rate,
                "device": STATE.device,
                "peakLevel": round(pcm_peak, 5),
                "rmsLevel": round(rms, 5),
                "peakVramMb": peak_vram_mb,
                "qualitySteps": steps,
                "guidanceScale": guidance,
                "speed": speed,
                "normalizationEnabled": normalize,
            }
            with STATE.lock:
                STATE.metrics.appendleft(metrics)
            emit({"type": "done", "metrics": metrics})
        except (BrokenPipeError, ConnectionResetError):
            LOG.info("Client disconnected during VoiceTut streaming.")
            raise
        except Exception as exc:
            LOG.exception("VoiceTut synthesis failed after streaming headers were sent.")
            try:
                emit({"type": "error", "message": str(exc)[:500]})
            except (BrokenPipeError, ConnectionResetError):
                pass

    def _synthesize(self, payload: dict[str, Any]) -> None:
        # Same Voice Design-only path; no reference audio or speaker registry can be selected.
        self._stream(payload)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()
    if args.host != "127.0.0.1":
        raise SystemExit("Layal VoiceTut bridge must bind to 127.0.0.1 only.")
    if not 1024 <= args.port <= 65535:
        raise SystemExit("Port must be between 1024 and 65535.")

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    if not os.environ.get("LAYAL_VOICETUT_TOKEN"):
        raise SystemExit("LAYAL_VOICETUT_TOKEN is required.")

    load_engine()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.daemon_threads = True
    LOG.info("READY http://%s:%d", args.host, args.port)
    try:
        server.serve_forever(poll_interval=0.25)
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
