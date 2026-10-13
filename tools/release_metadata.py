"""Generate a release inventory without copying machine-specific package metadata."""

import argparse
from datetime import datetime, timezone
from importlib.metadata import distributions
import json
import os
from pathlib import Path
import platform
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from download_model import MODEL_ID, MODEL_REVISION


def write_metadata(package, runtime_id, version):
    packages = []
    for distribution in distributions():
        notices = [
            str(Path(".runtime") / "Lib" / "site-packages" / str(file))
            for file in distribution.files or []
            if any(word in Path(str(file)).name.lower() for word in ("license", "notice", "copying"))
            and (package / ".runtime" / "Lib" / "site-packages" / str(file)).is_file()
        ]
        packages.append({
            "name": distribution.metadata["Name"], "version": distribution.version,
            "license_expression": distribution.metadata.get("License-Expression"),
            "license_classifiers": [value for value in distribution.metadata.get_all("Classifier", [])
                                    if value.startswith("License ::")],
            "notice_files": sorted(notices),
        })
    manifest = {
        "schema_version": 1, "application": "Fuzzy Find Anything", "version": version,
        "runtime_id": runtime_id, "built_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "source_commit": os.environ.get("GITHUB_SHA"),
        "source_repository": "https://github.com/sumanthewhiz/FuzzyFindAnything",
        "python": {"version": platform.python_version(), "architecture": "x64"},
        "model": {"id": MODEL_ID, "revision": MODEL_REVISION, "bundled": False},
        "packages": sorted(packages, key=lambda value: value["name"].lower()),
    }
    (package / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (package / "THIRD-PARTY-NOTICES.txt").write_text(
        "Fuzzy Find Anything - bundled component notices\n\n"
        "Application copyright (c) 2026 Suman Ghosh.\n"
        "An open-source license for the application's own source has not been designated.\n"
        "Do not infer application licensing from the licenses of its dependencies.\n\n"
        "The self-contained .NET deployment retains Microsoft's LICENSE.txt and\n"
        "ThirdPartyNotices.txt where supplied. CPython's license is .runtime\\LICENSE.txt.\n"
        "Python distributions retain their original license/notice files in .runtime.\n"
        "release-manifest.json records exact installed package versions and notice paths;\n"
        "missing license metadata is not a claim that a component has no license terms.\n\n"
        "Microsoft DiskANN/diskannpy is MIT-licensed. NumPy, SciPy, scikit-learn,\n"
        "PyTorch, SentenceTransformers, Transformers, Pillow and their dependencies\n"
        "retain the notices supplied by their distributions. Consult those files for\n"
        "complete terms, including any notices for bundled native libraries.\n\n"
        "EmbeddingGemma 2 is acquired separately, not included in this archive.\n"
        "Its model card and licensing information accompany the downloaded assets.\n"
        "Windows, Visual C++ Redistributable, and Office/PDF IFilters are separately\n"
        "installed Microsoft/vendor components, not redistributed by this package.\n",
        encoding="utf-8",
    )
    print(f"Recorded {len(packages)} installed distributions for {runtime_id}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--runtime-id", required=True, choices=("win-x64", "win-arm64"))
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    write_metadata(args.package, args.runtime_id, args.version)
