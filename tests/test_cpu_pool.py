import json
from pathlib import Path
import subprocess
import sys
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
from cpu_budget import get_budget, select_mask
from model_pool import EmbeddingPool
from vectors import DIMENSIONS


class CpuBudgetTests(unittest.TestCase):
    def test_half_physical_cores_and_one_smt_thread_per_core(self):
        masks = [3 << (2 * index) for index in range(16)]
        selected = select_mask(masks, (1 << 32) - 1, 16)
        self.assertEqual(8, selected.bit_count())
        self.assertTrue(all((selected & core).bit_count() <= 1 for core in masks))

    def test_existing_restrictions_are_never_widened(self):
        self.assertEqual(0b101, select_mask([1, 2, 4, 8], 0b101, 4))
        budget = get_budget()
        self.assertLessEqual(budget.processors, budget.total_cores // 2)
        self.assertLessEqual(budget.workers * budget.threads_per_worker, budget.processors)

    def test_worker_affinity_is_inherited_by_children(self):
        script = (
            "import sys,json,subprocess;sys.path.insert(0,sys.argv[1]);"
            "from cpu_budget import get_budget;b=get_budget(apply=True);"
            "out=subprocess.check_output([sys.executable,'-c',"
            "'import sys,json;sys.path.insert(0,sys.argv[1]);from cpu_budget import get_budget;"
            "b=get_budget();print(json.dumps([b.total_cores,b.processors,b.affinity_mask]))',sys.argv[1]],text=True);"
            "print(json.dumps({'parent':[b.total_cores,b.processors,b.affinity_mask],'child':json.loads(out)}))"
        )
        result = subprocess.run([sys.executable, "-c", script, str(ROOT / "engine")], capture_output=True, text=True, timeout=20, check=True)
        evidence = json.loads(result.stdout)
        self.assertEqual(evidence["parent"], evidence["child"])


class FakeClient:
    active = 0
    maximum = 0
    lock = threading.Lock()

    def __init__(self, path, threads):
        self.threads = threads

    def _request(self, operation, args):
        if operation == "split": return [f"passage {i}" for i in range(12)]
        with self.lock:
            FakeClient.active += 1
            FakeClient.maximum = max(FakeClient.maximum, FakeClient.active)
        try:
            time.sleep(0.04)
            return [[1.0] + [0.0] * (DIMENSIONS - 1) for _ in args["texts"]]
        finally:
            with self.lock: FakeClient.active -= 1

    def close(self):
        pass


class PoolTests(unittest.TestCase):
    def test_parallel_batches_preserve_order_and_progress(self):
        FakeClient.maximum = 0
        pool = EmbeddingPool(None, client_factory=FakeClient, workers=2)
        updates = []
        try:
            values = pool.document("title", "text", lambda: False, lambda done, total: updates.append((done, total)))
            self.assertEqual([f"passage {i}" for i in range(12)], [item[0] for item in values])
            self.assertEqual(2, FakeClient.maximum)
            self.assertEqual((12, 12), updates[-1])
        finally: pool.close()

    def test_unavailable_worker_is_replaced_before_reuse(self):
        pool = EmbeddingPool(None, client_factory=FakeClient, workers=1)
        original = pool.clients[0]
        original.closed = True
        try:
            self.assertEqual(12, len(pool.split("text")))
            self.assertIsNot(original, pool.clients[0])
        finally: pool.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
