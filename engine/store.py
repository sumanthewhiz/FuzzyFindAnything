import hashlib
from datetime import datetime, timezone
from importlib.metadata import version as package_version
import json
import logging
import math
import os
import re
import shutil
import sqlite3
import struct
import threading
import uuid
from pathlib import Path

import numpy as np

import diskannpy

from vectors import DIMENSIONS, MODEL_FINGERPRINT, normalized
from paths import is_within, path_key

log = logging.getLogger(__name__)


class VectorStore:
    """SQLite commits are the recovery journal for the real, checkpointed DiskANN graph."""

    def __init__(self, directory: Path):
        directory.mkdir(parents=True, exist_ok=True)
        self.directory = directory
        self.index_directory = directory / "diskann"
        self.index_directory.mkdir(exist_ok=True)
        self.lock = threading.RLock()
        self.diskann_version = package_version("diskannpy")
        self.db = sqlite3.connect(directory / "catalog.sqlite3", check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.execute("PRAGMA foreign_keys=ON")
        self.db.executescript(
            """
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS folders(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE,
                status TEXT NOT NULL DEFAULT 'In Progress',
                detail TEXT NOT NULL DEFAULT 'Waiting to index',
                completed INTEGER NOT NULL DEFAULT 0,
                total INTEGER NOT NULL DEFAULT 0,
                errors INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE,
                mtime_ns INTEGER NOT NULL, size INTEGER NOT NULL,
                kind TEXT NOT NULL, error TEXT,
                file_identity TEXT NOT NULL DEFAULT '',
                error_code TEXT, retry_after REAL, failure_count INTEGER NOT NULL DEFAULT 0,
                warning TEXT,
                content_hash TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS folder_files(
                folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                PRIMARY KEY(folder_id, file_id)
            );
            CREATE INDEX IF NOT EXISTS folder_files_by_file ON folder_files(file_id);
            CREATE TABLE IF NOT EXISTS vectors(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL, snippet TEXT NOT NULL, embedding BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS vectors_by_file ON vectors(file_id);
            CREATE TABLE IF NOT EXISTS index_jobs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE,
                mtime_ns INTEGER NOT NULL, size INTEGER NOT NULL, file_identity TEXT NOT NULL,
                content_hash TEXT NOT NULL, kind TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 1, state TEXT NOT NULL DEFAULT 'pending',
                prepared INTEGER NOT NULL DEFAULT 0, warning TEXT,
                attempts INTEGER NOT NULL DEFAULT 0, retry_at REAL NOT NULL DEFAULT 0,
                scheduled REAL NOT NULL, elapsed_ms REAL NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS job_folders(
                job_id INTEGER NOT NULL REFERENCES index_jobs(id) ON DELETE CASCADE,
                folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                PRIMARY KEY(job_id,folder_id)
            );
            CREATE TABLE IF NOT EXISTS job_passages(
                job_id INTEGER NOT NULL REFERENCES index_jobs(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL, content TEXT NOT NULL, embedding BLOB,
                PRIMARY KEY(job_id,ordinal)
            );
            INSERT OR IGNORE INTO settings VALUES('generation', '0');
            INSERT OR IGNORE INTO settings VALUES('schema_version', '1');
            """
        )
        version = self.db.execute("SELECT value FROM settings WHERE key='schema_version'").fetchone()[0]
        if version not in ("1", "2", "3", "4"):
            raise RuntimeError(f"Unsupported catalog schema {version}; use a compatible application version.")
        model = self.db.execute("SELECT value FROM settings WHERE key='model'").fetchone()
        if model and model[0] != MODEL_FINGERPRINT:
            raise RuntimeError("The index uses a different embedding model. Do not mix vector spaces.")
        with self.db:
            if version == "1":
                if "file_identity" not in {row["name"] for row in self.db.execute("PRAGMA table_info(files)")}:
                    self.db.execute("ALTER TABLE files ADD COLUMN file_identity TEXT NOT NULL DEFAULT ''")
                self.db.execute("UPDATE settings SET value='2' WHERE key='schema_version'")
            if version in ("1", "2"):
                columns = {row["name"] for row in self.db.execute("PRAGMA table_info(files)")}
                for name, definition in (
                    ("error_code", "TEXT"), ("retry_after", "REAL"),
                    ("failure_count", "INTEGER NOT NULL DEFAULT 0"), ("warning", "TEXT"),
                ):
                    if name not in columns:
                        self.db.execute(f"ALTER TABLE files ADD COLUMN {name} {definition}")
                self.db.execute("UPDATE files SET retry_after=0 WHERE error IS NOT NULL")
                self.db.execute("UPDATE settings SET value='3' WHERE key='schema_version'")
            if version != "4":
                if "content_hash" not in {row["name"] for row in self.db.execute("PRAGMA table_info(files)")}:
                    self.db.execute("ALTER TABLE files ADD COLUMN content_hash TEXT NOT NULL DEFAULT ''")
                self.db.execute("UPDATE settings SET value='4' WHERE key='schema_version'")
            self.db.execute("INSERT OR IGNORE INTO settings VALUES('model', ?)", (MODEL_FINGERPRINT,))
            self.db.execute("UPDATE folders SET status='In Progress', detail='Checking for changes'")
        self.index = None
        self.capacity = 0
        self.allocated = 0
        self.checkpoint_generation = -1
        self.recovery_message = ""
        self._restore()

    @property
    def generation(self) -> int:
        return int(self.db.execute("SELECT value FROM settings WHERE key='generation'").fetchone()[0])

    def _bump_generation(self):
        self.db.execute("UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='generation'")
        self.db.execute(
            "INSERT INTO settings VALUES('last_refreshed', ?) "
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            (datetime.now(timezone.utc).isoformat(timespec="milliseconds"),),
        )

    def get_setting(self, key: str) -> str | None:
        with self.lock:
            row = self.db.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
            return row[0] if row else None

    def set_setting(self, key: str, value: str):
        with self.lock, self.db:
            self.db.execute(
                "INSERT INTO settings VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, value)
            )

    def _parameters(self, count: int) -> dict:
        return {
            "max_vectors": max(128, 2 ** math.ceil(math.log2(max(1, count * 2)))),
            "complexity": 96,
            "graph_degree": 48,
            "num_threads": 2,
            "initial_search_complexity": 128,
            "search_threads": 1,
            "concurrent_consolidation": False,
        }

    def _new_index(self, count: int):
        parameters = self._parameters(count)
        self.capacity = parameters["max_vectors"]
        self.allocated = 0
        return diskannpy.DynamicMemoryIndex(
            distance_metric="l2", vector_dtype=np.float32, dimensions=DIMENSIONS, **parameters
        )

    def _rebuild(self):
        count = self.db.execute("SELECT COUNT(*) FROM vectors").fetchone()[0]
        self.index = self._new_index(count)
        rows = self.db.execute("SELECT id, embedding FROM vectors ORDER BY id")
        while batch := rows.fetchmany(256):
            values = np.stack([np.frombuffer(row["embedding"], dtype=np.float32) for row in batch])
            ids = np.asarray([row["id"] for row in batch], dtype=np.uint32)
            self.index.batch_insert(values, ids, num_threads=2)
            self.allocated += len(batch)

    @staticmethod
    def _hash(path: Path) -> str:
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()

    def _restore(self):
        manifest_path = self.index_directory / "current.json"
        if manifest_path.is_file():
            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                name = manifest["snapshot"]
                if not re.fullmatch(r"snapshot-[0-9a-f]{32}", name):
                    raise ValueError("Invalid DiskANN snapshot directory.")
                snapshot = self.index_directory / name
                if manifest["generation"] != self.generation:
                    raise ValueError("Replaying committed changes newer than the DiskANN snapshot.")
                if manifest["model"] != MODEL_FINGERPRINT:
                    raise ValueError("DiskANN snapshot model mismatch.")
                hashes = manifest["files"]
                count = self.db.execute("SELECT COUNT(*) FROM vectors").fetchone()[0]
                required = {"ann", "ann.data", "ann.tags", "ann_metadata.bin"}
                if count and not required.issubset(hashes):
                    raise ValueError("Incomplete DiskANN snapshot manifest.")
                for filename, digest in hashes.items():
                    if Path(filename).name != filename or self._hash(snapshot / filename) != digest:
                        raise ValueError("DiskANN snapshot checksum mismatch.")
                if count:
                    parameters = self._parameters(count)
                    self.index = diskannpy.DynamicMemoryIndex.from_file(
                        str(snapshot), distance_metric="l2", vector_dtype=np.float32,
                        dimensions=DIMENSIONS, **parameters
                    )
                    self.capacity = parameters["max_vectors"]
                    self.allocated = count
                else:
                    self.index = self._new_index(0)
                self.checkpoint_generation = self.generation
                return
            except (OSError, ValueError, KeyError, TypeError, RuntimeError) as exc:
                self.recovery_message = f"Recovered DiskANN from the durable vector journal: {exc}"
                log.warning(self.recovery_message, exc_info=True)
        self._rebuild()

    def _apply(self, deleted: list[int], inserted: list[tuple[int, np.ndarray]]):
        try:
            if self.allocated + len(inserted) > self.capacity:
                self._rebuild()
                return
            for identifier in deleted:
                self.index.mark_deleted(identifier)
            for identifier, vector in inserted:
                self.index.insert(vector, identifier)
                self.allocated += 1
        except RuntimeError:
            log.exception("DiskANN mutation failed; rebuilding from the committed recovery journal")
            self._rebuild()

    def checkpoint(self):
        with self.lock:
            generation = self.generation
            if generation == self.checkpoint_generation:
                return
            previous = None
            manifest_path = self.index_directory / "current.json"
            if manifest_path.is_file():
                try:
                    previous = json.loads(manifest_path.read_text(encoding="utf-8")).get("snapshot")
                except (OSError, ValueError):
                    log.warning("The previous snapshot manifest was unreadable; retaining its files.")
            snapshot = self.index_directory / f"snapshot-{uuid.uuid4().hex}"
            snapshot.mkdir()
            count = self.db.execute("SELECT COUNT(*) FROM vectors").fetchone()[0]
            if count:
                # 0.7.0's dynamic save can serialize sparse insertion slots as zeros.
                # Its official bulk builder produces packed, reloadable dynamic graphs.
                source = snapshot / "vectors.fbin"
                tags = []
                with source.open("wb") as stream:
                    stream.write(struct.pack("<II", count, DIMENSIONS))
                    for row in self.db.execute("SELECT id, embedding FROM vectors ORDER BY id"):
                        tags.append(row["id"])
                        stream.write(row["embedding"])
                diskannpy.build_memory_index(
                    str(source), "l2", str(snapshot), complexity=96, graph_degree=48,
                    num_threads=2, vector_dtype=np.float32,
                    tags=np.asarray(tags, dtype=np.uint32), use_pq_build=False,
                )
                source.unlink()
                parameters = self._parameters(count)
                self.index = diskannpy.DynamicMemoryIndex.from_file(
                    str(snapshot), distance_metric="l2", vector_dtype=np.float32,
                    dimensions=DIMENSIONS, **parameters
                )
                self.capacity = parameters["max_vectors"]
            else:
                self.index = self._new_index(0)
            self.allocated = count
            hashes = {}
            for path in snapshot.iterdir():
                with path.open("rb+") as stream:
                    os.fsync(stream.fileno())
                hashes[path.name] = self._hash(path)
            manifest = {
                "snapshot": snapshot.name,
                "generation": generation,
                "model": MODEL_FINGERPRINT,
                "files": hashes,
            }
            temporary = manifest_path.with_suffix(".tmp")
            with temporary.open("w", encoding="utf-8") as stream:
                json.dump(manifest, stream)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, manifest_path)
            self.checkpoint_generation = generation
            if previous and re.fullmatch(r"snapshot-[0-9a-f]{32}", previous):
                old_path = self.index_directory / previous
                if old_path != snapshot and old_path.is_dir():
                    try:
                        shutil.rmtree(old_path)
                    except OSError:
                        log.warning("Could not remove retired DiskANN snapshot %s", old_path, exc_info=True)

    def folders(self) -> list[dict]:
        with self.lock:
            return [dict(row) for row in self.db.execute("SELECT * FROM folders ORDER BY id")]

    def has_folder(self, folder_id: int) -> bool:
        with self.lock:
            return self.db.execute("SELECT 1 FROM folders WHERE id=?", (folder_id,)).fetchone() is not None

    def add_folder(self, path: Path) -> int:
        with self.lock, self.db:
            self.db.execute("INSERT OR IGNORE INTO folders(path, path_key) VALUES(?, ?)", (str(path), path_key(path)))
            return self.db.execute("SELECT id FROM folders WHERE path_key=?", (path_key(path),)).fetchone()[0]

    def _delete_orphans(self) -> list[int]:
        ids = [
            row[0] for row in self.db.execute(
                "SELECT v.id FROM vectors v WHERE NOT EXISTS "
                "(SELECT 1 FROM folder_files ff WHERE ff.file_id=v.file_id)"
            )
        ]
        self.db.execute("DELETE FROM files WHERE NOT EXISTS (SELECT 1 FROM folder_files WHERE file_id=files.id)")
        if ids:
            self._bump_generation()
        return ids

    def remove_folders(self, folder_ids: list[int]):
        with self.lock:
            with self.db:
                for identifier in folder_ids:
                    self.db.execute("DELETE FROM folders WHERE id=?", (identifier,))
                # A nested configured root must retain files previously discovered by its parent.
                remaining = list(self.db.execute("SELECT id, path FROM folders"))
                for file in self.db.execute("SELECT id, path FROM files").fetchall():
                    for folder in remaining:
                        if is_within(file["path"], folder["path"]):
                            self.db.execute("INSERT OR IGNORE INTO folder_files VALUES(?, ?)", (folder["id"], file["id"]))
                for job in self.db.execute("SELECT id,path FROM index_jobs").fetchall():
                    for folder in remaining:
                        if is_within(job["path"], folder["path"]):
                            self.db.execute("INSERT OR IGNORE INTO job_folders VALUES(?,?)", (job["id"], folder["id"]))
                self.db.execute("DELETE FROM index_jobs WHERE NOT EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id)")
                deleted = self._delete_orphans()
            self._apply(deleted, [])
            self.checkpoint()

    def set_status(self, folder_id: int, status: str, detail: str, completed: int = 0, total: int = 0, errors: int = 0):
        with self.lock, self.db:
            self.db.execute(
                "UPDATE folders SET status=?, detail=?, completed=?, total=?, errors=? WHERE id=?",
                (status, detail, completed, total, errors, folder_id),
            )

    def get_file(self, path: Path) -> dict | None:
        with self.lock:
            row = self.db.execute("SELECT * FROM files WHERE path_key=?", (path_key(path),)).fetchone()
            return dict(row) if row else None

    def attach_file(self, folder_id: int, file_id: int):
        with self.lock, self.db:
            if self.has_folder(folder_id):
                self.db.execute("INSERT OR IGNORE INTO folder_files VALUES(?, ?)", (folder_id, file_id))

    def replace_file(
        self, folder_id: int, path: Path, signature: tuple[int, int, str], kind: str,
        chunks: list[tuple[str, np.ndarray]], error: str | None = None,
        *, error_code: str | None = None, retry_after: float | None = None,
        failure_count: int = 0, warning: str | None = None,
        content_hash: str = "", job_ref: tuple[int, int] | None = None,
        complete_job: bool = True,
    ) -> bool:
        prepared = [(text, normalized(vector)) for text, vector in chunks]
        with self.lock:
            if not self.has_folder(folder_id):
                return False
            owners = [folder_id]
            if job_ref:
                current = self.db.execute("SELECT 1 FROM index_jobs WHERE id=? AND revision=?", job_ref).fetchone()
                owners = [row[0] for row in self.db.execute("SELECT folder_id FROM job_folders WHERE job_id=?", (job_ref[0],))]
                if not current or not owners:
                    return False
            inserted = []
            with self.db:
                self.db.execute(
                    "INSERT INTO files(path, path_key, mtime_ns, size, file_identity, kind, error, error_code, retry_after, failure_count, warning,content_hash) "
                    "VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,?) "
                    "ON CONFLICT(path_key) DO UPDATE SET path=excluded.path, mtime_ns=excluded.mtime_ns, "
                    "size=excluded.size, file_identity=excluded.file_identity, kind=excluded.kind, error=excluded.error, "
                    "error_code=excluded.error_code, retry_after=excluded.retry_after, "
                    "failure_count=excluded.failure_count, warning=excluded.warning,content_hash=excluded.content_hash",
                    (str(path), path_key(path), *signature, kind, error, error_code, retry_after, failure_count, warning,content_hash),
                )
                file_id = self.db.execute("SELECT id FROM files WHERE path_key=?", (path_key(path),)).fetchone()[0]
                for owner in owners:
                    self.db.execute("INSERT OR IGNORE INTO folder_files VALUES(?, ?)", (owner, file_id))
                deleted = [row[0] for row in self.db.execute("SELECT id FROM vectors WHERE file_id=?", (file_id,))]
                self.db.execute("DELETE FROM vectors WHERE file_id=?", (file_id,))
                for ordinal, (text, vector) in enumerate(prepared):
                    cursor = self.db.execute(
                        "INSERT INTO vectors(file_id, ordinal, snippet, embedding) VALUES(?, ?, ?, ?)",
                        (file_id, ordinal, text, vector.tobytes()),
                    )
                    identifier = cursor.lastrowid
                    if identifier > np.iinfo(np.uint32).max:
                        raise OverflowError("The DiskANN vector identifier space is exhausted.")
                    inserted.append((identifier, vector))
                if deleted or inserted:
                    self._bump_generation()
                if job_ref and complete_job:
                    self.db.execute("DELETE FROM index_jobs WHERE id=? AND revision=?", job_ref)
            self._apply(deleted, inserted)
            return True

    def prune_folder(self, folder_id: int, seen: set[str]):
        with self.lock:
            with self.db:
                attached = list(self.db.execute(
                    "SELECT f.id, f.path_key FROM files f JOIN folder_files ff ON f.id=ff.file_id WHERE ff.folder_id=?",
                    (folder_id,),
                ))
                for file in attached:
                    if file["path_key"] not in seen:
                        self.db.execute("DELETE FROM folder_files WHERE folder_id=? AND file_id=?", (folder_id, file["id"]))
                deleted = self._delete_orphans()
            self._apply(deleted, [])

    def counts(self) -> tuple[int, int]:
        with self.lock:
            return (
                self.db.execute("SELECT COUNT(*) FROM files WHERE error IS NULL").fetchone()[0],
                self.db.execute("SELECT COUNT(*) FROM vectors").fetchone()[0],
            )

    def metadata(self) -> dict:
        with self.lock:
            documents, images, code_files = self.db.execute(
                "SELECT COALESCE(SUM(kind='Document'),0), COALESCE(SUM(kind='Image'),0), COALESCE(SUM(kind='Code'),0) "
                "FROM files WHERE error IS NULL AND EXISTS(SELECT 1 FROM vectors WHERE file_id=files.id)"
            ).fetchone()
            paths = [
                self.directory / name
                for name in ("catalog.sqlite3", "catalog.sqlite3-wal", "catalog.sqlite3-shm")
            ]
            paths.extend(path for path in self.index_directory.rglob("*") if path.is_file())
            size = 0
            for path in paths:
                try:
                    size += path.stat().st_size
                except FileNotFoundError:
                    continue
            return {
                "type_version": f"DiskANN ({self.diskann_version})",
                "database_location": str(self.directory.resolve()),
                "total_files": documents + images + code_files,
                "total_documents": documents,
                "total_code_files": code_files,
                "total_images": images,
                "database_size_bytes": size,
                "last_refreshed": self.get_setting("last_refreshed"),
            }

    def search(self, vector: np.ndarray, limit: int, threshold: float) -> list[dict]:
        vector = normalized(vector)
        if not 1 <= limit <= 100 or not 0 <= threshold <= 1:
            raise ValueError("Search requires a limit from 1 to 100 and a similarity from 0 to 1.")
        with self.lock:
            count = self.db.execute("SELECT COUNT(*) FROM vectors").fetchone()[0]
            if not count:
                return []
            k = min(count, max(64, limit * 4))
            while True:
                matches = {}
                response = self.index.search(vector, k_neighbors=k, complexity=max(128, k))
                for identifier in response.identifiers:
                    row = self.db.execute(
                        "SELECT f.id, f.path, f.kind, f.size, f.mtime_ns, v.snippet, v.embedding FROM vectors v "
                        "JOIN files f ON f.id=v.file_id WHERE v.id=? AND f.error IS NULL "
                        "AND EXISTS(SELECT 1 FROM folder_files ff JOIN folders r ON r.id=ff.folder_id WHERE ff.file_id=f.id)",
                        (int(identifier),),
                    ).fetchone()
                    if not row or not Path(row["path"]).is_file():
                        continue
                    score = float(np.clip(np.dot(vector, np.frombuffer(row["embedding"], dtype=np.float32)), -1, 1))
                    if score < threshold or (row["id"] in matches and matches[row["id"]]["score"] >= score):
                        continue
                    matches[row["id"]] = {
                        "path": row["path"], "name": Path(row["path"]).name, "kind": row["kind"],
                        "size": row["size"], "mtime_ns": row["mtime_ns"], "snippet": row["snippet"], "score": score,
                    }
                if len(matches) >= limit or k == count:
                    return sorted(matches.values(), key=lambda item: (-item["score"], item["path"]))[:limit]
                k = min(count, k * 2)

    def close(self):
        with self.lock:
            self.checkpoint()
            self.db.close()

    def remove_path(self, path: Path, recursive: bool = False):
        with self.lock:
            with self.db:
                keys = [row for row in self.db.execute("SELECT id,path,path_key FROM files")
                        if row["path_key"] == path_key(path) or (recursive and is_within(row["path"], path))]
                deleted = []
                for row in keys:
                    deleted.extend(item[0] for item in self.db.execute("SELECT id FROM vectors WHERE file_id=?", (row["id"],)))
                    self.db.execute("DELETE FROM files WHERE id=?", (row["id"],))
                for row in self.db.execute("SELECT id,path,path_key FROM index_jobs").fetchall():
                    if row["path_key"] == path_key(path) or (recursive and is_within(row["path"], path)):
                        self.db.execute("DELETE FROM index_jobs WHERE id=?", (row["id"],))
                if deleted: self._bump_generation()
            self._apply(deleted, [])
