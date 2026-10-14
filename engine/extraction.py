import codecs
from dataclasses import dataclass
import json
import locale
import logging
import subprocess
import time
from pathlib import Path

from paths import file_kind

MAX_TEXT_CHARACTERS = 8_000_000
log = logging.getLogger(__name__)


class ExtractionError(RuntimeError):
    def __init__(self, message: str, code: str, retryable: bool = False):
        super().__init__(message)
        self.code = code
        self.retryable = retryable


@dataclass(frozen=True)
class ExtractedText:
    text: str
    warnings: tuple[str, ...] = ()
    format: str = ".txt"


def read_text(path: Path) -> str:
    with path.open("rb") as stream:
        raw = stream.read(MAX_TEXT_CHARACTERS * 4 + 1)
    if not raw:
        raise ExtractionError("The file is empty (0 bytes); there is no content to index.", "empty_file")
    if raw.startswith(b"\x00\x01\x00\x00") and (b"Standard ACE DB" in raw[:64] or b"Standard Jet DB" in raw[:64]):
        raise ExtractionError("The file contains a Microsoft Access database, not plain text.", "unsupported_content")
    unicode_bom = raw.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE, codecs.BOM_UTF32_LE, codecs.BOM_UTF32_BE))
    if file_kind(path) == "Code" and b"\x00" in raw and not unicode_bom:
        raise ExtractionError("The code file contains binary data rather than plain-text source.", "unsupported_content")
    if len(raw) > MAX_TEXT_CHARACTERS * 4:
        raise ExtractionError("Text file exceeds the 32 MB extraction safety limit.", "resource_limit")
    try:
        if raw.startswith((codecs.BOM_UTF32_LE, codecs.BOM_UTF32_BE)):
            text = raw.decode("utf-32")
        elif raw.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
            text = raw.decode("utf-16")
        else:
            try:
                text = raw.decode("utf-8-sig")
            except UnicodeDecodeError:
                text = raw.decode(locale.getencoding())
    except UnicodeDecodeError as exc:
        raise ExtractionError(
            "The file is not valid UTF-8, BOM-marked UTF-16/32, or Windows-encoded text.",
            "invalid_text_encoding",
        ) from exc
    if len(text) > MAX_TEXT_CHARACTERS:
        raise ExtractionError("Text exceeds the 8 million character extraction safety limit.", "resource_limit")
    text = text.replace("\x00", " ").strip()
    if not text:
        raise ExtractionError("The file contains no searchable text.", "no_text")
    return text


class TextExtractor:
    def __init__(self, executable: Path, timeout: float = 60):
        self.executable = executable
        self.timeout = timeout

    def extract(self, path: Path) -> str:
        return self.extract_details(path).text

    def extract_details(self, path: Path, is_cancelled=lambda: False) -> ExtractedText:
        if is_cancelled():
            raise InterruptedError("Extraction was cancelled.")
        if file_kind(path) == "Code":
            return ExtractedText(read_text(path), format=path.suffix.lower() or path.name)
        if path.suffix.lower() == ".txt":
            with path.open("rb") as stream:
                header = stream.read(1024)
            binary_document = (
                b"%PDF-" in header or header.startswith(b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1")
                or header.startswith(b"PK\x03\x04") or header.lstrip().startswith(b"{\\rtf")
            )
            if not binary_document:
                return ExtractedText(read_text(path))
        if not self.executable.is_file():
            raise FileNotFoundError(f"The Windows IFilter helper is missing: {self.executable}")
        started = time.monotonic()
        with subprocess.Popen(
            [str(self.executable), str(path)], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW,
        ) as process:
            while True:
                if is_cancelled() or time.monotonic() - started >= self.timeout:
                    process.kill()
                    process.communicate()
                    if is_cancelled():
                        raise InterruptedError("Extraction was cancelled.")
                    raise ExtractionError(
                        f"Windows IFilter exceeded {self.timeout:g} seconds for {path.name}; the isolated helper was stopped.",
                        "filter_timeout", True,
                    )
                try:
                    stdout, stderr = process.communicate(timeout=0.25)
                    break
                except subprocess.TimeoutExpired:
                    continue
            returncode = process.returncode
        try:
            data = json.loads(stdout.decode("utf-8-sig"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            code = "filter_timeout" if returncode == 124 else "filter_crash"
            diagnostic = stderr.decode("utf-8-sig", errors="replace").strip()[-2000:]
            raise ExtractionError(
                diagnostic or f"Windows IFilter returned no valid response for {path.name} (exit {returncode}).",
                code, True,
            ) from exc
        if not isinstance(data, dict):
            raise ExtractionError("The Windows IFilter helper returned an invalid response object.", "filter_protocol", True)
        if returncode != 0:
            error = data.get("error", {})
            raise ExtractionError(
                error.get("message", f"Windows IFilter failed with exit {returncode}."),
                error.get("code", "filter_failure"), bool(error.get("retryable", True)),
            )
        if not isinstance(data.get("text"), str):
            raise ExtractionError("The Windows IFilter helper returned no text field.", "filter_protocol", True)
        text = data["text"].strip()
        if not text:
            raise ExtractionError("The document contains no extractable text.", "no_text")
        warnings = tuple(data.get("warnings", []))
        for warning in warnings:
            log.warning("IFilter %s: %s", path, warning)
        return ExtractedText(text, warnings, data.get("format", path.suffix.lower()))
