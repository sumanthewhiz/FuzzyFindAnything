"""Opt-in warm-model queue benchmark; all files and indexes are temporary."""

import ctypes
from ctypes import wintypes
import json
from pathlib import Path
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from cpu_budget import get_budget

BUDGET = get_budget(apply=True)
from indexer import IndexingEngine
from model_pool import EmbeddingPool


def process_cpu(process):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    times = [wintypes.FILETIME() for _ in range(4)]
    if not kernel.GetProcessTimes(int(process._handle), *(ctypes.byref(value) for value in times)):
        raise ctypes.WinError(ctypes.get_last_error())
    return sum((value.dwHighDateTime << 32) | value.dwLowDateTime for value in times[2:]) / 10_000_000


def main():
    model = EmbeddingPool(ROOT / "models" / "embeddinggemma-2", workers=1)
    outcomes = []
    try:
        model.query("parsing structured records")
        for workers in (1, min(8, BUDGET.processors)):
            with tempfile.TemporaryDirectory(prefix="ffa-throughput-") as temporary:
                root = Path(temporary)
                corpus = root / "files"
                corpus.mkdir()
                for index in range(18):
                    (corpus / f"source-{index}.py").write_text(
                        f"def parse_records_{index}(rows):\n"
                        "    # Validate records, discard missing values, normalize fields, and store the result.\n"
                        "    return [row.strip().lower() for row in rows if row]\n",
                        encoding="utf-8",
                    )

                class BorrowedModel:
                    def __init__(self, path, *, progress=None, is_cancelled=lambda: False):
                        if progress: progress(6, "Warm benchmark model")

                    def __getattr__(self, name):
                        return getattr(model, name)

                    def close(self):
                        pass

                engine = IndexingEngine(root / "index", ROOT / "models" / "embeddinggemma-2",
                                        root / "unused-extractor.exe", root / "application",
                                        model_factory=BorrowedModel, watch_changes=False)
                engine.file_worker_count = workers
                try:
                    engine.add_folder(str(corpus))
                    before = model.batch_statistics()
                    cpu_before = process_cpu(model.clients[0].process) + time.process_time()
                    started = time.monotonic()
                    engine.start()
                    maximum_active = 0
                    while engine.store.counts()[0] < 18:
                        status = engine.status()
                        maximum_active = max(maximum_active, status["active_count"])
                        if status["model_state"] == "Error" or time.monotonic() - started > 240:
                            raise RuntimeError(f"Benchmark did not finish: {status}")
                        time.sleep(0.02)
                    elapsed = time.monotonic() - started
                    cpu = process_cpu(model.clients[0].process) + time.process_time() - cpu_before
                    after = model.batch_statistics()
                    batches = after["completed_batches"] - before["completed_batches"]
                    inputs = after["completed_inputs"] - before["completed_inputs"]
                    result = {"file_workers": workers, "files": 18, "seconds": round(elapsed, 3),
                              "files_per_second": round(18 / elapsed, 3), "maximum_active_files": maximum_active,
                              "inference_batches": batches, "mean_batch_size": round(inputs / batches, 2),
                              "average_cpu_cores": round(cpu / elapsed, 2), "core_limit": BUDGET.processors}
                    outcomes.append(result)
                    print(json.dumps(result), flush=True)
                finally:
                    engine.stop()
        print(json.dumps({"throughput_ratio": round(outcomes[0]["seconds"] / outcomes[1]["seconds"], 2)}), flush=True)
    finally:
        model.close()


if __name__ == "__main__":
    main()
