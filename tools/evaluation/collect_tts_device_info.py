#!/usr/bin/env python3
"""Collect a local hardware preflight report for Layal TTS evaluation.

This utility is intentionally read-only: it does not access the network, install
packages, download or open model weights, start inference, or record audio.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import platform
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

MIB = 1024 * 1024


def _windows_cpu_name() -> str | None:
    """Read the CPU model name from the standard Windows registry location."""
    if sys.platform != "win32":
        return None
    try:
        import winreg  # type: ignore[import-not-found]

        key_path = r"HARDWARE\DESCRIPTION\System\CentralProcessor\0"
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key_path) as key:
            value, _ = winreg.QueryValueEx(key, "ProcessorNameString")
        return value.strip() if isinstance(value, str) and value.strip() else None
    except (OSError, ImportError):
        return None


def collect_cpu() -> tuple[str, str]:
    """Return a useful CPU label and the source used to identify it."""
    registry_name = _windows_cpu_name()
    if registry_name:
        return registry_name, "windows-registry"

    processor = platform.processor().strip()
    if processor:
        return processor, "platform.processor"

    machine = platform.machine().strip()
    if machine:
        return f"Processor model unavailable (architecture: {machine})", "platform.machine"
    return "Processor model unavailable", "unavailable"


def collect_total_memory_mb() -> int:
    """Return installed physical RAM in MiB using the platform standard library."""
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
        global_memory_status = kernel32.GlobalMemoryStatusEx
        global_memory_status.argtypes = [ctypes.POINTER(MemoryStatusEx)]
        global_memory_status.restype = ctypes.c_int
        if not global_memory_status(ctypes.byref(status)):
            raise OSError(ctypes.get_last_error(), "GlobalMemoryStatusEx failed")
        total_bytes = int(status.ullTotalPhys)
    else:
        page_size = os.sysconf("SC_PAGE_SIZE")
        page_count = os.sysconf("SC_PHYS_PAGES")
        total_bytes = int(page_size) * int(page_count)

    if total_bytes <= 0:
        raise RuntimeError("The operating system did not report a valid physical-memory size.")
    return int(round(total_bytes / MIB))


def collect_nvidia_gpus() -> tuple[list[dict[str, Any]], str]:
    """Read NVIDIA model/VRAM metadata without loading any model or driver library."""
    command = [
        "nvidia-smi",
        "--query-gpu=name,memory.total",
        "--format=csv,noheader,nounits",
    ]
    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=5,
        )
    except FileNotFoundError:
        return [], "nvidia-smi-not-found"
    except subprocess.TimeoutExpired:
        return [], "nvidia-smi-timeout"
    except OSError as error:
        return [], f"nvidia-smi-error: {error.__class__.__name__}"

    if result.returncode != 0:
        return [], f"nvidia-smi-exit-{result.returncode}"

    devices: list[dict[str, Any]] = []
    for line in result.stdout.splitlines():
        if not line.strip():
            continue
        parts = [part.strip() for part in line.split(",", maxsplit=1)]
        if len(parts) != 2 or not parts[0]:
            continue
        try:
            memory_mb = int(float(parts[1]))
        except ValueError:
            memory_mb = None
        devices.append({"name": parts[0], "memoryMb": memory_mb})

    return devices, "nvidia-smi" if devices else "nvidia-smi-no-usable-device-data"


def collect_disk_info(directory: Path) -> dict[str, Any]:
    """Report free space on the volume containing an existing directory."""
    resolved = directory.expanduser().resolve(strict=True)
    if not resolved.is_dir():
        raise NotADirectoryError(f"Model/destination path is not a directory: {resolved}")
    usage = shutil.disk_usage(resolved)
    return {
        "directory": str(resolved),
        "freeMb": int(usage.free // MIB),
        "totalMb": int(usage.total // MIB),
    }


def collect_device_report(model_directory: Path) -> dict[str, Any]:
    """Collect a manifest-ready device record and non-invasive diagnostics."""
    cpu, cpu_source = collect_cpu()
    ram_mb = collect_total_memory_mb()
    gpus, gpu_detection = collect_nvidia_gpus()
    gpu_names = [device["name"] for device in gpus]
    gpu_memory_values = [device["memoryMb"] for device in gpus if device["memoryMb"] is not None]

    os_label = f"{platform.system()} {platform.release()} ({platform.version()})"
    return {
        "schemaVersion": 1,
        "collectedAtUtc": datetime.now(timezone.utc).isoformat(),
        "device": {
            "os": os_label,
            "cpu": cpu,
            "ramMb": ram_mb,
            "gpu": "; ".join(gpu_names) if gpu_names else None,
        },
        "hardwareDetails": {
            "cpuDetection": cpu_source,
            "gpuDetection": gpu_detection,
            "gpus": gpus,
            # VRAM belongs to separate devices; do not imply it is one shared pool.
            "largestNvidiaGpuVramMb": max(gpu_memory_values) if gpu_memory_values else None,
            "pythonVersion": platform.python_version(),
            "pythonImplementation": platform.python_implementation(),
            "architecture": platform.machine(),
        },
        "disk": collect_disk_info(model_directory),
        "limitations": [
            "GPU details are reported only when nvidia-smi is available; null does not prove that no GPU is installed.",
            "Disk space is measured for the supplied directory's volume; no model-size or performance guarantee is inferred.",
            "This report does not download, inspect, deserialize, or execute model files.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--model-dir",
        type=Path,
        default=Path.cwd(),
        help="Existing directory on the volume intended for local TTS artifacts (default: current directory)",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Optional JSON output path; when omitted, JSON is printed to stdout",
    )
    args = parser.parse_args(argv)

    try:
        report = collect_device_report(args.model_dir)
        serialized = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
        if args.output:
            output_path = args.output.expanduser()
            output_path.parent.mkdir(parents=True, exist_ok=True)
            output_path.write_text(serialized, encoding="utf-8")
            print(f"Device preflight report written to: {output_path.resolve()}")
        else:
            print(serialized, end="")
    except (OSError, RuntimeError, ValueError) as error:
        print(f"Could not collect device information: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
