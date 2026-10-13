"""Durable file jobs and passage checkpoints; one coordinator owns the SQLite connection."""

import time
from pathlib import Path

import numpy as np

from paths import is_within, path_key
from vectors import normalized


class WorkQueue:
    def __init__(self, store):
        self.store = store
        self.db = store.db
        self.lock = store.lock
        with self.lock, self.db:
            self.db.execute("UPDATE index_jobs SET state='pending',scheduled=0 WHERE state='active'")
            self.db.execute("DELETE FROM index_jobs WHERE NOT EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id)")

    def enqueue(self, folder_id, path, signature, kind, digest="", force=False):
        with self.lock, self.db:
            if not self.store.has_folder(folder_id): return False
            key = path_key(path)
            old = self.store.get_file(path)
            same = old and (old["mtime_ns"], old["size"], old["file_identity"]) == signature
            if old and not force and same and (
                old["error"] is None or old["retry_after"] is None or old["retry_after"] > time.time()
            ):
                self.store.attach_file(folder_id, old["id"])
                return False
            if old and digest and old["content_hash"] == digest and (
                old["error"] is None or old["retry_after"] is None or old["retry_after"] > time.time()
            ):
                self.db.execute("UPDATE files SET path=?,mtime_ns=?,size=?,file_identity=? WHERE id=?", (str(path), *signature, old["id"]))
                if old["error"] is None:
                    self.db.execute("DELETE FROM index_jobs WHERE path_key=?", (key,))
                self.store.attach_file(folder_id, old["id"])
                return False
            job = self.db.execute("SELECT * FROM index_jobs WHERE path_key=?", (key,)).fetchone()
            if job:
                self.db.execute("INSERT OR IGNORE INTO job_folders VALUES(?,?)", (job["id"], folder_id))
                same_job = (job["mtime_ns"], job["size"], job["file_identity"]) == signature
                if same_job and (not force or not digest or job["content_hash"] == digest):
                    return False
                self.db.execute("DELETE FROM job_passages WHERE job_id=?", (job["id"],))
                self.db.execute(
                    "UPDATE index_jobs SET path=?,mtime_ns=?,size=?,file_identity=?,content_hash=?,kind=?,"
                    "revision=revision+1,state='pending',prepared=0,warning=NULL,attempts=0,retry_at=0,scheduled=?,elapsed_ms=0 WHERE id=?",
                    (str(path), *signature, digest, kind, time.time(), job["id"]),
                )
            else:
                cursor = self.db.execute(
                    "INSERT INTO index_jobs(path,path_key,mtime_ns,size,file_identity,content_hash,kind,scheduled) VALUES(?,?,?,?,?,?,?,?)",
                    (str(path), key, *signature, digest, kind, time.time()),
                )
                self.db.execute("INSERT INTO job_folders VALUES(?,?)", (cursor.lastrowid, folder_id))
            return True

    def claim(self):
        with self.lock, self.db:
            row = self.db.execute(
                "SELECT * FROM index_jobs WHERE state='pending' AND retry_at<=? "
                "AND EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id) ORDER BY scheduled,size,id LIMIT 1",
                (time.time(),),
            ).fetchone()
            if not row: return None
            self.db.execute("UPDATE index_jobs SET state='active' WHERE id=?", (row["id"],))
            return dict(row)

    def current(self, job):
        with self.lock:
            return self.db.execute(
                "SELECT 1 FROM index_jobs WHERE id=? AND revision=? AND EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id)",
                (job["id"], job["revision"]),
            ).fetchone() is not None

    def owners(self, job):
        with self.lock:
            return [row[0] for row in self.db.execute("SELECT folder_id FROM job_folders WHERE job_id=? ORDER BY folder_id", (job["id"],))]

    def prepare(self, job, passages, warning=None):
        if not passages: raise ValueError("The document contains no searchable passages.")
        with self.lock, self.db:
            if not self.current(job): return False
            existing = self.db.execute("SELECT prepared FROM index_jobs WHERE id=?", (job["id"],)).fetchone()[0]
            if existing: return True
            self.db.executemany("INSERT INTO job_passages(job_id,ordinal,content) VALUES(?,?,?)",
                                [(job["id"], ordinal, text) for ordinal, text in enumerate(passages)])
            self.db.execute("UPDATE index_jobs SET prepared=1,warning=? WHERE id=?", (warning, job["id"]))
            return True

    def remaining(self, job, count=4):
        with self.lock:
            if not self.current(job): return []
            return [dict(row) for row in self.db.execute(
                "SELECT ordinal,content FROM job_passages WHERE job_id=? AND embedding IS NULL ORDER BY ordinal LIMIT ?",
                (job["id"], count),
            )]

    def save_batch(self, job, passages, vectors):
        if len(passages) != len(vectors): raise ValueError("Incomplete inference batch.")
        prepared = [(normalized(vector).tobytes(), job["id"], row["ordinal"]) for row, vector in zip(passages, vectors)]
        with self.lock, self.db:
            if not self.current(job): return False
            self.db.executemany("UPDATE job_passages SET embedding=? WHERE job_id=? AND ordinal=? AND embedding IS NULL", prepared)
            return True

    def progress(self, job):
        with self.lock:
            row = self.db.execute("SELECT COUNT(embedding),COUNT(*) FROM job_passages WHERE job_id=?", (job["id"],)).fetchone()
            return row[0], row[1]

    def release(self, job, elapsed_ms=0, retry_at=0):
        with self.lock, self.db:
            self.db.execute(
                "UPDATE index_jobs SET state='pending',scheduled=?,elapsed_ms=elapsed_ms+?,retry_at=? WHERE id=? AND revision=?",
                (time.time(), elapsed_ms, retry_at, job["id"], job["revision"]),
            )

    def complete(self, job):
        with self.lock:
            if not self.current(job): return False
            rows = self.db.execute("SELECT content,embedding FROM job_passages WHERE job_id=? ORDER BY ordinal", (job["id"],)).fetchall()
            if not rows or any(row["embedding"] is None for row in rows):
                raise RuntimeError("Cannot publish an incomplete file.")
            owners = self.owners(job)
            if not owners: return False
            warning = self.db.execute("SELECT warning FROM index_jobs WHERE id=?", (job["id"],)).fetchone()[0]
            return self.store.replace_file(
                owners[0], Path(job["path"]), (job["mtime_ns"], job["size"], job["file_identity"]), job["kind"],
                [(" ".join(row["content"].split())[:400], np.frombuffer(row["embedding"], dtype=np.float32)) for row in rows],
                warning=warning, content_hash=job["content_hash"], job_ref=(job["id"], job["revision"]),
            )

    def fail(self, job, message, code, retryable, elapsed_ms=0):
        with self.lock:
            if not self.current(job): return False
            owners = self.owners(job)
            if not owners: return False
            attempts = job["attempts"] + 1
            retry_at = time.time() + min(3600, 300 * 2 ** min(attempts - 1, 4)) if retryable else None
            result = self.store.replace_file(
                owners[0], Path(job["path"]), (job["mtime_ns"], job["size"], job["file_identity"]), job["kind"], [],
                error=message, error_code=code, retry_after=retry_at, failure_count=attempts,
                content_hash=job["content_hash"], job_ref=(job["id"], job["revision"]), complete_job=not retryable,
            )
            if result and retryable:
                with self.db:
                    self.db.execute("UPDATE index_jobs SET state='pending',attempts=?,retry_at=?,scheduled=?,elapsed_ms=? WHERE id=? AND revision=?",
                                    (attempts, retry_at, time.time(), elapsed_ms, job["id"], job["revision"]))
            return result

    def prune(self, folder_id, scope, seen):
        with self.lock:
            for row in self.db.execute(
                "SELECT j.id,j.path,j.path_key FROM index_jobs j JOIN job_folders f ON f.job_id=j.id WHERE f.folder_id=?",
                (folder_id,),
            ).fetchall():
                if is_within(row["path"], scope) and row["path_key"] not in seen and not Path(row["path"]).exists():
                    with self.db:
                        self.db.execute("DELETE FROM job_folders WHERE job_id=? AND folder_id=?", (row["id"], folder_id))
            with self.db:
                self.db.execute("DELETE FROM index_jobs WHERE NOT EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id)")
            for row in self.db.execute(
                "SELECT f.path,f.path_key FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=?",
                (folder_id,),
            ).fetchall():
                if is_within(row["path"], scope) and row["path_key"] not in seen and not Path(row["path"]).exists():
                    self.store.remove_path(Path(row["path"]))

    def counts(self):
        with self.lock:
            return dict(self.db.execute(
                "SELECT state,COUNT(*) FROM index_jobs GROUP BY state"
            ).fetchall())

    def folder_counts(self, folder_id):
        with self.lock:
            succeeded = self.db.execute(
                "SELECT COUNT(*) FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=? AND f.error IS NULL "
                "AND NOT EXISTS(SELECT 1 FROM index_jobs j WHERE j.path_key=f.path_key)", (folder_id,),
            ).fetchone()[0]
            failed = self.db.execute(
                "SELECT COUNT(*) FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=? AND f.error IS NOT NULL",
                (folder_id,),
            ).fetchone()[0]
            queued, active = self.db.execute(
                "SELECT COUNT(*),COALESCE(SUM(j.state='active'),0) FROM index_jobs j JOIN job_folders f ON f.job_id=j.id WHERE f.folder_id=?",
                (folder_id,),
            ).fetchone()
            total = self.db.execute(
                "SELECT COUNT(*) FROM (SELECT f.path_key FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=? "
                "UNION SELECT j.path_key FROM index_jobs j JOIN job_folders jf ON jf.job_id=j.id WHERE jf.folder_id=?)",
                (folder_id, folder_id),
            ).fetchone()[0]
            return succeeded, failed, queued, active, total
