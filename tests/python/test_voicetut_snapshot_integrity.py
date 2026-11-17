import hashlib
import importlib.util
import os
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "src" / "Layal.Desktop" / "VoiceTut" / "voicetut_local_server.py"
SPEC = importlib.util.spec_from_file_location("voicetut_local_server", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)


class VoiceTutSnapshotIntegrityTests(unittest.TestCase):
    def test_known_model_binary_pins_are_present_and_well_formed(self):
        self.assertEqual(
            {
                "model.safetensors",
                "tokenizer.json",
            },
            set(MODULE.MODEL_FILE_MANIFEST),
        )
        for name, pin in MODULE.MODEL_FILE_MANIFEST.items():
            with self.subTest(file=name):
                self.assertGreater(pin["size_bytes"], 0)
                self.assertRegex(pin["sha256"], r"^[0-9a-f]{64}$")

    def test_python_manifest_pins_match_the_dotnet_resource_catalog(self):
        catalog_path = ROOT / "src" / "Layal.Desktop" / "Services" / "LocalResourceCatalog.cs"
        catalog = catalog_path.read_text(encoding="utf-8")
        for name, pin in MODULE.MODEL_FILE_MANIFEST.items():
            with self.subTest(file=name):
                self.assertIn(pin["sha256"], catalog)
                self.assertIn(f"{pin['size_bytes']:,}".replace(",", "_"), catalog)

    def test_snapshot_verifier_accepts_pinned_files_and_hub_style_cache_symlinks(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            snapshot = root / "hub" / "models--voice" / "snapshots" / "revision"
            blobs = root / "hub" / "models--voice" / "blobs"
            snapshot.mkdir(parents=True)
            blobs.mkdir(parents=True)
            expected = {}
            for name, payload in {
                "model.safetensors": b"model-weights",
                "tokenizer.json": b'{"tokenizer":true}',
            }.items():
                blob = blobs / name
                blob.write_bytes(payload)
                (snapshot / name).symlink_to(blob)
                expected[name] = {
                    "size_bytes": len(payload),
                    "sha256": hashlib.sha256(payload).hexdigest(),
                }

            MODULE.verify_model_snapshot_files(snapshot, root, expected)

    def test_snapshot_verifier_rejects_tampered_binary(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            snapshot = root / "snapshot"
            snapshot.mkdir()
            payload = b"original-model"
            (snapshot / "model.safetensors").write_bytes(payload)
            expected = {
                "model.safetensors": {
                    "size_bytes": len(payload),
                    "sha256": hashlib.sha256(payload).hexdigest(),
                }
            }
            (snapshot / "model.safetensors").write_bytes(b"tampered-model")
            with self.assertRaisesRegex(ValueError, "size mismatch|SHA-256 mismatch"):
                MODULE.verify_model_snapshot_files(snapshot, root, expected)

    def test_snapshot_verifier_rejects_same_size_hash_tampering(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            snapshot = root / "snapshot"
            snapshot.mkdir()
            original = b"original!"
            (snapshot / "model.safetensors").write_bytes(original)
            expected = {
                "model.safetensors": {
                    "size_bytes": len(original),
                    "sha256": hashlib.sha256(original).hexdigest(),
                }
            }
            (snapshot / "model.safetensors").write_bytes(b"tampered!")
            with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
                MODULE.verify_model_snapshot_files(snapshot, root, expected)

    def test_snapshot_verifier_rejects_symlink_escaping_approved_cache(self):
        with tempfile.TemporaryDirectory() as allowed_temp, tempfile.TemporaryDirectory() as outside_temp:
            allowed = Path(allowed_temp)
            outside = Path(outside_temp) / "model.safetensors"
            outside.write_bytes(b"model")
            snapshot = allowed / "snapshot"
            snapshot.mkdir()
            link = snapshot / "model.safetensors"
            try:
                link.symlink_to(outside)
            except (OSError, NotImplementedError):
                self.skipTest("The Windows test host does not permit creating symbolic links.")
            expected = {
                "model.safetensors": {
                    "size_bytes": 5,
                    "sha256": hashlib.sha256(b"model").hexdigest(),
                }
            }
            with self.assertRaisesRegex(ValueError, "outside the approved"):
                MODULE.verify_model_snapshot_files(snapshot, allowed, expected)

    def test_snapshot_verifier_rejects_unsafe_manifest_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            snapshot = root / "snapshot"
            snapshot.mkdir()
            with self.assertRaisesRegex(ValueError, "unsafe relative file"):
                MODULE.verify_model_snapshot_files(
                    snapshot,
                    root,
                    {"../outside": {"size_bytes": 1, "sha256": "0" * 64}},
                )


if __name__ == "__main__":
    unittest.main()
