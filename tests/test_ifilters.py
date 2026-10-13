import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from extraction import ExtractionError, TextExtractor, read_text
from fixtures import make_pdf
from model_client import EmbeddingClient
from test_engine import vector


class NativeFilterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.executable = ROOT / "artifacts" / "ifilter-repair" / "extractor" / "FuzzyFindAnything.Extractor.exe"
        if not cls.executable.is_file():
            cls.executable = ROOT / "artifacts" / "FuzzyFindAnything" / "extractor" / "FuzzyFindAnything.Extractor.exe"

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ffa-filter-tests-")
        self.root = Path(self.temporary.name)
        self.extractor = TextExtractor(self.executable)

    def tearDown(self):
        self.temporary.cleanup()

    def test_installed_pdf_filter_reads_pdf_and_reports_corrupt_input_accurately(self):
        valid = self.root / "valid.pdf"
        make_pdf(valid, "PDF filter regression content")
        result = self.extractor.extract_details(valid)
        self.assertIn("PDF filter regression content", result.text)
        broken = self.root / "broken.pdf"
        broken.write_bytes(b"%PDF-1.7\nThis is not a valid PDF object graph.")
        with self.assertRaises(ExtractionError) as raised:
            self.extractor.extract(broken)
        self.assertEqual("invalid_document", raised.exception.code)
        self.assertIn("IFilter was found", str(raised.exception))
        self.assertNotIn("Install", str(raised.exception))

    def test_empty_and_renamed_access_databases_are_permanent_content_errors(self):
        for suffix in (".pdf", ".txt"):
            empty = self.root / ("empty" + suffix)
            empty.write_bytes(b"")
            with self.assertRaises(ExtractionError) as raised:
                self.extractor.extract(empty)
            self.assertEqual("empty_file", raised.exception.code)
            self.assertFalse(raised.exception.retryable)
            renamed = self.root / ("database" + suffix)
            renamed.write_bytes(b"\x00\x01\x00\x00Standard ACE DB\x00" + bytes(128))
            with self.assertRaises(ExtractionError) as raised:
                self.extractor.extract(renamed)
            self.assertEqual("unsupported_content", raised.exception.code)
            self.assertIn("Access database", str(raised.exception))

    def test_real_format_selects_ifilter_without_modifying_original_file(self):
        path = self.root / "misnamed.docx"
        original = b"{\\rtf1\\ansi A rich text document with the wrong extension.}"
        path.write_bytes(original)
        result = self.extractor.extract_details(path)
        self.assertEqual(".rtf", result.format)
        self.assertIn("rich text document", result.text)
        self.assertTrue(result.warnings)
        self.assertEqual(original, path.read_bytes())
        pdf = self.root / "misnamed.txt"
        make_pdf(pdf, "Actual PDF inside a text filename")
        self.assertIn("Actual PDF", self.extractor.extract(pdf))


class ExtractionLifecycleTests(unittest.TestCase):
    def test_timeout_kills_and_reaps_helper(self):
        process = Mock()
        process.__enter__ = Mock(return_value=process)
        process.__exit__ = Mock(return_value=False)
        process.communicate.side_effect = [subprocess.TimeoutExpired("filter", 0.25), (b"", b"")]
        with patch("extraction.subprocess.Popen", return_value=process), patch(
            "extraction.time.monotonic", side_effect=[0, 0, 3]
        ):
            with self.assertRaises(ExtractionError) as raised:
                TextExtractor(Path(sys.executable), timeout=1).extract(Path("sample.pdf"))
        self.assertEqual("filter_timeout", raised.exception.code)
        self.assertTrue(raised.exception.retryable)
        process.kill.assert_called_once()
        self.assertEqual(2, process.communicate.call_count)

    def test_cancellation_stops_helper_before_timeout(self):
        process = Mock()
        process.__enter__ = Mock(return_value=process)
        process.__exit__ = Mock(return_value=False)
        process.communicate.side_effect = [subprocess.TimeoutExpired("filter", 0.25), (b"", b"")]
        cancelled = Mock(side_effect=[False, False, True, True])
        with patch("extraction.subprocess.Popen", return_value=process):
            with self.assertRaises(InterruptedError):
                TextExtractor(Path(sys.executable)).extract_details(Path("sample.pdf"), cancelled)
        process.kill.assert_called_once()

    def test_whitespace_only_text_is_not_sent_for_inference(self):
        with tempfile.TemporaryDirectory(prefix="ffa-empty-text-") as root:
            path = Path(root) / "blank.txt"
            path.write_text(" \n \t ", encoding="utf-8")
            with self.assertRaises(ExtractionError) as raised:
                read_text(path)
            self.assertEqual("no_text", raised.exception.code)
            path.write_bytes(b"\xff\xfe\x00")
            with self.assertRaises(ExtractionError) as raised:
                read_text(path)
            self.assertEqual("invalid_text_encoding", raised.exception.code)

    def test_document_batches_report_progress_and_honor_cancellation(self):
        client = object.__new__(EmbeddingClient)
        client._request = Mock(side_effect=[
            ["one", "two", "three", "four", "five"],
            [vector().tolist()] * 4,
            [vector().tolist()],
        ])
        progress = Mock()
        chunks = client.document("Document", "body", lambda: False, progress)
        self.assertEqual(5, len(chunks))
        self.assertEqual([(0, None), (0, 5), (4, 5), (5, 5)], [call.args for call in progress.call_args_list])
        client._request = Mock(return_value=["one", "two"])
        with self.assertRaises(InterruptedError):
            client.document("Document", "body", lambda: True)
        self.assertEqual(1, client._request.call_count)


if __name__ == "__main__":
    unittest.main(verbosity=2)
