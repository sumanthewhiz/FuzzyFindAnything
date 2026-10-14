from pathlib import Path
import queue
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
from model_client import EmbeddingClient
from model_pool import EmbeddingPool
from startup import GIB, StartupProgress, plan_workers
from test_cpu_pool import FakeClient


class ProgressTests(unittest.TestCase):
    def test_progress_tracks_completed_checkpoints_without_regressing(self):
        values = []
        progress = StartupProgress(values.append)
        progress.update(1, "Catalog loaded")
        progress.update(2, "Monitoring prepared")
        for step in range(1, 7):
            progress.model_progress(step, f"Model checkpoint {step}")
        self.assertLess(values[-1]["percent"], 100)
        progress.ready()
        progress.update(3, "Delayed loading report")
        self.assertEqual("Ready", progress.snapshot()["state"])
        self.assertEqual(100, progress.snapshot()["percent"])
        self.assertEqual(sorted(value["percent"] for value in values), [value["percent"] for value in values])
        self.assertEqual(len(values), len({value["sequence"] for value in values}))

    def test_failure_is_not_reported_as_complete(self):
        progress = StartupProgress()
        progress.model_progress(2, "Importing libraries")
        progress.fail(RuntimeError("model unavailable"))
        progress.ready()
        self.assertEqual("Error", progress.snapshot()["state"])
        self.assertLess(progress.snapshot()["percent"], 100)
        self.assertIn("model unavailable", progress.snapshot()["error"])

    def test_memory_plan_preserves_the_shared_cpu_budget(self):
        for processors, memory, expected in ((6, 10 * GIB, (2, 3)), (6, 7 * GIB, (1, 6)), (1, 16 * GIB, (1, 1))):
            plan = plan_workers(processors, 2, memory)
            self.assertEqual(expected, (plan.workers, plan.threads))
            self.assertLessEqual(plan.workers * plan.threads, processors)
        with self.assertRaises(MemoryError):
            plan_workers(6, 2, 4 * GIB)

    def test_shutdown_during_initialization_does_not_publish_false_readiness(self):
        from indexer import IndexingEngine
        from test_engine import FakeModel
        with tempfile.TemporaryDirectory(prefix="ffa-startup-stop-") as temporary:
            root = Path(temporary)

            def factory(path, *, progress, is_cancelled):
                model = FakeModel(path, progress=progress)
                engine.stop_event.set()
                return model

            engine = IndexingEngine(root / "index", root / "model", root / "extractor.exe",
                                    root / "app", model_factory=factory, watch_changes=False)
            try:
                engine._load_model()
                self.assertIsNone(engine.model)
                self.assertNotEqual("Ready", engine.startup.snapshot()["state"])
                self.assertEqual([], engine.workers)
            finally:
                engine.stop()

    def client(self):
        client = object.__new__(EmbeddingClient)
        client.messages = queue.Queue()
        client.last_startup_stage = "Starting"
        return client

    def test_client_consumes_progress_without_confusing_it_with_readiness(self):
        client = self.client()
        client.messages.put({"event": "startup", "completed": 2, "total": 6, "stage": "Importing"})
        client.messages.put({"event": "startup", "completed": 6, "total": 6, "stage": "Ready"})
        client.messages.put({"ok": True, "result": "ready"})
        updates = []
        self.assertEqual("ready", client._receive(progress=lambda done, stage: updates.append((done, stage))))
        self.assertEqual([(2, "Importing"), (6, "Ready")], updates)
        self.assertTrue(client.messages.empty())

    def test_client_startup_cancellation_and_deadline_are_bounded(self):
        client = self.client()
        with self.assertRaises(InterruptedError):
            client._receive(is_cancelled=lambda: True)
        client.messages.put({"event": "startup", "completed": 2, "total": 6, "stage": "Importing libraries"})
        with self.assertRaisesRegex(TimeoutError, "Importing libraries"):
            client._receive(timeout=0.03)


class WarmupTests(unittest.TestCase):
    def setUp(self):
        budget = patch("model_pool.get_budget", return_value=CpuBudget(12, 6, 63, 2, 3))
        budget.start()
        self.addCleanup(budget.stop)

    def test_second_worker_does_not_gate_readiness_and_can_be_cancelled(self):
        second_started = threading.Event()
        constructed = threading.Event()
        release = threading.Event()
        result = {}
        created = []

        class BlockingClient(FakeClient):
            def __init__(self, path, threads, *, progress=None, is_cancelled=lambda: False):
                if created:
                    second_started.set()
                    while not release.wait(0.01):
                        if is_cancelled(): raise InterruptedError()
                super().__init__(path, threads, progress=progress, is_cancelled=is_cancelled)
                created.append(self)

        def construct():
            try:
                result["pool"] = EmbeddingPool(None, client_factory=BlockingClient, memory_available=lambda: 16 * GIB)
            except Exception as error:
                result["error"] = error
            finally:
                constructed.set()

        thread = threading.Thread(target=construct, daemon=True)
        thread.start()
        try:
            self.assertTrue(constructed.wait(2), "The primary model must make the pool usable before secondary startup.")
            self.assertNotIn("error", result)
            pool = result["pool"]
            self.assertTrue(second_started.wait(2))
            self.assertEqual(1, pool.ready_worker_count)
            self.assertEqual(12, len(pool.split("usable during warmup")))
            started = time.monotonic()
            pool.close()
            self.assertLess(time.monotonic() - started, 2)
            self.assertTrue(pool.warmup_complete.is_set())
            self.assertTrue(created[0].closed)
        finally:
            release.set()
            thread.join(timeout=3)
            if "pool" in result:
                result["pool"].close()

    def test_background_failure_keeps_primary_usable_and_reports_the_error(self):
        created = []

        def create(path, threads, **kwargs):
            if created:
                raise RuntimeError("secondary failed")
            client = FakeClient(path, threads, **kwargs)
            created.append(client)
            return client

        pool = EmbeddingPool(None, client_factory=create, memory_available=lambda: 16 * GIB)
        try:
            self.assertTrue(pool.warmup_complete.wait(2))
            self.assertEqual(1, pool.ready_worker_count)
            self.assertIn("secondary failed", pool.warmup_error)
            self.assertEqual(12, len(pool.split("still usable")))
        finally:
            pool.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
