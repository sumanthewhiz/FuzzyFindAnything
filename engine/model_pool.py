from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from contextlib import contextmanager
import logging
import threading

from cpu_budget import get_budget
from inference_batches import InferenceBatcher
from model_client import EmbeddingClient
from startup import available_memory, plan_workers, WORKER_MEMORY, MEMORY_RESERVE, GIB
from vectors import normalized

log = logging.getLogger(__name__)


class EmbeddingPool:
    def __init__(self, model_path, client_factory=EmbeddingClient, workers=None, *, progress=None,
                 is_cancelled=lambda: False, memory_available=available_memory):
        self.budget = get_budget()
        memory = memory_available()
        plan = plan_workers(self.budget.processors, workers or self.budget.workers, memory)
        self.worker_count = plan.workers
        self.threads_per_worker = plan.threads
        self.batch_concurrency = min(self.budget.processors, 2 * self.worker_count)
        self.condition = threading.Condition()
        self.waiting_queries = 0
        self.closed = False
        self.clients = []
        self.available = []
        self.model_path = model_path
        self.client_factory = client_factory
        self.memory_available = memory_available
        self.external_cancelled = is_cancelled
        self.stop_event = threading.Event()
        self.warmup_complete = threading.Event()
        self.warmup_error = ""
        self.initializer = None
        log.info("Inference plan: %s workers, %s threads each, %.1f GiB available; first worker gates readiness.",
                 self.worker_count, self.threads_per_worker, memory / GIB)
        try:
            client = client_factory(model_path, threads=self.threads_per_worker, progress=progress,
                                    is_cancelled=self._cancelled)
            self.clients.append(client)
            self.available.append(client)
        except Exception:
            for client in self.clients:
                client.close()
            raise
        self.batcher = InferenceBatcher(self._encode_group, is_cancelled=self._cancelled)
        self.batcher.add_worker()
        if self.worker_count > 1:
            self.initializer = threading.Thread(target=self._warm_remaining, name="Additional inference startup", daemon=True)
            self.initializer.start()
        else:
            self.warmup_complete.set()

    def _cancelled(self):
        return self.stop_event.is_set() or self.external_cancelled()

    @property
    def ready_worker_count(self):
        with self.condition:
            return sum(not getattr(client, "closed", False) and (
                getattr(client, "process", None) is None or client.process.poll() is None
            ) for client in self.clients)

    def _warm_remaining(self):
        try:
            for _ in range(1, self.worker_count):
                if self._cancelled(): return
                if self.memory_available() < WORKER_MEMORY + MEMORY_RESERVE:
                    raise MemoryError("The additional inference worker was not started because available memory became low.")
                client = self.client_factory(self.model_path, threads=self.threads_per_worker,
                                             is_cancelled=self._cancelled)
                with self.condition:
                    accepted = not self.closed and not self._cancelled()
                    if accepted:
                        self.clients.append(client)
                        self.available.append(client)
                        self.condition.notify_all()
                if not accepted:
                    client.close()
                    return
                self.batcher.add_worker()
                log.info("Additional inference worker ready; %s workers available.", self.ready_worker_count)
        except InterruptedError:
            log.info("Additional inference startup cancelled.")
        except Exception as error:
            self.warmup_error = f"Search remains available with one worker. Additional worker startup failed: {error}"
            log.warning(self.warmup_error, exc_info=True)
        finally:
            self.warmup_complete.set()

    @contextmanager
    def lease(self, query=False):
        with self.condition:
            if query:
                self.waiting_queries += 1
            try:
                self.condition.wait_for(lambda: self.closed or (self.available and (query or not self.waiting_queries)))
                if self.closed:
                    raise RuntimeError("The inference pool is stopped.")
                client = self.available.pop()
            finally:
                if query:
                    self.waiting_queries -= 1
        try:
            process = getattr(client, "process", None)
            if getattr(client, "closed", False) or (process is not None and process.poll() is not None):
                log.warning("Restarting an unavailable inference worker; durable passage progress is retained.")
                client.close()
                replacement = self.client_factory(self.model_path, threads=self.threads_per_worker,
                                                  is_cancelled=self._cancelled)
                with self.condition:
                    self.clients[self.clients.index(client)] = replacement
                client = replacement
            yield client
        finally:
            with self.condition:
                self.available.append(client)
                self.condition.notify_all()

    def query(self, text):
        with self.lease(query=True) as client:
            return client.query(text)

    def split(self, text):
        with self.lease() as client:
            return client._request("split", {"text": text})

    def embed_batch(self, title, passages):
        if not passages:
            return []
        texts = [f"title: {title[:160]} | text: {passage}" for passage in passages]
        return self.batcher.submit("documents", texts).result()

    def _encode_group(self, operation, items):
        with self.lease() as client:
            if self._cancelled():
                raise InterruptedError("Inference batching was cancelled.")
            arguments = {"texts": items} if operation == "documents" else {"paths": items}
            values = client._request(operation, arguments)
        if len(values) != len(items):
            raise RuntimeError("The inference worker returned an incomplete batch.")
        return [normalized(value) for value in values]

    def document(self, title, text, is_cancelled, progress=None):
        if progress: progress(0, None)
        passages = self.split(text)
        if progress: progress(0, len(passages))
        chunks = [None] * len(passages)
        batches = iter(range(0, len(passages), 4))
        completed = 0
        with ThreadPoolExecutor(max_workers=self.batch_concurrency) as executor:
            pending = {}

            def submit():
                start = next(batches, None)
                if start is None: return
                batch = passages[start:start + 4]
                pending[executor.submit(self.embed_batch, title, batch)] = (start, batch)

            for _ in range(self.batch_concurrency): submit()
            while pending:
                if is_cancelled():
                    for future in pending: future.cancel()
                    raise InterruptedError("Indexing was cancelled.")
                done, _ = wait(pending, timeout=0.2, return_when=FIRST_COMPLETED)
                for future in done:
                    start, batch = pending.pop(future)
                    values = future.result()
                    for offset, (passage, value) in enumerate(zip(batch, values)):
                        chunks[start + offset] = (" ".join(passage.split())[:400], value)
                    completed += len(batch)
                    if progress: progress(completed, len(passages))
                    submit()
        return chunks

    def image(self, path):
        values = self.batcher.submit("images", [str(path)]).result()
        return [("Visual content indexed with EmbeddingGemma 2.", values[0])]

    def batch_statistics(self):
        return self.batcher.statistics()

    def close(self):
        self.stop_event.set()
        with self.condition:
            self.closed = True
            self.condition.notify_all()
        if self.initializer is not None:
            self.initializer.join(timeout=15)
            if self.initializer.is_alive():
                raise TimeoutError("The additional inference worker did not stop.")
        self.batcher.close()
        if self.clients:
            with ThreadPoolExecutor(max_workers=len(self.clients)) as executor:
                list(executor.map(lambda client: client.close(), self.clients))
