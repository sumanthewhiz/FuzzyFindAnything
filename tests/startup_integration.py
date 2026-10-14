"""Opt-in real-model startup check; uses temporary data and never opens the WPF app."""

import json
from pathlib import Path
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
from integration import Worker


def main():
    with tempfile.TemporaryDirectory(prefix="ffa-startup-integration-") as temporary:
        worker = Worker(Path(temporary) / "index")
        try:
            status = worker.until(lambda value: value["model_state"] == "Ready", timeout=330, label="primary model readiness")
            updates = list(worker.startup_updates)
            assert updates and updates[-1]["state"] == "Ready" and updates[-1]["percent"] == 100, updates
            assert len({value["percent"] for value in updates}) >= 5, updates
            assert [value["percent"] for value in updates] == sorted(value["percent"] for value in updates), updates
            assert status["cpu"]["core_limit"] <= status["cpu"]["total_cores"] // 2, status["cpu"]
            assert status["cpu"]["worker_count"] >= 1, status["cpu"]
            for value in updates:
                print(json.dumps(value, ensure_ascii=True), flush=True)
            started = time.monotonic()
            assert worker.call("search", {"query": "local document retrieval", "threshold": 0.6}) == []
            print(json.dumps({"search_seconds": round(time.monotonic() - started, 3),
                              "ready_seconds": round(updates[-1]["elapsed_seconds"], 3),
                              "cpu": status["cpu"]}), flush=True)
        finally:
            started = time.monotonic()
            worker.close()
            elapsed = time.monotonic() - started
            print(json.dumps({"shutdown_seconds": round(elapsed, 3)}), flush=True)
            assert elapsed < 25, "Shutdown must cancel an unfinished secondary model startup promptly."
    print("REAL STARTUP, PROGRESS, SEARCH AND CANCELLATION CHECKS PASSED", flush=True)


if __name__ == "__main__":
    main()
