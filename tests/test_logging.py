import json
import logging
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from indexer import DirtyFolder, IndexingEngine
from indexer_logging import IndexerLogging, load_logging_settings
from test_engine import FakeModel


class LoggingTests(unittest.TestCase):
    def test_default_xml_configuration_and_validation(self):
        with tempfile.TemporaryDirectory(prefix="ffa-log-settings-") as temporary:
            root = Path(temporary).resolve()
            self.assertEqual((True, root / "Logs"), load_logging_settings(root))
            target = root / "custom"
            config = root / "settings.xml"
            config.write_text(f'<FuzzyFindAnythingConfiguration version="1"><Appearance theme="Dark"/>'
                              f'<Logging enabled="false" directory="{target}"/></FuzzyFindAnythingConfiguration>', encoding="utf-8")
            self.assertEqual((False, target), load_logging_settings(root))
            for text in (
                '<FuzzyFindAnythingConfiguration version="2"/>',
                '<!DOCTYPE x [<!ENTITY value "true">]><FuzzyFindAnythingConfiguration version="1"/>',
                '<FuzzyFindAnythingConfiguration version="1"><Logging enabled="yes"/></FuzzyFindAnythingConfiguration>',
                '<FuzzyFindAnythingConfiguration version="1"><Logging directory="relative"/></FuzzyFindAnythingConfiguration>',
            ):
                config.write_text(text, encoding="utf-8")
                with self.subTest(text=text), self.assertRaises(ValueError):
                    load_logging_settings(root)

    def test_per_file_outcomes_disable_and_location_change(self):
        with tempfile.TemporaryDirectory(prefix="ffa-log-files-") as temporary:
            root = Path(temporary).resolve()
            corpus = root / "files"
            corpus.mkdir()
            good, bad = corpus / "notes.txt", corpus / "empty.txt"
            good.write_text("apple", encoding="utf-8")
            bad.write_bytes(b"")
            sink = IndexerLogging(True, root / "Logs")
            engine = IndexingEngine(root / "index", root / "model", root / "filter.exe", root / "app",
                                    model_factory=FakeModel, watch_changes=False, outcome_log=sink)
            engine.model = FakeModel(None)
            try:
                folder = engine.add_folder(str(corpus))
                engine.reconcile(folder, DirtyFolder(0))
                while job := engine.queue.claim(): engine.process_job(job)
                path = root / "Logs" / "indexer.jsonl"
                records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
                self.assertEqual({"Succeeded", "Failed"}, {record["outcome"] for record in records})
                failed = next(record for record in records if record["outcome"] == "Failed")
                self.assertEqual(str(bad), failed["path"])
                self.assertEqual("empty_file", failed["error"]["code"])
                self.assertIn("ExtractionError", failed["error"]["details"])
                self.assertGreaterEqual(failed["duration_ms"], 0)
                saved = path.read_bytes()
                sink.configure(False, root / "Logs")
                good.write_text("boats", encoding="utf-8")
                engine.reconcile(folder, DirtyFolder(0))
                while job := engine.queue.claim(): engine.process_job(job)
                self.assertEqual(saved, path.read_bytes())
                sink.configure(True, corpus)
                good.write_text("apple again", encoding="utf-8")
                engine.reconcile(folder, DirtyFolder(0))
                while job := engine.queue.claim(): engine.process_job(job)
                self.assertEqual(1, len((corpus / "indexer.jsonl").read_text(encoding="utf-8").splitlines()))
                self.assertFalse(engine.excluded(good))
                self.assertTrue(engine.excluded(corpus / "indexer.jsonl"))
                self.assertEqual(1, engine.store.counts()[0])
            finally:
                engine.stop()
                sink.close()

    def test_unwritable_target_keeps_active_destination_and_reports_runtime_failure(self):
        with tempfile.TemporaryDirectory(prefix="ffa-log-errors-") as temporary:
            root = Path(temporary).resolve()
            sink = IndexerLogging(True, root / "Logs")
            try:
                invalid = root / "not-a-directory"
                invalid.write_text("file", encoding="utf-8")
                with self.assertRaises(OSError): sink.configure(True, invalid)
                self.assertEqual(str(root / "Logs"), sink.status()["directory"])
                record = logging.LogRecord("test", logging.ERROR, "", 0, "failure", (), None)
                with patch.object(sink.engine_handler, "handle", side_effect=PermissionError("denied")):
                    sink.emit(record)
                self.assertIn("denied", sink.status()["error"])
                sink.configure(False, root / "Logs")
                self.assertEqual("", sink.status()["error"])
            finally: sink.close()

    def test_rotation_retains_bounded_history(self):
        with tempfile.TemporaryDirectory(prefix="ffa-log-rotation-") as temporary:
            root = Path(temporary).resolve()
            sink = IndexerLogging(True, root)
            sink.engine_handler.maxBytes = 140
            try:
                for index in range(12):
                    sink.emit(logging.LogRecord("test", logging.INFO, "", 0, f"{index} " + "x" * 100, (), None))
                self.assertEqual(4, len(list(root.glob("engine.log*"))))
                self.assertIn("11 ", (root / "engine.log").read_text(encoding="utf-8"))
            finally: sink.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
