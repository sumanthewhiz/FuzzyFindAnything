from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from cpu_budget import CpuBudget
from extraction import ExtractedText
from indexer import IndexingEngine
from inference_batches import InferenceBatcher
from model_pool import EmbeddingPool
from test_cpu_pool import FakeClient
from test_engine import FakeModel, vector


class BatchingTests(unittest.TestCase):
    def test_cross_file_batches_preserve_each_requests_results(self):
        calls = []

        def encode(operation, items):
            calls.append((operation, list(items)))
            return [item.upper() for item in items]

        batcher = InferenceBatcher(encode)
        futures = [batcher.submit("documents", [f"file-{index}-a", f"file-{index}-b"]) for index in range(6)]
        batcher.add_worker()
        try:
            for index, future in enumerate(futures):
                self.assertEqual([f"FILE-{index}-A", f"FILE-{index}-B"], future.result(timeout=2))
            self.assertEqual([8, 4], [len(items) for _, items in calls])
            self.assertEqual(12, batcher.statistics()["completed_inputs"])
        finally:
            batcher.close()

    def test_documents_and_images_are_not_mixed_or_oversized(self):
        calls = []

        def encode(operation, items):
            calls.append((operation, len(items)))
            return items

        batcher = InferenceBatcher(encode)
        futures = [batcher.submit("images", [f"image-{index}"]) for index in range(3)]
        futures.append(batcher.submit("documents", ["source"]))
        batcher.add_worker()
        try:
            for future in futures: future.result(timeout=2)
            self.assertEqual([("images", 2), ("images", 1), ("documents", 1)], calls)
        finally:
            batcher.close()

    def test_one_bad_file_does_not_fail_other_files_in_the_batch(self):
        def encode(operation, items):
            if "bad" in items: raise ValueError("invalid file")
            return items

        batcher = InferenceBatcher(encode)
        bad = batcher.submit("images", ["bad"])
        good = batcher.submit("images", ["good"])
        batcher.add_worker()
        try:
            with self.assertRaisesRegex(ValueError, "invalid file"): bad.result(timeout=2)
            self.assertEqual(["good"], good.result(timeout=2))
        finally:
            batcher.close()

    def test_very_different_text_lengths_are_not_padded_into_one_batch(self):
        calls = []
        batcher = InferenceBatcher(lambda operation, items: calls.append(len(items)) or items)
        futures = [batcher.submit("documents", [text]) for text in ("a" * 64, "b" * 2048, "c" * 64)]
        batcher.add_worker()
        try:
            for future in futures: future.result(timeout=2)
            self.assertEqual([1, 1, 1], calls)
        finally:
            batcher.close()

    def test_shutdown_releases_pending_requests_without_inference(self):
        calls = []
        batcher = InferenceBatcher(lambda operation, items: calls.append(items))
        future = batcher.submit("documents", ["pending"])
        batcher.close()
        with self.assertRaises(InterruptedError): future.result(timeout=1)
        with self.assertRaises(InterruptedError): batcher.submit("documents", ["late"])
        self.assertEqual([], calls)

    def test_partial_responses_are_not_published(self):
        batcher = InferenceBatcher(lambda operation, items: ["incomplete"])
        future = batcher.submit("documents", ["one", "two"])
        batcher.add_worker()
        try:
            with self.assertRaisesRegex(RuntimeError, "incomplete"):
                future.result(timeout=2)
        finally:
            batcher.close()

    def test_queries_keep_priority_over_waiting_inference_batches(self):
        entered = threading.Event()
        release = threading.Event()
        calls = []

        class Client(FakeClient):
            def _request(self, operation, arguments):
                calls.append("documents")
                if len(calls) == 1:
                    entered.set()
                    if not release.wait(4): raise TimeoutError("Test batch was not released.")
                return [vector() for _ in arguments["texts"]]

            def query(self, text):
                calls.append("query")
                return vector()

        with patch("model_pool.get_budget", return_value=CpuBudget(12, 6, 63, 2, 3)):
            pool = EmbeddingPool(None, client_factory=Client, workers=1, memory_available=lambda: 16 * 1024**3)
        executor = ThreadPoolExecutor(max_workers=3)
        try:
            first = executor.submit(pool.embed_batch, "first", ["body"])
            self.assertTrue(entered.wait(2))
            query = executor.submit(pool.query, "find")
            deadline = time.monotonic() + 2
            while not pool.waiting_queries and time.monotonic() < deadline: time.sleep(0.01)
            self.assertEqual(1, pool.waiting_queries)
            second = executor.submit(pool.embed_batch, "second", ["body"])
            release.set()
            first.result(timeout=2)
            query.result(timeout=2)
            second.result(timeout=2)
            self.assertEqual(["documents", "query", "documents"], calls)
        finally:
            release.set()
            executor.shutdown(wait=True, cancel_futures=True)
            pool.close()


class FilePipelineTests(unittest.TestCase):
    def test_one_model_does_not_limit_six_parallel_file_readers(self):
        with tempfile.TemporaryDirectory(prefix="ffa-parallel-files-") as temporary:
            root = Path(temporary).resolve()
            corpus = root / "files"
            corpus.mkdir()
            for index in range(6):
                (corpus / f"source-{index}.py").write_text(f"apple = {index}", encoding="utf-8")
            budget = CpuBudget(12, 6, 63, 2, 3)
            with patch("indexer.get_budget", return_value=budget):
                engine = IndexingEngine(root / "index", root / "model", root / "extractor.exe", root / "app",
                                        model_factory=FakeModel, watch_changes=False)
            gate = threading.Barrier(6, timeout=4)
            lock = threading.Lock()
            active = maximum = 0

            def extract(path, cancelled):
                nonlocal active, maximum
                with lock:
                    active += 1
                    maximum = max(maximum, active)
                try:
                    gate.wait()
                    return ExtractedText(path.read_text(encoding="utf-8"))
                finally:
                    with lock: active -= 1

            try:
                engine.add_folder(str(corpus))
                with patch.object(engine.extractor, "extract_details", side_effect=extract):
                    engine.start()
                    deadline = time.monotonic() + 12
                    while time.monotonic() < deadline and engine.store.counts() != (6, 6):
                        time.sleep(0.05)
                self.assertEqual(6, maximum)
                self.assertEqual(6, len(engine.workers))
                self.assertEqual((6, 6), engine.store.counts())
                self.assertEqual(6, engine.status()["cpu"]["file_worker_count"])
            finally:
                engine.stop()


if __name__ == "__main__":
    unittest.main(verbosity=2)
