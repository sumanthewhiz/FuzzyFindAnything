from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from contextlib import contextmanager
import logging
import threading

from cpu_budget import get_budget
from model_client import EmbeddingClient
from vectors import normalized

log = logging.getLogger(__name__)


class EmbeddingPool:
    def __init__(self, model_path, client_factory=EmbeddingClient, workers=None):
        self.budget = get_budget()
        self.worker_count = workers or self.budget.workers
        self.condition = threading.Condition()
        self.waiting_queries = 0
        self.closed = False
        self.clients = []
        self.model_path = model_path
        self.client_factory = client_factory
        try:
            for _ in range(self.worker_count):
                self.clients.append(client_factory(model_path, threads=self.budget.threads_per_worker))
        except Exception:
            for client in self.clients:
                client.close()
            raise
        self.available = list(self.clients)

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
                replacement = self.client_factory(self.model_path, threads=self.budget.threads_per_worker)
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
        with self.lease() as client:
            values = client._request("documents", {"texts": [f"title: {title[:160]} | text: {passage}" for passage in passages]})
        if len(values) != len(passages):
            raise RuntimeError("The inference worker returned an incomplete batch.")
        return [normalized(value) for value in values]

    def document(self, title, text, is_cancelled, progress=None):
        if progress: progress(0, None)
        passages = self.split(text)
        if progress: progress(0, len(passages))
        chunks = [None] * len(passages)
        batches = iter(range(0, len(passages), 4))
        completed = 0
        with ThreadPoolExecutor(max_workers=self.worker_count) as executor:
            pending = {}

            def submit():
                start = next(batches, None)
                if start is None: return
                batch = passages[start:start + 4]
                pending[executor.submit(self.embed_batch, title, batch)] = (start, batch)

            for _ in range(self.worker_count): submit()
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
        with self.lease() as client:
            return client.image(path)

    def close(self):
        with self.condition:
            self.closed = True
            self.condition.notify_all()
        with ThreadPoolExecutor(max_workers=len(self.clients)) as executor:
            list(executor.map(lambda client: client.close(), self.clients))
