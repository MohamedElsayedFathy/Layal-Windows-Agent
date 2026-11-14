import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
import wave
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
RENDERER_PATH = ROOT / "tools" / "evaluation" / "render_piper_baseline.py"
SCORER_PATH = ROOT / "tools" / "evaluation" / "score_tts_candidates.py"
PROMPT_PATH = ROOT / "tests" / "TestAssets" / "Speech" / "voice-persona-evaluation.json"


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


RENDERER = load_module("render_piper_baseline", RENDERER_PATH)
SCORER = load_module("score_tts_candidates_for_piper_test", SCORER_PATH)


class PiperBaselineRendererTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        prompt_payload = json.loads(PROMPT_PATH.read_text(encoding="utf-8"))
        errors, cls.prompt_cases = SCORER.validate_prompt_set(prompt_payload)
        if errors:
            raise AssertionError(errors)

    @staticmethod
    def write_wav(path, values=None, rate=22050):
        values = values or [1000, -1000, 500, -500] * 220
        path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(path), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(rate)
            import struct
            audio.writeframes(b"".join(struct.pack("<h", value) for value in values))
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_fixed_style_settings_match_application_policy(self):
        self.assertEqual(
            {
                "natural": (1.00, 0.20),
                "gentle": (1.10, 0.36),
                "bright": (0.95, 0.16),
                "clear": (1.03, 0.28),
                "empathetic": (1.08, 0.40),
                "serious": (1.05, 0.34),
                "energetic": (0.92, 0.12),
                "playful": (0.97, 0.14),
            },
            RENDERER.STYLE_SETTINGS,
        )

    def test_text_normalization_matches_the_local_service_rules(self):
        tick = chr(96)
        value = "## Title\nSay " + tick + "hello" + tick + ", see [guide](https://example.com), and visit https://example.net"
        self.assertEqual("Title Say hello, see guide, and visit", RENDERER.normalize_text_for_speech(value))

    def test_mixed_text_segmentation_routes_to_the_same_language_voice_order(self):
        self.assertEqual(
            [("افتحي", True), ("Settings", False)],
            RENDERER.split_text_by_language("افتحي Settings"),
        )
        self.assertEqual(
            [
                ("أنا هراجع", True),
                ("the final report", False),
                ("وأبعتلك النتيجة.", True),
            ],
            RENDERER.split_text_by_language("أنا هراجع the final report وأبعتلك النتيجة."),
        )

    def test_asset_verification_checks_size_and_sha256(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            payload = b"known local piper asset"
            (root / "asset.bin").write_bytes(payload)
            spec = RENDERER.AssetSpec(
                "asset.bin",
                len(payload),
                hashlib.sha256(payload).hexdigest(),
                "test asset",
            )
            self.assertEqual(spec.sha256, RENDERER.verify_asset(root, spec))

            (root / "asset.bin").write_bytes(b"tampered")
            with self.assertRaises(ValueError):
                RENDERER.verify_asset(root, spec)

    def test_joining_pcm_segments_preserves_frames_and_rejects_incompatible_rates(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            first = root / "first.wav"
            second = root / "second.wav"
            output = root / "joined.wav"
            self.write_wav(first, values=[100, 200, 300, 400])
            self.write_wav(second, values=[-100, -200, -300, -400])
            parts = [
                RENDERER.validate_pcm_wav(first),
                RENDERER.validate_pcm_wav(second),
            ]
            RENDERER.write_joined_wav(output, parts)
            with wave.open(str(output), "rb") as audio:
                self.assertEqual(8, audio.getnframes())
                self.assertEqual(22050, audio.getframerate())

            self.write_wav(second, values=[1, 2, 3, 4], rate=16000)
            incompatible = [
                RENDERER.validate_pcm_wav(first),
                RENDERER.validate_pcm_wav(second),
            ]
            with self.assertRaises(ValueError):
                RENDERER.write_joined_wav(root / "bad.wav", incompatible)

    def test_piper_is_invoked_without_a_shell_with_pinned_runtime_arguments(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / "segment.wav"
            captured = {}

            def fake_run(arguments, **kwargs):
                captured["arguments"] = arguments
                captured["kwargs"] = kwargs
                self.write_wav(output)
                return SimpleNamespace(returncode=0, stdout="", stderr="")

            with mock.patch.object(RENDERER.subprocess, "run", side_effect=fake_run):
                elapsed, _part = RENDERER.run_piper_segment(
                    piper_executable=root / "piper.exe",
                    model_path=root / "voice.onnx",
                    config_path=root / "voice.onnx.json",
                    text="Hello Layal",
                    output_path=output,
                    length_scale=1.10,
                    sentence_silence=0.36,
                    timeout_seconds=30,
                )

            self.assertGreaterEqual(elapsed, 0)
            self.assertIn("--length_scale", captured["arguments"])
            self.assertEqual("1.10", captured["arguments"][captured["arguments"].index("--length_scale") + 1])
            self.assertEqual("0.36", captured["arguments"][captured["arguments"].index("--sentence_silence") + 1])
            self.assertFalse(captured["kwargs"]["shell"])
            self.assertEqual("Hello Layal\n", captured["kwargs"]["input"])
            self.assertEqual(str(root), captured["kwargs"]["cwd"])

    def test_mixed_prompt_renders_two_segments_and_writes_a_scoring_manifest_sample(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            run_dir = root / "run"
            samples_dir = run_dir / "samples"
            temp_dir = run_dir / "tmp"
            samples_dir.mkdir(parents=True)
            temp_dir.mkdir()
            models_dir = root / "models"
            models_dir.mkdir()
            commands = []

            def fake_run(arguments, **kwargs):
                output_path = Path(arguments[arguments.index("--output_file") + 1])
                commands.append((arguments, kwargs))
                self.write_wav(output_path, values=[len(commands) * 500] * 220)
                return SimpleNamespace(returncode=0, stdout="", stderr="")

            output_path = samples_dir / "piper-baseline__mix-01__natural.wav"
            with mock.patch.object(RENDERER.subprocess, "run", side_effect=fake_run):
                record = RENDERER.render_prompt(
                    prompt={
                        "id": "mix-01",
                        "language": "MixedArabicEnglish",
                        "text": "افتحي Settings",
                    },
                    style="natural",
                    piper_executable=root / "piper" / "piper.exe",
                    models_dir=models_dir,
                    temporary_dir=temp_dir,
                    output_path=output_path,
                )

            self.assertEqual(2, len(commands))
            self.assertEqual(["ar_JO-kareem-medium", "en_US-lessac-medium"], record["voiceRoutes"])
            self.assertEqual("samples/piper-baseline__mix-01__natural.wav", record["audioPath"])
            self.assertEqual(64, len(record["audioSha256"]))
            self.assertGreater(record["renderTimeMs"], 0)
            self.assertLessEqual(record["timeToFirstAudioMs"], record["renderTimeMs"])
            self.assertEqual([], list(temp_dir.iterdir()))

            asset_info = {
                "voiceSha256": {
                    "ar_JO-kareem-medium.onnx": "a" * 64,
                    "ar_JO-kareem-medium.onnx.json": "b" * 64,
                    "en_US-lessac-medium.onnx": "c" * 64,
                    "en_US-lessac-medium.onnx.json": "d" * 64,
                },
                "runtimeSha256": {"piper/piper.exe": "e" * 64},
            }
            manifest = RENDERER.create_run_manifest(
                run_id="piper-baseline-test",
                device={"os": "Windows", "cpu": "Test CPU", "ramMb": 8192, "gpu": None},
                engine=RENDERER.build_engine_record(asset_info),
                prompts_path=PROMPT_PATH,
            )
            manifest["samples"].append(record)
            errors, _engines = SCORER.validate_manifest(manifest, self.prompt_cases)
            self.assertEqual([], errors)

    def test_engine_manifest_keeps_rights_and_human_acceptance_pending(self):
        engine = RENDERER.build_engine_record({
            "voiceSha256": {
                "ar_JO-kareem-medium.onnx": "a" * 64,
                "ar_JO-kareem-medium.onnx.json": "b" * 64,
                "en_US-lessac-medium.onnx": "c" * 64,
                "en_US-lessac-medium.onnx.json": "d" * 64,
            },
            "runtimeSha256": {"piper/piper.exe": "e" * 64},
        })
        self.assertEqual("pending", engine["licenseStatus"])
        self.assertEqual("pending", engine["speakerRightsStatus"])
        self.assertEqual("pending", engine["manualAcceptanceStatus"])
        self.assertFalse(engine["voiceCloningEnabled"])
        self.assertIn("two different Piper voice models", engine["voiceIdentityNote"])
        self.assertIn("do not expose semantic emotion", engine["styleControlNote"])


if __name__ == "__main__":
    unittest.main()
