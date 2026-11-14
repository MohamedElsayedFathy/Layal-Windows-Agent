import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "evaluation" / "collect_tts_device_info.py"
SPEC = importlib.util.spec_from_file_location("collect_tts_device_info", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class TtsDevicePreflightTests(unittest.TestCase):
    def test_physical_memory_probe_returns_a_positive_integer_on_the_host(self):
        memory_mb = MODULE.collect_total_memory_mb()
        self.assertIsInstance(memory_mb, int)
        self.assertGreater(memory_mb, 0)

    def test_cpu_falls_back_to_architecture_without_claiming_model_name(self):
        with mock.patch.object(MODULE, "_windows_cpu_name", return_value=None), \
             mock.patch.object(MODULE.platform, "processor", return_value=""), \
             mock.patch.object(MODULE.platform, "machine", return_value="AMD64"):
            cpu, source = MODULE.collect_cpu()
        self.assertIn("AMD64", cpu)
        self.assertEqual("platform.machine", source)

    def test_nvidia_smi_parses_multiple_devices(self):
        completed = subprocess.CompletedProcess(
            args=["nvidia-smi"],
            returncode=0,
            stdout="NVIDIA RTX A2000, 6144\nNVIDIA RTX 3060, 12288\n",
            stderr="",
        )
        with mock.patch.object(MODULE.subprocess, "run", return_value=completed) as run:
            devices, source = MODULE.collect_nvidia_gpus()
        self.assertEqual("nvidia-smi", source)
        self.assertEqual(
            [
                {"name": "NVIDIA RTX A2000", "memoryMb": 6144},
                {"name": "NVIDIA RTX 3060", "memoryMb": 12288},
            ],
            devices,
        )
        self.assertEqual(5, run.call_args.kwargs["timeout"])
        self.assertTrue(run.call_args.kwargs["capture_output"])

    def test_nvidia_smi_unknown_vram_is_not_reported_as_zero(self):
        completed = subprocess.CompletedProcess(
            args=["nvidia-smi"],
            returncode=0,
            stdout="NVIDIA GPU, [N/A]\n",
            stderr="",
        )
        with mock.patch.object(MODULE.subprocess, "run", return_value=completed):
            devices, source = MODULE.collect_nvidia_gpus()
        self.assertEqual("nvidia-smi", source)
        self.assertEqual([{"name": "NVIDIA GPU", "memoryMb": None}], devices)

    def test_missing_nvidia_smi_is_reported_as_unknown_not_no_gpu(self):
        with mock.patch.object(MODULE.subprocess, "run", side_effect=FileNotFoundError):
            devices, source = MODULE.collect_nvidia_gpus()
        self.assertEqual([], devices)
        self.assertEqual("nvidia-smi-not-found", source)

    def test_disk_info_requires_an_existing_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            info = MODULE.collect_disk_info(Path(temp))
            self.assertGreater(info["totalMb"], 0)
            self.assertGreaterEqual(info["freeMb"], 0)
            self.assertEqual(str(Path(temp).resolve()), info["directory"])
            with self.assertRaises(FileNotFoundError):
                MODULE.collect_disk_info(Path(temp) / "missing")
            file_path = Path(temp) / "not-a-directory.txt"
            file_path.write_text("test", encoding="utf-8")
            with self.assertRaises(NotADirectoryError):
                MODULE.collect_disk_info(file_path)

    def test_device_report_has_the_existing_manifest_device_shape(self):
        with tempfile.TemporaryDirectory() as temp, \
             mock.patch.object(MODULE, "collect_cpu", return_value=("Test CPU", "test")), \
             mock.patch.object(MODULE, "collect_total_memory_mb", return_value=16384), \
             mock.patch.object(MODULE, "collect_nvidia_gpus", return_value=([{"name": "Test GPU 1", "memoryMb": 8192}, {"name": "Test GPU 2", "memoryMb": 4096}], "nvidia-smi")):
            report = MODULE.collect_device_report(Path(temp))
        self.assertEqual(
            {"os", "cpu", "ramMb", "gpu"},
            set(report["device"]),
        )
        self.assertEqual("Test CPU", report["device"]["cpu"])
        self.assertEqual(16384, report["device"]["ramMb"])
        self.assertEqual("Test GPU 1; Test GPU 2", report["device"]["gpu"])
        self.assertEqual(8192, report["hardwareDetails"]["largestNvidiaGpuVramMb"])
        self.assertTrue(report["limitations"])

    def test_cli_writes_machine_readable_json_without_running_inference(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            out = root / "reports" / "device.json"
            payload = {
                "schemaVersion": 1,
                "device": {"os": "Windows", "cpu": "CPU", "ramMb": 8192, "gpu": None},
            }
            with mock.patch.object(MODULE, "collect_device_report", return_value=payload):
                result = MODULE.main(["--model-dir", str(root), "--output", str(out)])
            self.assertEqual(0, result)
            self.assertEqual(payload, json.loads(out.read_text(encoding="utf-8")))

    def test_invalid_model_directory_returns_nonzero(self):
        with tempfile.TemporaryDirectory() as temp:
            result = MODULE.main(["--model-dir", str(Path(temp) / "missing")])
        self.assertEqual(2, result)


if __name__ == "__main__":
    unittest.main()
