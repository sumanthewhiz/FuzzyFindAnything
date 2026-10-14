"""Opt-in end-to-end checks using the real local model, real DiskANN, and Win32 APIs."""

import json
from contextlib import closing
import os
from pathlib import Path
import queue
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from fixtures import create_corpus

PYTHON = ROOT / ".runtime" / "python.exe"
EXTRACTOR = ROOT / "artifacts" / "FuzzyFindAnything" / "extractor" / "FuzzyFindAnything.Extractor.exe"


class Worker:
    def __init__(self, data: Path):
        self.data = data
        data.mkdir(parents=True, exist_ok=True)
        self.log = (data / "test-stderr.log").open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            [str(PYTHON), "-u", str(ROOT / "engine" / "worker.py"), "--data-dir", str(data),
             "--model", str(ROOT / "models" / "embeddinggemma-2"), "--extractor", str(EXTRACTOR),
             "--app-dir", str(ROOT / "artifacts" / "FuzzyFindAnything")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log,
            text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW,
            env={**os.environ, "PYTHONUTF8": "1"},
        )
        self.messages = queue.Queue()
        self.status = {}
        self.started_at = time.monotonic()
        self.startup_updates = []

        def read():
            for line in self.process.stdout:
                try:
                    message = json.loads(line)
                    if message.get("event") == "startup":
                        self.startup_updates.append({"elapsed_seconds": time.monotonic() - self.started_at, **message["data"]})
                    self.messages.put(message)
                except ValueError:
                    self.messages.put({"event": "fatal", "error": f"Non-JSON output on IPC: {line}"})
            self.messages.put({"event": "fatal", "error": "Worker pipe closed"})

        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()

    def call(self, command: str, args=None, timeout=180):
        identifier = uuid.uuid4().hex
        self.process.stdin.write(json.dumps({"id": identifier, "command": command, "args": args or {}}) + "\n")
        self.process.stdin.flush()
        deadline = time.monotonic() + timeout
        while True:
            message = self.messages.get(timeout=max(0.1, deadline - time.monotonic()))
            if message.get("event") == "status":
                self.status = message["data"]
            elif message.get("event") == "fatal":
                raise RuntimeError(message["error"])
            elif message.get("id") == identifier:
                if not message["ok"]:
                    raise RuntimeError(message["error"])
                return message["result"]

    def until(self, predicate, timeout=240, label="condition"):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            status = self.call("status")
            if status["model_state"] == "Error":
                raise RuntimeError(status["error"])
            if predicate(status):
                return status
            time.sleep(0.5)
        raise TimeoutError(f"Timed out waiting for {label}: {self.call('status')}")

    def close(self):
        if self.process.poll() is None:
            try:
                self.call("shutdown", timeout=10)
                self.process.wait(timeout=100)
            finally:
                if self.process.poll() is None:
                    self.process.kill()
                    self.process.wait()
        self.reader.join(timeout=3)
        self.log.close()


def main():
    with tempfile.TemporaryDirectory(prefix="ffa-integration-") as temporary:
        root = Path(temporary)
        corpus = root / "corpus-\u03b1"
        texts = create_corpus(corpus)
        for relative, expected in texts.items():
            if relative.endswith(".txt"):
                continue
            result = subprocess.run(
                [str(EXTRACTOR), str(corpus / relative)], capture_output=True,
                text=True, encoding="utf-8-sig", timeout=70, creationflags=subprocess.CREATE_NO_WINDOW,
            )
            assert result.returncode == 0, f"IFilter {relative}: {result.stderr}"
            text = json.loads(result.stdout)["text"]
            assert expected.split()[0] in text, f"IFilter produced no expected content for {relative}: {text!r}"
            print(f"PASS IFilter {Path(relative).suffix}", flush=True)

        data = root / "index"
        worker = Worker(data)
        try:
            ready = worker.until(lambda status: status["model_state"] == "Ready", label="real model ready")
            assert ready["cpu"]["core_limit"] <= ready["cpu"]["total_cores"] // 2, ready["cpu"]
            assert ready["cpu"]["worker_count"] * ready["cpu"]["threads_per_worker"] <= ready["cpu"]["core_limit"], ready["cpu"]
            print("PASS real EmbeddingGemma 2 loaded offline", flush=True)
            print("CPU BUDGET:", ready["cpu"], flush=True)
            folder = worker.call("add_folder", {"path": str(corpus)})["id"]
            indexed = worker.until(
                lambda status: status["folders"] and all(item["status"] == "Indexed" for item in status["folders"]),
                timeout=300, label="initial recursive multimodal indexing",
            )
            assert indexed["file_count"] == 8, indexed
            print("PASS indexed 8 real documents/images recursively", flush=True)
            print("MONITOR:", indexed["monitor"], flush=True)
            assert "USN journal" in indexed["monitor"], indexed["monitor"]

            results = worker.call("search", {"query": "growing fruit and harvesting apples", "threshold": 0.2})
            assert results and results[0]["name"] == "orchard-notes.txt", results
            print("PASS semantic text retrieval", results[0]["score"], flush=True)
            images = worker.call("search", {"query": "a red circle on a white background", "threshold": 0.1})
            assert images and images[0]["name"] == "image-a.png", images
            print("PASS direct text-to-image retrieval", images[0]["score"], flush=True)
            empty = worker.call("search", {"query": "deep sea submarine sonar", "threshold": 0.99})
            assert empty == [], empty
            print("PASS no-match threshold", flush=True)

            added = corpus / "fresh.txt"
            added.write_text("Freshly brewed coffee, espresso beans, cafe roasters and cappuccino.", encoding="utf-8")
            worker.until(lambda status: status["file_count"] == 9 and status["folders"][0]["status"] == "Indexed", timeout=90, label="live create")
            results = worker.call("search", {"query": "espresso and cappuccino coffee", "threshold": 0.2})
            assert results[0]["name"] == "fresh.txt", results
            print("PASS live file creation", flush=True)

            with closing(sqlite3.connect(data / "catalog.sqlite3")) as connection:
                old_id = connection.execute(
                    "SELECT MAX(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.path=?", (str(added),)
                ).fetchone()[0]
            added.write_text("A chamber orchestra plays violins, cellos and classical symphonies in a concert hall.", encoding="utf-8")

            def updated(_):
                with closing(sqlite3.connect(data / "catalog.sqlite3")) as connection:
                    new_id = connection.execute(
                        "SELECT MAX(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.path=?", (str(added),)
                    ).fetchone()[0]
                    return new_id is not None and new_id > old_id

            worker.until(updated, timeout=90, label="live modification")
            assert worker.call("search", {"query": "classical orchestra concert", "threshold": 0.2})[0]["name"] == "fresh.txt"
            print("PASS live file modification", flush=True)
            renamed = corpus / "renamed.txt"
            added.rename(renamed)
            worker.until(
                lambda _: any(result["name"] == "renamed.txt" for result in worker.call(
                    "search", {"query": "classical orchestra concert", "threshold": 0.2}
                )), timeout=90, label="live rename",
            )
            renamed.unlink()
            worker.until(lambda status: status["file_count"] == 8 and status["folders"][0]["status"] == "Indexed", timeout=90, label="live deletion")
            print("PASS live rename and deletion", flush=True)

            with closing(sqlite3.connect(data / "catalog.sqlite3")) as connection:
                saved_vectors = connection.execute("SELECT id FROM vectors ORDER BY id").fetchall()
            worker.close()
            worker = Worker(data)
            worker.until(lambda status: status["model_state"] == "Ready", label="restart")
            worker.until(lambda status: status["folders"][0]["status"] == "Indexed", label="restart reconciliation")
            with closing(sqlite3.connect(data / "catalog.sqlite3")) as connection:
                assert saved_vectors == connection.execute("SELECT id FROM vectors ORDER BY id").fetchall()
                assert connection.execute("SELECT COUNT(*) FROM index_jobs").fetchone()[0] == 0
            results = worker.call("search", {"query": "growing fruit and harvesting apples", "threshold": 0.2})
            assert results[0]["name"] == "orchard-notes.txt", results
            assert worker.call("status")["recovery"] == "", worker.call("status")["recovery"]
            print("PASS DiskANN restart persistence and USN cursor restore", flush=True)
            removed = worker.call("remove_folders", {"ids": [folder]})
            assert removed["folders"] == [] and removed["vector_count"] == 0, removed
            assert worker.call("search", {"query": "apples", "threshold": 0}) == []
            assert (corpus / "orchard-notes.txt").is_file()
            print("PASS removal purges vectors without deleting original files", flush=True)
        except Exception:
            print((data / "test-stderr.log").read_text(encoding="utf-8", errors="replace")[-16000:], file=sys.stderr)
            raise
        finally:
            worker.close()
    print("ALL END-TO-END CHECKS PASSED", flush=True)


if __name__ == "__main__":
    main()
