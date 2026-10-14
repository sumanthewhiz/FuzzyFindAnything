"""Bounded cross-file batching; each result retains its originating request."""

from collections import deque
from concurrent.futures import Future
from dataclasses import dataclass
import logging
import threading
import time

TEXT_BATCH_SIZE = 8
IMAGE_BATCH_SIZE = 2
LIMITS = {"documents": TEXT_BATCH_SIZE, "images": IMAGE_BATCH_SIZE}
log = logging.getLogger(__name__)


@dataclass
class BatchRequest:
    operation: str
    items: list
    future: Future
    length_bucket: int


class InferenceBatcher:
    def __init__(self, encode, *, is_cancelled=lambda: False, window=0.02, capacity=64):
        self.encode = encode
        self.is_cancelled = is_cancelled
        self.window = window
        self.capacity = capacity
        self.condition = threading.Condition()
        self.pending = deque()
        self.threads = []
        self.closed = False
        self.batches = 0
        self.items = 0
        self.last_batch_size = 0
        self.largest_batch = 0
        self.active = 0

    def submit(self, operation, items):
        if operation not in LIMITS or not 1 <= len(items) <= LIMITS[operation]:
            raise ValueError("Invalid inference batch request.")
        future = Future()
        with self.condition:
            while len(self.pending) >= self.capacity and not self.closed and not self.is_cancelled():
                self.condition.wait(timeout=0.05)
            if self.closed or self.is_cancelled():
                raise InterruptedError("Inference batching is stopping.")
            bucket = max(len(item) for item in items).bit_length() if operation == "documents" else 0
            self.pending.append(BatchRequest(operation, list(items), future, bucket))
            self.condition.notify_all()
        return future

    def add_worker(self):
        with self.condition:
            if self.closed or self.is_cancelled():
                return
            thread = threading.Thread(target=self._run, name=f"Inference batches {len(self.threads) + 1}", daemon=True)
            self.threads.append(thread)
            thread.start()

    def _cancel_pending(self):
        while self.pending:
            request = self.pending.popleft()
            if not request.future.done():
                request.future.set_exception(InterruptedError("Inference batching was cancelled."))
        self.condition.notify_all()

    def _next_batch(self):
        with self.condition:
            while not self.pending and not self.closed and not self.is_cancelled():
                self.condition.wait(timeout=0.05)
            if self.closed or self.is_cancelled():
                self._cancel_pending()
                return None
            first = self.pending.popleft()
            if not first.future.set_running_or_notify_cancel():
                return []
            group = [first]
            count = len(first.items)
            deadline = time.monotonic() + self.window
            while count < LIMITS[first.operation]:
                if self.pending:
                    request = self.pending[0]
                    if (request.operation != first.operation or request.length_bucket != first.length_bucket
                            or count + len(request.items) > LIMITS[first.operation]):
                        break
                    self.pending.popleft()
                    if request.future.set_running_or_notify_cancel():
                        group.append(request)
                        count += len(request.items)
                else:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0 or self.closed or self.is_cancelled():
                        break
                    self.condition.wait(timeout=remaining)
            self.condition.notify_all()
            self.active += 1
            return group

    def _run(self):
        while True:
            group = self._next_batch()
            if group is None:
                return
            if not group:
                continue
            try:
                if self.closed or self.is_cancelled():
                    raise InterruptedError("Inference batching was cancelled.")
                items = [item for request in group for item in request.items]
                values = self.encode(group[0].operation, items)
                if len(values) != len(items):
                    raise RuntimeError("The inference worker returned an incomplete cross-file batch.")
                self._record_batch(len(items))
                offset = 0
                for request in group:
                    request.future.set_result(values[offset:offset + len(request.items)])
                    offset += len(request.items)
            except Exception as error:
                if len(group) > 1 and not isinstance(error, InterruptedError) and not self.closed and not self.is_cancelled():
                    log.warning("Combined inference failed; isolating %s requests: %s", len(group), error)
                    for request in group:
                        try:
                            if self.closed or self.is_cancelled():
                                raise InterruptedError("Inference batching was cancelled.")
                            values = self.encode(request.operation, request.items)
                            if len(values) != len(request.items):
                                raise RuntimeError("The inference worker returned an incomplete request.")
                            self._record_batch(len(request.items))
                            request.future.set_result(values)
                        except Exception as individual_error:
                            if not request.future.done():
                                request.future.set_exception(individual_error)
                else:
                    for request in group:
                        if not request.future.done():
                            request.future.set_exception(error)
            finally:
                with self.condition:
                    self.active -= 1
                    self.condition.notify_all()

    def _record_batch(self, count):
        with self.condition:
            self.batches += 1
            self.items += count
            self.last_batch_size = count
            self.largest_batch = max(self.largest_batch, count)

    def statistics(self):
        with self.condition:
            return {"completed_batches": self.batches, "completed_inputs": self.items,
                    "last_batch_size": self.last_batch_size, "largest_batch": self.largest_batch,
                    "active_batches": self.active, "pending_requests": len(self.pending)}

    def close(self):
        with self.condition:
            self.closed = True
            self._cancel_pending()
        for thread in self.threads:
            thread.join(timeout=185)
            if thread.is_alive():
                raise TimeoutError("An inference batch did not finish within its response deadline.")
