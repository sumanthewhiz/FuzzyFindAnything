from contextlib import contextmanager
import json
import logging
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

from startup import MODEL_STEPS
from vectors import normalized

log = logging.getLogger(__name__)


class EmbeddingClient:
    def __init__(self, model_path: Path, threads: int | None = None, *, progress=None, is_cancelled=lambda: False):
        self.condition = threading.Condition()
        self.busy = False
        self.waiting_queries = 0
        self.closed = False
        self.messages = queue.Queue()
        self.last_startup_stage = "Starting inference worker"
        environment = {**os.environ, "PYTHONUTF8": "1", "HF_HUB_DISABLE_TELEMETRY": "1"}
        if threads:
            environment["FFA_INFERENCE_THREADS"] = str(threads)
            for name in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS"):
                environment[name] = str(threads)
        self.process = subprocess.Popen(
            [sys.executable, "-u", str(Path(__file__).with_name("model_worker.py")), "--model", str(model_path)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr,
            text=True, encoding="utf-8", close_fds=True, creationflags=subprocess.CREATE_NO_WINDOW,
            cwd=str(Path(sys.executable).parent),
            env=environment,
        )

        def read():
            try:
                for line in self.process.stdout:
                    try:
                        self.messages.put(json.loads(line))
                    except ValueError:
                        self.messages.put({"ok": False, "error": "The inference worker returned invalid JSON."})
            finally:
                self.messages.put({"ok": False, "error": "The local inference worker stopped unexpectedly."})

        self.reader = threading.Thread(target=read, name="Inference responses", daemon=True)
        self.reader.start()
        try:
            if self._receive(300, progress=progress, is_cancelled=is_cancelled) != "ready":
                raise RuntimeError("The inference worker did not finish initializing.")
        except Exception:
            if self.process.poll() is None:
                self.process.kill()
                self.process.wait()
            self.reader.join(timeout=3)
            self.process.stdin.close()
            self.process.stdout.close()
            self.closed = True
            raise

    def _receive(self, timeout=180, *, progress=None, is_cancelled=lambda: False):
        deadline = time.monotonic() + timeout
        while True:
            if is_cancelled():
                raise InterruptedError("Inference startup was cancelled.")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Local inference timed out during {self.last_startup_stage}. Close other applications and retry.")
            try:
                response = self.messages.get(timeout=min(0.2, remaining))
            except queue.Empty:
                continue
            if not isinstance(response, dict):
                raise RuntimeError("The inference worker returned an invalid response.")
            if response.get("event") == "startup":
                completed = response.get("completed")
                stage = response.get("stage")
                if (type(completed) is not int or not 0 <= completed <= MODEL_STEPS
                        or response.get("total") != MODEL_STEPS or not isinstance(stage, str) or not stage):
                    raise RuntimeError("The inference worker returned invalid startup progress.")
                self.last_startup_stage = stage
                if progress: progress(completed, stage)
                continue
            if not response.get("ok"):
                raise RuntimeError(response.get("error", "The inference worker failed."))
            return response["result"]

    @contextmanager
    def _slot(self, query=False):
        with self.condition:
            if query:
                self.waiting_queries += 1
            try:
                self.condition.wait_for(lambda: not self.busy and (query or not self.waiting_queries))
                self.busy = True
            finally:
                if query:
                    self.waiting_queries -= 1
        try:
            yield
        finally:
            with self.condition:
                self.busy = False
                self.condition.notify_all()

    def _request(self, operation, arguments, query=False):
        with self._slot(query):
            if self.closed or self.process.poll() is not None:
                raise RuntimeError("The local inference worker is not running.")
            self.process.stdin.write(json.dumps({"operation": operation, "args": arguments}, ensure_ascii=True) + "\n")
            self.process.stdin.flush()
            try:
                self.last_startup_stage = f"{operation} inference"
                return self._receive()
            except TimeoutError:
                self.process.kill()
                self.process.wait()
                self.closed = True
                raise

    def query(self, text):
        return normalized(self._request("query", {"text": text}, query=True))

    def document(self, title, text, is_cancelled, progress=None):
        if progress:
            progress(0, None)
        passages = self._request("split", {"text": text})
        chunks = []
        if progress:
            progress(0, len(passages))
        for start in range(0, len(passages), 4):
            if is_cancelled():
                raise InterruptedError("The folder was removed or the engine is shutting down.")
            batch = passages[start : start + 4]
            embeddings = self._request("documents", {"texts": [f"title: {title[:160]} | text: {passage}" for passage in batch]})
            if len(embeddings) != len(batch):
                raise RuntimeError("The inference worker returned an incomplete document batch.")
            for passage, embedding in zip(batch, embeddings):
                chunks.append((" ".join(passage.split())[:400], normalized(embedding)))
            if progress:
                progress(len(chunks), len(passages))
        return chunks

    def image(self, path):
        return [("Visual content indexed with EmbeddingGemma 2.", normalized(self._request("image", {"path": str(path)})))]

    def close(self):
        try:
            if not self.closed and self.process.poll() is None:
                self._request("shutdown", {})
                self.process.wait(timeout=15)
        except (OSError, RuntimeError, subprocess.TimeoutExpired, TimeoutError):
            log.exception("Inference worker shutdown failed")
            if self.process.poll() is None:
                self.process.kill()
                self.process.wait()
        finally:
            self.closed = True
            self.process.stdin.close()
            self.process.stdout.close()
            self.reader.join(timeout=3)
