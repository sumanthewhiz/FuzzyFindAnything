"""Validate distributable contents and optionally exercise a relocated runtime."""

import argparse
import json
from pathlib import Path, PurePosixPath
import struct
import subprocess
import shutil
import tempfile
import time
import zipfile

MACHINES = {"win-x64": 0x8664, "win-arm64": 0xAA64}
CONFIGURATION = {
    "python": ".runtime\\python.exe",
    "model": "models\\embeddinggemma-2",
    "extractor": "extractor\\FuzzyFindAnything.Extractor.exe",
}
REQUIRED = {
    "FuzzyFindAnything.exe", "FuzzyFindAnything.dll", "FuzzyFindAnything.runtimeconfig.json",
    "runtime.json", "Get-Model.ps1", "QUICKSTART.md", "THIRD-PARTY-NOTICES.txt", "release-manifest.json",
    ".runtime/python.exe", ".runtime/python311._pth", ".runtime/python311.zip", ".runtime/LICENSE.txt",
    "engine/worker.py", "engine/model_worker.py", "engine/download_model.py", "engine/work_queue.py",
    "engine/indexer_logging.py", "extractor/FuzzyFindAnything.Extractor.exe",
    "engine/startup.py",
    "engine/inference_batches.py",
}


def pe_machine(data):
    if data[:2] != b"MZ":
        raise ValueError("A packaged executable is not a Windows PE file.")
    offset = struct.unpack_from("<I", data, 60)[0]
    if data[offset:offset + 4] != b"PE\0\0":
        raise ValueError("Invalid Windows PE header.")
    return struct.unpack_from("<H", data, offset + 4)[0]


def validate_archive(path, runtime_id, version):
    with zipfile.ZipFile(path) as archive:
        names = set(archive.namelist())
        if not REQUIRED.issubset(names):
            raise ValueError("Required release files are missing: " + ", ".join(sorted(REQUIRED - names)))
        forbidden = {"settings.xml", "engine.lock", "catalog.sqlite3", "catalog.sqlite3-wal", "catalog.sqlite3-shm",
                     "engine.log", "native-engine.log", "app.log", "indexer.jsonl", ".env"}
        for name in names:
            relative = PurePosixPath(name)
            if relative.is_absolute() or ".." in relative.parts or "\\" in name or ":" in name:
                raise ValueError(f"Unsafe archive path: {name}")
            if (relative.parts[0] in (".git", ".github", "models", "data", "index", "corpus")
                    or (relative.name in forbidden and relative.parts[0] != ".runtime") or "__pycache__" in relative.parts
                    or name.endswith((".pyc", ".safetensors"))):
                raise ValueError(f"Development, model, or user data is present in the release: {name}")
        configuration = json.loads(archive.read("runtime.json").decode("utf-8-sig"))
        if configuration != CONFIGURATION:
            raise ValueError("Release configuration must use the supported relative runtime/model/helper paths.")
        manifest = json.loads(archive.read("release-manifest.json"))
        if manifest["runtime_id"] != runtime_id or manifest["version"] != version or manifest["model"]["bundled"]:
            raise ValueError("Release identity does not match the requested package.")
        if not manifest["packages"] or not any(item["name"].lower() == "diskannpy" for item in manifest["packages"]):
            raise ValueError("The release dependency inventory is incomplete.")
        for name, expected in (
            ("FuzzyFindAnything.exe", MACHINES[runtime_id]),
            (".runtime/python.exe", MACHINES["win-x64"]),
            ("extractor/FuzzyFindAnything.Extractor.exe", MACHINES["win-x64"]),
        ):
            if pe_machine(archive.read(name)) != expected:
                raise ValueError(f"Incorrect executable architecture: {name}")
        imports = archive.read(".runtime/python311._pth").decode("ascii").splitlines()
        if imports != ["python311.zip", ".", "Lib\\site-packages", "import site"]:
            raise ValueError("The embedded Python import paths are not relocatable.")
        if archive.testzip() is not None:
            raise ValueError("The release archive has a damaged entry.")
        print(f"Verified {runtime_id} {version}: {len(names):,} archive entries, relative paths, no model/user data.")


def runtime_smoke(path):
    temporary = tempfile.mkdtemp(prefix="ffa relocated runtime ")
    try:
        destination = Path(temporary) / "Application"
        with zipfile.ZipFile(path) as archive:
            archive.extractall(destination)
        python = destination / ".runtime" / "python.exe"
        engine = destination / "engine"
        checks = [
            "import sys;sys.path.insert(0,sys.argv[1]);from cpu_budget import get_budget;get_budget(apply=True);"
            "import torch,transformers,sentence_transformers,PIL,huggingface_hub;"
            "print('Relocated inference dependencies:',torch.__version__,transformers.__version__)",
            "import sys;sys.path.insert(0,sys.argv[1]);from cpu_budget import get_budget;get_budget(apply=True);"
            "import tempfile;from pathlib import Path;from store import VectorStore;"
            "root=tempfile.TemporaryDirectory();store=VectorStore(Path(root.name));"
            "assert store.counts()==(0,0);store.close();root.cleanup();print('Relocated DiskANN/SQLite: OK')",
        ]
        for script in checks:
            subprocess.run([str(python), "-c", script, str(engine)], cwd=temporary, check=True, timeout=240)
        probe = subprocess.run(
            [str(python), str(engine / "download_model.py"), "--destination", str(destination / "models" / "missing"),
             "--verify-only"], cwd=temporary, capture_output=True, text=True, encoding="utf-8", timeout=30,
        )
        if probe.returncode == 0 or "Missing or empty model assets" not in probe.stderr:
            raise RuntimeError("The relocated model acquisition entry point did not report missing assets correctly.")
        print("Relocated model-acquisition entry point: OK")
    finally:
        deadline = time.monotonic() + 30
        while True:
            try:
                shutil.rmtree(temporary)
                break
            except OSError as error:
                if error.winerror not in (5, 32, 33) or time.monotonic() >= deadline:
                    raise
                time.sleep(0.5)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--runtime-id", required=True, choices=tuple(MACHINES))
    parser.add_argument("--version", required=True)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    validate_archive(args.archive, args.runtime_id, args.version)
    if args.smoke:
        runtime_smoke(args.archive)
