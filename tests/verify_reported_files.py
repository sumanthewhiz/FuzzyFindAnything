"""Explicit real-file regression; never changes the original corpus or the user's index."""

import argparse
import json
from pathlib import Path
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))

from extraction import TextExtractor
from model_client import EmbeddingClient
from paths import fingerprint
from store import VectorStore


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--xls", required=True, type=Path)
    parser.add_argument("--extractor", required=True, type=Path)
    args = parser.parse_args()
    started = time.monotonic()
    original = fingerprint(args.xls)
    text = TextExtractor(args.extractor).extract(args.xls)
    print(f"EXTRACTION seconds={time.monotonic() - started:.3f} characters={len(text)}", flush=True)
    model = EmbeddingClient(ROOT / "models" / "embeddinggemma-2")
    try:
        print(f"MODEL READY elapsed={time.monotonic() - started:.1f}s", flush=True)
        calls = []
        last_report = 0
        query_checked = False

        def progress(done, total):
            nonlocal last_report, query_checked
            calls.append((done, total))
            if done == 0 or done == total or time.monotonic() - last_report >= 20:
                print(f"PROGRESS {done}/{total} elapsed={time.monotonic() - started:.1f}s", flush=True)
                last_report = time.monotonic()
            if done >= 4 and not query_checked:
                t = time.monotonic()
                query = model.query("three dimensional geometry model coordinates")
                assert query.shape == (768,)
                print(f"SEARCH WHILE INDEXING seconds={time.monotonic() - t:.2f}", flush=True)
                query_checked = True

        chunks = model.document(args.xls.name, text, lambda: False, progress)
        assert calls[-1] == (len(chunks), len(chunks)) and len(chunks) > 1
        assert fingerprint(args.xls) == original
        with tempfile.TemporaryDirectory(prefix="ffa-xls-regression-") as temporary:
            store = VectorStore(Path(temporary))
            try:
                folder = store.add_folder(args.xls.parent)
                store.replace_file(folder, args.xls, original, "Document", chunks)
                result = store.search(chunks[0][1], 5, 0.9)
                assert result and Path(result[0]["path"]) == args.xls
                store.close()
                store = VectorStore(Path(temporary))
                assert store.search(chunks[0][1], 5, 0.9)
                print(f"PASS ALL {len(chunks)} PASSAGES indexed, searchable, and durable; elapsed={time.monotonic() - started:.1f}s", flush=True)
            finally:
                store.close()
    finally:
        model.close()


if __name__ == "__main__":
    main()
