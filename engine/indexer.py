from dataclasses import dataclass, field
from concurrent.futures import ThreadPoolExecutor, as_completed
import logging
import os
from pathlib import Path
import sys
import threading
import time

from cpu_budget import get_budget
from extraction import ExtractionError, TextExtractor
from model_pool import EmbeddingPool
from paths import file_kind, fingerprint, is_within, path_key
from store import VectorStore
from startup import StartupProgress
from windows_changes import ChangeMonitor
from work_queue import WorkQueue

log = logging.getLogger(__name__)


@dataclass
class DirtyFolder:
    due: float
    paths: set[str] = field(default_factory=set)
    force: bool = False
    full_scan: bool = False


class SourceChanged(Exception):
    pass


class IndexingEngine:
    def __init__(
        self, data_path: Path, model_path: Path, extractor_path: Path, app_path: Path,
        *, model_factory=EmbeddingPool, watch_changes: bool = True,
        outcome_log=None,
        startup=None,
    ):
        self.startup = startup or StartupProgress()
        self.startup.update(0, "Opening the saved index")
        self.store = VectorStore(data_path)
        self.queue = WorkQueue(self.store)
        self.startup.update(1, "Preparing change monitoring")
        self.outcome_log = outcome_log
        self.model_path = model_path
        self.model_factory = model_factory
        self.cpu_budget = get_budget()
        self.file_worker_count = min(8, self.cpu_budget.processors)
        self.extractor = TextExtractor(extractor_path)
        self.exclusions = [Path(path).resolve() for path in (data_path, model_path, app_path, Path(sys.executable).parent)]
        self.condition = threading.Condition(threading.RLock())
        self.pending: dict[int, DirtyFolder] = {}
        self.stop_event = threading.Event()
        self.model = None
        self.model_state = "Loading"
        self.message = "Preparing search engine"
        self.error = ""
        self.scanning: set[int] = set()
        self.last_scans: dict[int, float] = {}
        self.identities: dict[int, dict[int, str]] = {}
        self.activities: dict[int, dict] = {}
        self.monitor = ChangeMonitor(self.store, self.condition, self.schedule) if watch_changes else None
        self.thread = threading.Thread(target=self._run, name="File discovery", daemon=True)
        self.loader = threading.Thread(target=self._load_model, name="Inference startup", daemon=True)
        self.workers: list[threading.Thread] = []

    def start(self):
        folders = self.store.folders()
        if self.monitor:
            self.monitor.configure(folders)
            self.monitor.start()
        for folder in folders:
            self.schedule(folder["id"], immediate=True)
        self.startup.update(2, "Starting the search model")
        self.thread.start()
        self.loader.start()

    def excluded(self, path: str | Path) -> bool:
        return any(is_within(path, root) for root in self.exclusions) or bool(self.outcome_log and self.outcome_log.owns(path))

    def schedule(self, folder_id: int, changed: str | None = None, force: bool = False, *, immediate=False):
        if changed and self.excluded(changed): return
        with self.condition:
            if self.stop_event.is_set(): return
            request = self.pending.setdefault(folder_id, DirtyFolder(time.monotonic() + (0 if immediate else 0.6)))
            if changed: request.paths.add(str(Path(changed).absolute()))
            else: request.full_scan = True
            request.force |= force
            if immediate: request.due = 0
            self.condition.notify_all()

    def add_folder(self, value: str) -> int:
        if not isinstance(value, str) or not value.strip():
            raise ValueError("Choose a folder to index.")
        path = Path(value).resolve(strict=True)
        if not path.is_dir(): raise ValueError("The selected path is not a folder.")
        if self.excluded(path): raise ValueError("Application-owned folders cannot be indexed.")
        identifier = self.store.add_folder(path)
        if self.monitor: self.monitor.configure(self.store.folders())
        self.schedule(identifier, immediate=True)
        return identifier

    def remove_folders(self, identifiers: list[int]):
        if not isinstance(identifiers, list) or any(type(value) is not int or value <= 0 for value in identifiers):
            raise ValueError("Folder identifiers must be positive integers.")
        self.store.remove_folders(identifiers)
        with self.condition:
            for identifier in identifiers:
                self.pending.pop(identifier, None)
                self.last_scans.pop(identifier, None)
                self.identities.pop(identifier, None)
        if self.monitor: self.monitor.configure(self.store.folders())

    def status(self):
        files, vectors = self.store.counts()
        folders = self.store.folders()
        with self.condition:
            scanning = set(self.scanning) | set(self.pending)
            activities = list(self.activities.values())
        for folder in folders:
            success, failed, queued, active, total = self.queue.folder_counts(folder["id"])
            folder.update(succeeded=success, failed=failed, queued=queued, active=active,
                          completed=max(0, total - queued), total=total, errors=failed)
            folder["status"] = "In Progress" if queued or folder["id"] in scanning else "Indexed"
            folder["detail"] = f"{success:,} succeeded  |  {failed:,} failed"
            if self.store.get_setting(f"scan_error:{folder['id']}"):
                folder["status"] = "In Progress"
        queue_counts = self.queue.counts()
        batches = self.model.batch_statistics() if self.model is not None and hasattr(self.model, "batch_statistics") else {}
        return {
            "model_state": self.model_state, "message": self.message,
            "error": self.error or getattr(self.model, "warmup_error", ""),
            "folders": folders, "file_count": files, "vector_count": vectors,
            "monitor": self.monitor.description if self.monitor else "Monitoring disabled",
            "recovery": self.store.recovery_message, "index_info": self.store.metadata(),
            "cpu": {"total_cores": self.cpu_budget.total_cores, "core_limit": self.cpu_budget.processors,
                    "worker_count": getattr(self.model, "ready_worker_count", getattr(self.model, "worker_count", 0)),
                    "threads_per_worker": getattr(self.model, "threads_per_worker", self.cpu_budget.threads_per_worker),
                    "file_worker_count": self.file_worker_count, "last_batch_size": batches.get("last_batch_size", 0)},
            "inference_batches": batches,
            "queue_count": sum(queue_counts.values()), "active_count": queue_counts.get("active", 0),
            "activities": activities,
            "logging": self.outcome_log.status() if self.outcome_log else {"enabled": False, "directory": "", "error": ""},
            "startup": self.startup.snapshot(),
        }

    def search(self, text: str, threshold=0.6, limit=50):
        if not isinstance(text, str) or not text.strip(): raise ValueError("Enter a search phrase.")
        if len(text) > 2048: raise ValueError("Search phrases must be under 2,048 characters.")
        if self.model_state != "Ready": raise RuntimeError(self.error or self.message)
        return self.store.search(self.model.query(text.strip()), limit, threshold)

    def _load_model(self):
        try:
            self.model = self.model_factory(self.model_path, progress=self.startup.model_progress,
                                            is_cancelled=self.stop_event.is_set)
            if self.stop_event.is_set():
                if hasattr(self.model, "close"): self.model.close()
                self.model = None
                return
            self.model_state = "Ready"
            self.message = "Search engine ready"
            log.info("Primary inference ready: available_workers=%s planned_workers=%s core_limit=%s total_cores=%s",
                     getattr(self.model, "ready_worker_count", 1), getattr(self.model, "worker_count", 1),
                     self.cpu_budget.processors, self.cpu_budget.total_cores)
            log.info("File pipeline: %s concurrent workers within the shared %s-core affinity budget.",
                     self.file_worker_count, self.cpu_budget.processors)
            for index in range(self.file_worker_count):
                worker = threading.Thread(target=self._work_loop, name=f"Index worker {index + 1}", daemon=True)
                self.workers.append(worker)
                worker.start()
            self.startup.ready()
        except InterruptedError:
            if not self.stop_event.is_set():
                self.model_state = "Error"
                self.error = "Inference startup was interrupted."
                self.startup.fail(self.error)
                log.exception(self.error)
            else:
                log.info("Inference startup cancelled during shutdown.")
        except Exception as error:
            log.exception("Inference startup failed")
            self.model_state = "Error"
            self.error = str(error)
            self.message = "Search engine unavailable"
            self.startup.fail(error)

    def _run(self):
        checkpoint_at = time.monotonic()
        while not self.stop_event.is_set():
            selected = None
            with self.condition:
                now = time.monotonic()
                for folder in self.store.folders():
                    if now - self.last_scans.get(folder["id"], now) >= 300:
                        self.pending.setdefault(folder["id"], DirtyFolder(0))
                for identifier, request in self.pending.items():
                    if request.due <= now:
                        selected = (identifier, request)
                        break
                if selected:
                    self.pending.pop(selected[0])
                    self.scanning.add(selected[0])
                else:
                    self.condition.wait(timeout=0.5)
            if selected:
                identifier, request = selected
                try:
                    self.reconcile(identifier, request)
                except Exception as error:
                    log.exception("Folder discovery failed: %s", identifier)
                    self.store.set_setting(f"scan_error:{identifier}", str(error))
                finally:
                    with self.condition:
                        self.scanning.discard(identifier)
                        self.last_scans[identifier] = time.monotonic()
                        if self.store.has_folder(identifier) and self.store.get_setting(f"scan_error:{identifier}"):
                            retry = self.pending.setdefault(identifier, DirtyFolder(time.monotonic() + 30))
                            retry.full_scan = True
                        # Every observed change is now a durable job or a completed catalog deletion.
                        if not self.pending and self.monitor:
                            self.monitor.checkpoint_cursors()
                        self.condition.notify_all()
            try:
                counts = self.queue.counts()
                if time.monotonic() - checkpoint_at >= 120 or not counts:
                    self.store.checkpoint()
                    checkpoint_at = time.monotonic()
            except Exception as error:
                log.exception("Index checkpoint failed")
                self.error = f"Index checkpoint failed: {error}"

    def _enqueue(self, identifier, path, force=False):
        if self.stop_event.is_set() or not self.store.has_folder(identifier): return
        kind = file_kind(path)
        if kind is None or self.excluded(path): return
        info = os.stat(path, follow_symlinks=False)
        if getattr(info, "st_reparse_tag", 0) & 0x20000000 or path.is_symlink():
            self.store.remove_path(path, recursive=True)
            return
        path = path.resolve(strict=True)
        folder = next((item for item in self.store.folders() if item["id"] == identifier), None)
        if folder is None or not is_within(path, folder["path"]) or self.excluded(path): return
        offline = getattr(info, "st_file_attributes", 0) & (0x1000 | 0x40000 | 0x400000)
        signature = fingerprint(path)
        old = self.store.get_file(path)
        unchanged = old and (old["mtime_ns"], old["size"], old["file_identity"]) == signature
        digest = ""
        if not offline and (force or not unchanged):
            try: digest = self.store._hash(path)
            except OSError:
                log.warning("Content fingerprint unavailable for %s; extraction will report its access error.", path)
        self.queue.enqueue(identifier, path, signature, kind, digest, force)
        self.identities.setdefault(identifier, {})[int(signature[2])] = str(path)

    def reconcile(self, identifier, request):
        folder = next((item for item in self.store.folders() if item["id"] == identifier), None)
        if not folder or self.stop_event.is_set(): return
        root = Path(folder["path"])
        errors = []
        if request.paths and not request.force and not request.full_scan:
            for changed in request.paths:
                path = Path(changed).resolve()
                if not is_within(path, root) or self.excluded(path): continue
                if path.is_dir():
                    self._discover(identifier, path, False, errors)
                elif path.is_file():
                    try: self._enqueue(identifier, path, True)
                    except OSError as error: errors.append(str(error))
                else:
                    self.store.remove_path(path, recursive=True)
        else:
            self._discover(identifier, root, request.force, errors)
        if self.monitor: self.monitor.update_identities(identifier, self.identities.get(identifier, {}))
        self.store.set_setting(f"scan_error:{identifier}", "; ".join(errors))
        if errors: log.warning("Folder discovery warnings for %s: %s", root, "; ".join(errors))
        with self.condition: self.condition.notify_all()

    def _discover(self, identifier, root, force, errors):
        stack, entries, seen = [root], [], set()
        initial_error_count = len(errors)
        identities = self.identities.setdefault(identifier, {})
        while stack and not self.stop_event.is_set() and self.store.has_folder(identifier):
            directory = stack.pop()
            try:
                identities[directory.stat().st_ino] = str(directory)
                with os.scandir(directory) as children:
                    for entry in children:
                        if self.excluded(entry.path): continue
                        try:
                            info = os.stat(entry.path, follow_symlinks=False)
                            if entry.is_symlink() or getattr(info, "st_reparse_tag", 0) & 0x20000000:
                                self.store.remove_path(Path(entry.path), recursive=True)
                                continue
                            if entry.is_dir(follow_symlinks=False):
                                stack.append(Path(entry.path))
                            elif entry.is_file(follow_symlinks=False) and file_kind(entry.path):
                                seen.add(path_key(entry.path))
                                entries.append((Path(entry.path), info.st_size))
                        except OSError as error: errors.append(f"{entry.path}: {error}")
            except OSError as error: errors.append(f"{directory}: {error}")
        for path, _ in sorted(entries, key=lambda item: (item[1], str(item[0]).lower())):
            try: self._enqueue(identifier, path, force)
            except FileNotFoundError: seen.discard(path_key(path))
            except OSError as error: errors.append(f"{path}: {error}")
        if len(errors) == initial_error_count and not self.stop_event.is_set():
            self.queue.prune(identifier, root, seen)

    def _work_loop(self):
        while not self.stop_event.is_set():
            job = self.queue.claim()
            if job is None:
                self.stop_event.wait(0.3)
                continue
            self.process_job(job)

    def _activity(self, job, stage):
        done, total = self.queue.progress(job)
        with self.condition:
            self.activities[threading.get_ident()] = {"path": job["path"], "stage": stage,
                                                     "completed_passages": done, "total_passages": total}

    def process_job(self, job):
        path = Path(job["path"])
        started = time.monotonic()
        elapsed = lambda: (time.monotonic() - started) * 1000
        cancelled = lambda: self.stop_event.is_set() or not self.queue.current(job)
        signature = (job["mtime_ns"], job["size"], job["file_identity"])
        try:
            if cancelled(): raise InterruptedError()
            if fingerprint(path) != signature: raise SourceChanged()
            if getattr(path.stat(), "st_file_attributes", 0) & (0x1000 | 0x40000 | 0x400000):
                raise ExtractionError("The file is online-only. Make it available on this device to index it.",
                                      "cloud_unavailable", True)
            if not job["prepared"]:
                if job["kind"] == "Image":
                    self.queue.prepare(job, ["Visual content"])
                else:
                    self._activity(job, "Extracting")
                    extracted = self.extractor.extract_details(path, cancelled)
                    self._activity(job, "Preparing")
                    passages = self.model.split(extracted.text)
                    if not passages: raise ExtractionError("No searchable text was extracted.", "no_text")
                    if cancelled(): raise InterruptedError()
                    if fingerprint(path) != signature: raise SourceChanged()
                    self.queue.prepare(job, passages, " ".join(extracted.warnings) or None)
            # Yield after a bounded slice so large files do not monopolize the durable queue.
            if cancelled(): raise InterruptedError()
            remaining = self.queue.remaining(job, count=16)
            self._activity(job, "Embedding")
            if job["kind"] == "Image" and remaining:
                if not self.queue.save_batch(job, remaining, [self.model.image(path)[0][1]]):
                    raise InterruptedError()
            elif remaining:
                failure = None
                with ThreadPoolExecutor(max_workers=getattr(self.model, "batch_concurrency", min(2, self.cpu_budget.processors))) as executor:
                    pending = {}
                    for start in range(0, len(remaining), 4):
                        batch = remaining[start:start + 4]
                        pending[executor.submit(self.model.embed_batch, path.name, [row["content"] for row in batch])] = batch
                    for future in as_completed(pending):
                        try:
                            values = future.result()
                            if not self.queue.save_batch(job, pending[future], values): raise InterruptedError()
                            self._activity(job, "Embedding")
                        except Exception as error:
                            failure = failure or error
                        if failure or cancelled():
                            for queued in pending: queued.cancel()
                if cancelled(): raise InterruptedError()
                if failure: raise failure
            if cancelled(): raise InterruptedError()
            done, total = self.queue.progress(job)
            if done == total and total:
                if fingerprint(path) != signature: raise SourceChanged()
                if job["content_hash"] and self.store._hash(path) != job["content_hash"]: raise SourceChanged()
                self._activity(job, "Saving")
                if self.queue.complete(job):
                    log.info("File indexed: %s; duration_ms=%.1f; passages=%s", path, job["elapsed_ms"] + elapsed(), total)
                    if self.outcome_log:
                        current = self.store.get_file(path)
                        self.outcome_log.record_file(job, "Succeeded", job["elapsed_ms"] + elapsed(), passages=total,
                                                     warning=current["warning"] if current else None)
            else:
                self.queue.release(job, elapsed())
        except InterruptedError:
            self.queue.release(job, elapsed())
        except FileNotFoundError as error:
            if path.exists():
                self._fail_job(job, error, job["elapsed_ms"] + elapsed(), code="missing_dependency")
            else:
                with self.store.lock:
                    if self.queue.current(job): self.store.remove_path(path)
                log.info("File removed before indexing completed: %s", path)
        except SourceChanged:
            try:
                for owner in self.queue.owners(job):
                    try: self._enqueue(owner, path, True)
                    except FileNotFoundError: self.store.remove_path(path)
                self.queue.release(job, elapsed())
            except OSError as error:
                self._fail_job(job, error, job["elapsed_ms"] + elapsed())
        except Exception as error:
            self._fail_job(job, error, job["elapsed_ms"] + elapsed())
        finally:
            with self.condition:
                self.activities.pop(threading.get_ident(), None)
                self.condition.notify_all()

    def _fail_job(self, job, error, elapsed_ms, code=None):
        code = code or (error.code if isinstance(error, ExtractionError) else "indexing_failure")
        retryable = not isinstance(error, ExtractionError) or error.retryable
        if not self.queue.fail(job, str(error), code, retryable, elapsed_ms):
            return
        log.error("File indexing failed: %s; duration_ms=%.1f; code=%s",
                  job["path"], elapsed_ms, code, exc_info=error)
        if self.outcome_log:
            self.outcome_log.record_file(job, "Failed", elapsed_ms, error=error, code=code)

    def stop(self):
        self.stop_event.set()
        with self.condition: self.condition.notify_all()
        if self.monitor: self.monitor.stop()
        for thread in (self.thread, self.loader):
            if thread.ident is not None: thread.join(timeout=90)
        for thread in self.workers:
            if thread.ident is not None: thread.join(timeout=90)
        if any(thread.is_alive() for thread in (self.thread, self.loader, *self.workers)):
            raise TimeoutError("Indexing is still stopping; durable work will resume on the next launch.")
        if self.model is not None and hasattr(self.model, "close"): self.model.close()
        self.store.close()
