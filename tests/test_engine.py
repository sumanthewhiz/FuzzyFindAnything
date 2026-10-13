import codecs
import os
from pathlib import Path
import struct
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))

import numpy as np

from vectors import DIMENSIONS
from extraction import read_text
from indexer import DirtyFolder, IndexingEngine
from paths import file_kind, fingerprint, is_within, path_key
from store import VectorStore
from windows_changes import parse_directory_records, parse_usn_records


def vector(axis: int = 0) -> np.ndarray:
    result = np.zeros(DIMENSIONS, dtype=np.float32)
    result[axis] = 1
    return result


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ffa-unit-")
        self.root = Path(self.temporary.name)
        self.corpus = self.root / "files"
        self.corpus.mkdir()
        self.store = VectorStore(self.root / "index")
        self.folder = self.store.add_folder(self.corpus)

    def tearDown(self):
        self.store.close()
        self.temporary.cleanup()

    def add(self, name="document.txt", axis=0, folder=None, chunks=None):
        path = self.corpus / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("content", encoding="utf-8")
        self.store.replace_file(
            folder or self.folder, path, fingerprint(path), file_kind(path),
            chunks if chunks is not None else [("content", vector(axis))],
        )
        return path

    def test_real_diskann_persists_and_reloads_with_metadata(self):
        path = self.add()
        self.store.close()
        self.store = VectorStore(self.root / "index")
        results = self.store.search(vector(), 50, 0.5)
        self.assertEqual([str(path)], [result["path"] for result in results])
        self.assertAlmostEqual(1, results[0]["score"], places=5)
        self.assertEqual("", self.store.recovery_message)
        self.assertEqual(1, len(list((self.root / "index" / "diskann").glob("snapshot-*"))))

    def test_update_replaces_old_vectors_and_deduplicates_passages(self):
        path = self.add(chunks=[("old", vector()), ("old duplicate", vector())])
        self.assertEqual(1, len(self.store.search(vector(), 50, 0.5)))
        self.store.replace_file(self.folder, path, fingerprint(path), "Document", [("new", vector(1))])
        self.assertEqual([], self.store.search(vector(), 50, 0.5))
        self.assertEqual("new", self.store.search(vector(1), 50, 0.5)[0]["snippet"])

    def test_parent_removal_retains_nested_configured_folder(self):
        child = self.corpus / "child"
        child.mkdir()
        nested_id = self.store.add_folder(child)
        kept = self.add("child\\kept.txt")
        self.add("removed.txt", axis=1)
        self.store.remove_folders([self.folder])
        self.assertEqual((1, 1), self.store.counts())
        self.assertEqual(str(kept), self.store.search(vector(), 50, 0.5)[0]["path"])
        self.store.remove_folders([nested_id])
        self.assertEqual((0, 0), self.store.counts())
        self.assertEqual([], self.store.search(vector(), 50, 0))
        self.assertTrue(kept.is_file())

    def test_removed_folder_cannot_commit_inflight_embedding(self):
        path = self.corpus / "late.txt"
        path.write_text("late", encoding="utf-8")
        self.store.remove_folders([self.folder])
        committed = self.store.replace_file(self.folder, path, fingerprint(path), "Document", [("late", vector())])
        self.assertFalse(committed)
        self.assertEqual((0, 0), self.store.counts())

    def test_uncheckpointed_commit_is_recovered_without_reembedding(self):
        self.add("first.txt")
        self.store.checkpoint()
        second = self.add("second.txt", axis=1)
        self.store.db.close()
        self.store = VectorStore(self.root / "index")
        self.assertIn("Recovered DiskANN", self.store.recovery_message)
        self.assertEqual(str(second), self.store.search(vector(1), 50, 0.5)[0]["path"])

    def test_damaged_graph_is_rebuilt_before_native_load(self):
        self.add()
        self.store.close()
        graph = next((self.root / "index" / "diskann").glob("snapshot-*\\ann"))
        graph.write_bytes(b"corrupt")
        self.store = VectorStore(self.root / "index")
        self.assertIn("checksum", self.store.recovery_message)
        self.assertEqual(1, len(self.store.search(vector(), 50, 0.5)))

    def test_failed_reindex_does_not_return_stale_content(self):
        path = self.add()
        self.store.replace_file(self.folder, path, fingerprint(path), "Document", [], error="IFilter unavailable")
        self.assertEqual([], self.store.search(vector(), 50, 0))
        self.assertEqual((0, 0), self.store.counts())

    def test_deleted_files_and_low_similarity_return_no_matches(self):
        path = self.add()
        self.assertEqual([], self.store.search(vector(1), 50, 0.3))
        path.unlink()
        self.assertEqual([], self.store.search(vector(), 50, 0))
        self.store.prune_folder(self.folder, set())
        self.assertEqual((0, 0), self.store.counts())

    def test_capacity_grows_and_small_indexes_do_not_request_too_many_neighbors(self):
        self.add(chunks=[(str(index), vector(index % DIMENSIONS)) for index in range(140)])
        self.assertGreaterEqual(self.store.capacity, 140)
        results = self.store.search(vector(139), 50, 0.9)
        self.assertEqual(1, len(results))
        self.assertEqual("139", results[0]["snippet"])

    def test_empty_index_checkpoint_reload_then_insert(self):
        self.store.close()
        self.store = VectorStore(self.root / "index")
        self.assertEqual("", self.store.recovery_message)
        self.add()
        self.assertEqual(1, len(self.store.search(vector(), 50, 0)))

    def test_invalid_vectors_are_not_committed(self):
        path = self.corpus / "bad.txt"
        path.write_text("bad", encoding="utf-8")
        with self.assertRaises(ValueError):
            self.store.replace_file(
                self.folder, path, fingerprint(path), "Document", [("bad", np.full(DIMENSIONS, np.nan))]
            )
        self.assertEqual((0, 0), self.store.counts())

    def test_metadata_updates_for_document_image_error_and_removal(self):
        initial = self.store.metadata()
        self.assertIsNone(initial["last_refreshed"])
        self.assertEqual(0, initial["total_files"])
        self.add("document.txt")
        image = self.add("picture.png", axis=1)
        information = self.store.metadata()
        self.assertEqual((2, 1, 1), (information["total_files"], information["total_documents"], information["total_images"]))
        self.assertEqual(str((self.root / "index").resolve()), information["database_location"])
        self.assertGreater(information["database_size_bytes"], 0)
        self.assertIsNotNone(information["last_refreshed"])
        self.store.replace_file(self.folder, image, fingerprint(image), "Image", [], error="Unreadable image")
        information = self.store.metadata()
        self.assertEqual((1, 1, 0), (information["total_files"], information["total_documents"], information["total_images"]))
        self.store.remove_folders([self.folder])
        information = self.store.metadata()
        self.assertEqual(0, information["total_files"])
        self.assertGreaterEqual(information["last_refreshed"], initial["last_refreshed"] or "")

    def test_metadata_refresh_timestamp_survives_restart(self):
        self.add()
        timestamp = self.store.metadata()["last_refreshed"]
        self.store.close()
        self.store = VectorStore(self.root / "index")
        self.assertEqual(timestamp, self.store.metadata()["last_refreshed"])
        self.assertEqual(1, self.store.metadata()["total_documents"])

    def test_offline_reset_preserves_folders_and_removes_all_generated_index_data(self):
        sys.path.insert(0, str(ROOT / "tools"))
        from reset_index import reset
        self.add()
        self.store.set_setting("usn:C:", '{"cursor": 100, "id": 1}')
        folders = [(item["id"], item["path"]) for item in self.store.folders()]
        self.store.close()
        (self.root / "index" / "engine.lock").write_bytes(b"\0")
        reset(self.root / "index")
        self.store = VectorStore(self.root / "index")
        self.assertEqual(folders, [(item["id"], item["path"]) for item in self.store.folders()])
        self.assertEqual((0, 0), self.store.counts())
        self.assertIsNone(self.store.get_setting("usn:C:"))
        self.assertIsNone(self.store.metadata()["last_refreshed"])
        self.assertTrue((self.corpus / "document.txt").exists())
        self.assertTrue(list((self.root / "index").glob("configured-folders-*.json")))

    def test_explicit_reset_clears_folders_and_staged_jobs_but_preserves_preferences(self):
        sys.path.insert(0, str(ROOT / "tools"))
        from reset_index import reset
        from work_queue import WorkQueue
        self.add()
        pending = self.corpus / "pending.txt"
        pending.write_text("apple", encoding="utf-8")
        queue = WorkQueue(self.store)
        queue.enqueue(self.folder, pending, fingerprint(pending), "Document")
        job = queue.claim()
        queue.prepare(job, ["apple", "more"])
        queue.save_batch(job, [{"ordinal": 0}], [vector()])
        settings = self.root / "index" / "settings.xml"
        settings.write_text('<preferences unchanged="true"/>', encoding="utf-8")
        original = settings.read_bytes()
        self.store.set_setting("usn:C:", '{"cursor":1,"id":1}')
        self.store.close()
        (self.root / "index" / "engine.lock").write_bytes(b"\0")
        reset(self.root / "index", clear_folders=True)
        self.store = VectorStore(self.root / "index")
        self.assertEqual([], self.store.folders())
        for table in ("files", "vectors", "folder_files", "index_jobs", "job_passages", "job_folders"):
            self.assertEqual(0, self.store.db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0], table)
        self.assertIsNone(self.store.get_setting("usn:C:"))
        self.assertEqual(original, settings.read_bytes())
        self.assertTrue(pending.exists())


class FakeModel:
    def __init__(self, _):
        pass

    def document(self, title, text, cancelled, progress=None):
        if progress:
            progress(1, 1)
        return [(text, vector(0 if "apple" in text else 1))]

    def query(self, text):
        return vector(0 if "apple" in text else 1)

    def split(self, text):
        if not text.strip(): raise ValueError("No text")
        return [text]

    def embed_batch(self, title, passages):
        return [vector(0 if "apple" in text else 1) for text in passages]


class CrawlerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ffa-crawler-")
        self.root = Path(self.temporary.name)
        self.corpus = self.root / "files"
        self.corpus.mkdir()
        self.engine = IndexingEngine(
            self.root / "index", self.root / "model", self.root / "extractor.exe",
            self.root / "application", model_factory=FakeModel, watch_changes=False,
        )
        self.engine.model = FakeModel(None)
        self.engine.model_state = "Ready"
        self.folder = self.engine.add_folder(str(self.corpus))

    def tearDown(self):
        self.engine.stop()
        self.temporary.cleanup()

    def reconcile(self, identifier, request):
        self.engine.pending.pop(identifier, None)
        self.engine.reconcile(identifier, request)
        while job := self.engine.queue.claim():
            self.engine.process_job(job)

    def test_recursive_index_and_renaming_to_unsupported_type(self):
        nested = self.corpus / "one" / "two"
        nested.mkdir(parents=True)
        path = nested / "notes.TXT"
        path.write_text("apple", encoding="utf-8")
        (nested / "ignored.exe").write_bytes(b"not a supported document")
        self.reconcile(self.folder, DirtyFolder(0))
        self.assertEqual("Indexed", self.engine.status()["folders"][0]["status"])
        self.assertEqual((1, 1), self.engine.store.counts())
        path.rename(nested / "notes.bin")
        self.reconcile(self.folder, DirtyFolder(0))
        self.assertEqual((0, 0), self.engine.store.counts())

    def test_usn_dirty_path_reindexes_even_if_mtime_and_size_are_preserved(self):
        path = self.corpus / "mutable.txt"
        path.write_text("apple", encoding="utf-8")
        self.reconcile(self.folder, DirtyFolder(0))
        stamp = path.stat()
        path.write_text("boats", encoding="utf-8")
        os.utime(path, ns=(stamp.st_atime_ns, stamp.st_mtime_ns))
        self.reconcile(self.folder, DirtyFolder(0, paths={path_key(path)}))
        self.assertEqual([], self.engine.search("apple"))
        self.assertEqual(1, len(self.engine.search("boats")))

    def test_parent_directory_notifications_do_not_reembed_unchanged_children(self):
        path = self.corpus / "stable.txt"
        path.write_text("apple", encoding="utf-8")
        self.reconcile(self.folder, DirtyFolder(0))
        original_generation = self.engine.store.generation
        self.reconcile(self.folder, DirtyFolder(0, paths={path_key(self.corpus)}))
        self.assertEqual(original_generation, self.engine.store.generation)

    def test_replaced_file_identity_invalidates_preserved_metadata(self):
        path = self.corpus / "replaced.txt"
        path.write_text("apple", encoding="utf-8")
        self.reconcile(self.folder, DirtyFolder(0))
        stamp = path.stat()
        replacement = self.corpus / "replacement.tmp"
        replacement.write_text("boats", encoding="utf-8")
        os.utime(replacement, ns=(stamp.st_atime_ns, stamp.st_mtime_ns))
        os.replace(replacement, path)
        self.reconcile(self.folder, DirtyFolder(0, paths={path_key(self.corpus)}))
        self.assertEqual([], self.engine.search("apple"))
        self.assertEqual(1, len(self.engine.search("boats")))

    def test_unreadable_subtree_does_not_delete_previous_index(self):
        path = self.corpus / "safe.txt"
        path.write_text("apple", encoding="utf-8")
        self.reconcile(self.folder, DirtyFolder(0))
        with patch("indexer.os.scandir", side_effect=PermissionError("test access denied")):
            self.reconcile(self.folder, DirtyFolder(0))
        self.assertEqual((1, 1), self.engine.store.counts())
        self.assertEqual("In Progress", self.engine.status()["folders"][0]["status"])
        self.assertIn("test access denied", self.engine.store.get_setting(f"scan_error:{self.folder}"))

    def test_application_storage_is_never_self_indexed(self):
        with self.assertRaises(ValueError):
            self.engine.add_folder(str(self.root / "index"))
        self.reconcile(self.folder, DirtyFolder(0))
        self.assertEqual("Indexed", self.engine.status()["folders"][0]["status"])

    def test_permanent_content_errors_wait_for_changes_instead_of_retrying(self):
        path = self.corpus / "empty.txt"
        path.write_bytes(b"")
        self.reconcile(self.folder, DirtyFolder(0))
        failed = self.engine.store.get_file(path)
        self.assertEqual("empty_file", failed["error_code"])
        self.assertIsNone(failed["retry_after"])
        with patch.object(self.engine.extractor, "extract_details", side_effect=AssertionError("Unexpected retry")):
            self.reconcile(self.folder, DirtyFolder(0))
        self.assertEqual(1, self.engine.store.get_file(path)["failure_count"])
        path.write_text("apple", encoding="utf-8")
        self.reconcile(self.folder, DirtyFolder(0))
        self.assertIsNone(self.engine.store.get_file(path)["error"])
        self.assertEqual((1, 1), self.engine.store.counts())

    def test_active_job_and_saved_passages_resume_after_unclean_exit(self):
        path = self.corpus / "large.txt"
        passages = [f"apple passage {index}" for index in range(12)]
        path.write_text("\n".join(passages), encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0))
        job = self.engine.queue.claim()
        self.engine.queue.prepare(job, passages)
        self.engine.queue.save_batch(job, self.engine.queue.remaining(job), [vector()] * 4)
        self.engine.store.db.close()
        self.engine = IndexingEngine(
            self.root / "index", self.root / "model", self.root / "extractor.exe",
            self.root / "application", model_factory=FakeModel, watch_changes=False,
        )
        self.engine.model = FakeModel(None)
        self.engine.model_state = "Ready"
        self.assertEqual({"pending": 1}, self.engine.queue.counts())
        with patch.object(self.engine.model, "embed_batch", wraps=self.engine.model.embed_batch) as embed:
            self.engine.process_job(self.engine.queue.claim())
        resumed = [text for call in embed.call_args_list for text in call.args[1]]
        self.assertEqual(passages[4:], resumed)
        self.assertEqual((1, 12), self.engine.store.counts())
        self.assertEqual({}, self.engine.queue.counts())
        self.assertEqual(0, self.engine.store.db.execute("SELECT COUNT(*) FROM job_passages").fetchone()[0])

    def test_directory_deltas_preserve_active_job_revision_and_passages(self):
        path = self.corpus / "large.txt"
        path.write_text("apple", encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0))
        job = self.engine.queue.claim()
        self.engine.queue.prepare(job, ["apple"] * 8)
        self.engine.queue.save_batch(job, self.engine.queue.remaining(job), [vector()] * 4)
        added = self.corpus / "added.txt"
        added.write_text("boats", encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0, paths={path_key(self.corpus)}))
        self.engine.reconcile(self.folder, DirtyFolder(0, paths={path_key(path)}))
        self.assertTrue(self.engine.queue.current(job))
        self.assertEqual((4, 8), self.engine.queue.progress(job))
        self.assertEqual({"active": 1, "pending": 1}, self.engine.queue.counts())
        added.unlink()
        self.engine.reconcile(self.folder, DirtyFolder(0, paths={path_key(added)}))
        self.assertEqual({"active": 1}, self.engine.queue.counts())
        self.assertEqual((4, 8), self.engine.queue.progress(job))

    def test_changed_content_rejects_stale_publication_even_with_preserved_metadata(self):
        path = self.corpus / "changing.txt"
        path.write_text("apple", encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0))
        old = self.engine.queue.claim()
        self.engine.queue.prepare(old, ["apple"])
        self.engine.queue.save_batch(old, self.engine.queue.remaining(old), [vector()])
        stamp = path.stat()
        path.write_text("boats", encoding="utf-8")
        os.utime(path, ns=(stamp.st_atime_ns, stamp.st_mtime_ns))
        self.engine.reconcile(self.folder, DirtyFolder(0, paths={path_key(path)}))
        self.assertFalse(self.engine.queue.complete(old))
        self.assertFalse(self.engine.queue.save_batch(old, [{"ordinal": 0}], [vector()]))
        self.engine.process_job(self.engine.queue.claim())
        self.assertEqual([], self.engine.search("apple"))
        self.assertEqual(1, len(self.engine.search("boats")))

    def test_large_job_yields_and_never_repeats_saved_passages(self):
        large = self.corpus / "large.txt"
        passages = [f"apple passage {index}" for index in range(20)]
        large.write_text("\n".join(passages), encoding="utf-8")
        small = self.corpus / "one-passage.txt"
        small.write_text("boats " * 100, encoding="utf-8")
        self.engine.model.split = lambda text: text.splitlines()
        self.engine.reconcile(self.folder, DirtyFolder(0))
        with patch.object(self.engine.model, "embed_batch", wraps=self.engine.model.embed_batch) as embed:
            first = self.engine.queue.claim()
            self.assertEqual(str(large), first["path"])
            self.engine.process_job(first)
            self.assertEqual((16, 20), self.engine.queue.progress(first))
            second = self.engine.queue.claim()
            self.assertEqual(str(small), second["path"])
            self.engine.process_job(second)
            self.engine.process_job(self.engine.queue.claim())
        actual = [text for call in embed.call_args_list if call.args[0] == large.name for text in call.args[1]]
        self.assertEqual(passages, actual)
        self.assertEqual((2, 21), self.engine.store.counts())

    def test_removing_parent_retains_overlapping_durable_work(self):
        child = self.corpus / "child"
        child.mkdir()
        child_id = self.engine.add_folder(str(child))
        path = child / "retained.txt"
        path.write_text("apple", encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0))
        job = self.engine.queue.claim()
        self.engine.queue.prepare(job, ["apple"])
        self.engine.queue.save_batch(job, self.engine.queue.remaining(job), [vector()])
        self.engine.remove_folders([self.folder])
        self.assertEqual([child_id], self.engine.queue.owners(job))
        self.assertTrue(self.engine.queue.complete(job))
        self.assertEqual((1, 1), self.engine.store.counts())
        self.engine.remove_folders([child_id])
        self.assertEqual((0, 0), self.engine.store.counts())
        self.assertTrue(path.exists())

    def test_missing_extractor_is_a_retryable_failure_not_a_deleted_source(self):
        path = self.corpus / "document.docx"
        path.write_bytes(b"content")
        self.reconcile(self.folder, DirtyFolder(0))
        failed = self.engine.store.get_file(path)
        self.assertEqual("missing_dependency", failed["error_code"])
        self.assertIsNotNone(failed["retry_after"])
        self.assertEqual({"pending": 1}, self.engine.queue.counts())
        self.assertTrue(path.exists())

    def test_reverting_to_committed_content_cancels_obsolete_work(self):
        path = self.corpus / "reverted.txt"
        path.write_text("apple", encoding="utf-8")
        self.reconcile(self.folder, DirtyFolder(0))
        generation = self.engine.store.generation
        path.write_text("boats", encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0, paths={path_key(path)}))
        obsolete = self.engine.queue.claim()
        path.write_text("apple", encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0, paths={path_key(path)}))
        self.assertFalse(self.engine.queue.current(obsolete))
        self.assertEqual({}, self.engine.queue.counts())
        self.assertEqual(generation, self.engine.store.generation)
        self.assertEqual(1, len(self.engine.search("apple")))

    def test_notification_during_startup_does_not_replace_initial_discovery(self):
        first = self.corpus / "first.txt"
        first.write_text("apple", encoding="utf-8")
        (self.corpus / "second.txt").write_text("boats", encoding="utf-8")
        self.engine.schedule(self.folder, str(first))
        request = self.engine.pending[self.folder]
        self.assertTrue(request.full_scan)
        self.reconcile(self.folder, request)
        self.assertEqual((2, 2), self.engine.store.counts())

    def test_delta_indexing_preserves_actual_filename_casing(self):
        path = self.corpus / "Quarterly Report.TXT"
        path.write_text("apple", encoding="utf-8")
        self.reconcile(self.folder, DirtyFolder(0, paths={path_key(path)}))
        self.assertEqual(str(path), self.engine.store.get_file(path)["path"])

    def test_user_exit_preserves_batches_without_recording_a_retry_failure(self):
        path = self.corpus / "interrupted.txt"
        path.write_text("apple", encoding="utf-8")
        self.engine.reconcile(self.folder, DirtyFolder(0))
        job = self.engine.queue.claim()
        self.engine.queue.prepare(job, [f"apple {index}" for index in range(16)])

        def interrupted_batch(title, passages):
            self.engine.stop_event.set()
            time.sleep(0.05)
            return [vector()] * len(passages)

        with patch.object(self.engine.model, "embed_batch", side_effect=interrupted_batch):
            self.engine.process_job(job)
        self.assertIsNone(self.engine.store.get_file(path))
        saved = self.engine.store.db.execute("SELECT state,retry_at FROM index_jobs WHERE id=?", (job["id"],)).fetchone()
        self.assertEqual(("pending", 0), tuple(saved))
        self.assertGreaterEqual(self.engine.queue.progress(job)[0], 4)
        self.engine.stop_event.clear()
        self.engine.process_job(self.engine.queue.claim())
        self.assertEqual((1, 16), self.engine.store.counts())


class ExtractionAndPathTests(unittest.TestCase):
    def test_text_boms_and_unicode_paths(self):
        with tempfile.TemporaryDirectory(prefix="ffa-text-") as root:
            path = Path(root) / "notes-\u03b1.txt"
            for encoding in ["utf-8-sig", "utf-16", "utf-32"]:
                with self.subTest(encoding=encoding):
                    path.write_bytes("R\u00e9sum\u00e9 and \u5c71".encode(encoding))
                    self.assertEqual("R\u00e9sum\u00e9 and \u5c71", read_text(path))

    def test_scope_boundaries_case_and_unc_paths(self):
        self.assertTrue(is_within(r"C:\Documents\a\item.txt", r"c:\documents"))
        self.assertFalse(is_within(r"C:\DocumentsOther\item.txt", r"C:\Documents"))
        self.assertTrue(is_within(r"\\server\share\folder\item.txt", r"\\server\share\folder"))
        self.assertFalse(is_within(r"D:\Documents\item.txt", r"C:\Documents"))

    def test_exact_requested_file_types(self):
        for suffix in [".txt", ".rtf", ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx",
                       ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico"]:
            self.assertIsNotNone(file_kind("item" + suffix.upper()))
        self.assertIsNone(file_kind("item.exe"))
        self.assertIsNone(file_kind("item.txt.exe"))


class NotificationTests(unittest.TestCase):
    @staticmethod
    def record(version, file_id, parent_id, name):
        encoded = name.encode("utf-16-le")
        base = 60 if version == 2 else 76
        length = (base + len(encoded) + 7) // 8 * 8
        data = bytearray(length)
        struct.pack_into("<IHH", data, 0, length, version, 0)
        if version == 2:
            struct.pack_into("<QQ", data, 8, file_id, parent_id)
            struct.pack_into("<HH", data, 56, len(encoded), base)
        else:
            data[8:24] = file_id.to_bytes(16, "little")
            data[24:40] = parent_id.to_bytes(16, "little")
            struct.pack_into("<HH", data, 72, len(encoded), base)
        data[base : base + len(encoded)] = encoded
        return data

    def test_v2_v3_and_64_bit_file_ids(self):
        a = self.record(2, 2**62 + 5, 2**60, "file-\u03b1.txt")
        b = self.record(3, 2**100, 2**99, "image.png")
        cursor, records, unknown = parse_usn_records(struct.pack("<q", 12345) + a + b)
        self.assertEqual(12345, cursor)
        self.assertEqual([(2**62 + 5, 2**60, "file-\u03b1.txt"), (2**100, 2**99, "image.png")], records)
        self.assertFalse(unknown)

    def test_unknown_version_requests_conservative_reconciliation(self):
        data = struct.pack("<qIHH", 10, 8, 4, 0)
        self.assertTrue(parse_usn_records(data)[2])

    def test_malformed_usn_buffers_are_rejected(self):
        for data in [b"", struct.pack("<qIHH", 10, 0, 2, 0), struct.pack("<qIHH", 10, 900, 2, 0)]:
            with self.subTest(data=data), self.assertRaises(ValueError):
                parse_usn_records(data)

    def test_directory_unicode_and_rename_records(self):
        first = "old.txt".encode("utf-16-le")
        second = "\u65b0.txt".encode("utf-16-le")
        offset = (12 + len(first) + 3) // 4 * 4
        data = struct.pack("<III", offset, 4, len(first)) + first
        data += bytes(offset - len(data))
        data += struct.pack("<III", 0, 5, len(second)) + second
        self.assertEqual(["old.txt", "\u65b0.txt"], parse_directory_records(data))
        with self.assertRaises(ValueError):
            parse_directory_records(b"short")


class LiveUsnTests(unittest.TestCase):
    def test_usn_alone_detects_create_preserved_timestamp_update_and_delete(self):
        with tempfile.TemporaryDirectory(prefix="ffa-usn-") as temporary:
            root = Path(temporary)
            corpus = root / "files"
            corpus.mkdir()
            # Disable directory callbacks to prove the USN journal itself drives these updates.
            with patch("windows_changes.DirectoryWatcher"):
                engine = IndexingEngine(
                    root / "index", root / "model", root / "extractor.exe", root / "app",
                    model_factory=FakeModel,
                )
                try:
                    identifier = engine.add_folder(str(corpus))
                    if not engine.monitor.journals:
                        self.skipTest(engine.monitor.description)
                    engine.start()

                    def wait_until(predicate):
                        deadline = time.monotonic() + 15
                        while time.monotonic() < deadline:
                            if predicate():
                                return
                            time.sleep(0.1)
                        self.fail(f"USN update did not arrive: {engine.status()}")

                    wait_until(lambda: engine.status()["folders"][0]["status"] == "Indexed")
                    path = corpus / "journal-only.txt"
                    path.write_text("apple", encoding="utf-8")
                    wait_until(lambda: engine.store.counts() == (1, 1))
                    wait_until(lambda: engine.status()["folders"][0]["status"] == "Indexed")
                    original = path.stat()
                    path.write_text("boats", encoding="utf-8")
                    os.utime(path, ns=(original.st_atime_ns, original.st_mtime_ns))
                    wait_until(lambda: bool(engine.store.search(vector(1), 1, 0.9)))
                    path.unlink()
                    wait_until(lambda: engine.store.counts() == (0, 0))
                    self.assertTrue(engine.store.has_folder(identifier))
                finally:
                    engine.stop()


if __name__ == "__main__":
    unittest.main(verbosity=2)
