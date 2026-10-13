import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from verify_release import CONFIGURATION, REQUIRED, validate_archive


def executable(machine):
    data = bytearray(128)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 60, 64)
    data[64:68] = b"PE\0\0"
    struct.pack_into("<H", data, 68, machine)
    return data


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ffa-release-tests-")
        self.path = Path(self.temporary.name) / "release.zip"
        self.files = dict.fromkeys(REQUIRED, b"fixture")
        self.files.update({
            "runtime.json": json.dumps(CONFIGURATION).encode(),
            "release-manifest.json": json.dumps({
                "runtime_id": "win-arm64", "version": "1.0.0",
                "model": {"bundled": False}, "packages": [{"name": "diskannpy"}],
            }).encode(),
            "FuzzyFindAnything.exe": executable(0xAA64),
            ".runtime/python.exe": executable(0x8664),
            "extractor/FuzzyFindAnything.Extractor.exe": executable(0x8664),
            ".runtime/python311._pth": b"python311.zip\n.\nLib\\site-packages\nimport site\n",
        })

    def tearDown(self):
        self.temporary.cleanup()

    def verify(self):
        with zipfile.ZipFile(self.path, "w") as archive:
            for name, content in self.files.items():
                archive.writestr(name, content)
        validate_archive(self.path, "win-arm64", "1.0.0")

    def test_complete_relocatable_archive_is_accepted(self):
        self.files[".runtime/Lib/site-packages/docx/templates/word/settings.xml"] = b"public package template"
        self.verify()

    def test_build_machine_paths_are_rejected(self):
        configuration = {**CONFIGURATION, "python": r"C:\build\.runtime\python.exe"}
        self.files["runtime.json"] = json.dumps(configuration).encode()
        with self.assertRaisesRegex(ValueError, "relative"):
            self.verify()

    def test_model_user_data_and_path_traversal_are_rejected(self):
        for name in ("catalog.sqlite3", "settings.xml", "models/model.safetensors", "../outside.txt"):
            with self.subTest(name=name):
                self.files[name] = b"must not ship"
                try:
                    with self.assertRaises(ValueError): self.verify()
                finally:
                    del self.files[name]

    def test_missing_runtime_and_wrong_native_architecture_are_rejected(self):
        del self.files[".runtime/python.exe"]
        with self.assertRaisesRegex(ValueError, "missing"):
            self.verify()
        self.files[".runtime/python.exe"] = executable(0xAA64)
        with self.assertRaisesRegex(ValueError, "architecture"):
            self.verify()


if __name__ == "__main__":
    unittest.main(verbosity=2)
