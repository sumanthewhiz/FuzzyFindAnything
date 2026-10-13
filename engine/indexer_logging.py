from datetime import datetime, timezone
import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
import sys
import traceback
import xml.etree.ElementTree as ET

from paths import path_key


def load_logging_settings(data_directory):
    path = data_directory / "settings.xml"
    default_directory = data_directory / "Logs"
    if not path.exists():
        return True, default_directory
    with path.open("rb") as stream:
        contents = stream.read(1_048_577)
    if len(contents) > 1_048_576:
        raise ValueError(f"Configuration exceeds the 1 MB limit: {path}")
    text = contents.decode("utf-8-sig")
    if "<!doctype" in text.lower():
        raise ValueError("Configuration must not contain a document type declaration.")
    root = ET.fromstring(text)
    if root.tag != "FuzzyFindAnythingConfiguration" or root.get("version") != "1":
        raise ValueError(f"Unsupported configuration format or version: {path}")
    element = root.find("Logging")
    if element is None:
        return True, default_directory
    enabled = element.get("enabled", "true").lower()
    if enabled not in ("true", "false"):
        raise ValueError("The logging enabled setting must be true or false.")
    directory = Path(element.get("directory", str(default_directory)))
    if not directory.is_absolute():
        raise ValueError("The logging directory must be an absolute path.")
    return enabled == "true", directory


class StrictRotatingFileHandler(RotatingFileHandler):
    def handleError(self, record):
        error = sys.exc_info()[1]
        raise OSError(f"Could not write {self.baseFilename}: {error}") from error


class IndexerLogging(logging.Handler):
    """Switchable logs; logging failures are reported in status without losing indexing work."""

    def __init__(self, enabled, directory):
        super().__init__()
        self.enabled = enabled
        self.directory = Path(directory)
        self.error = ""
        self.engine_handler = None
        self.file_handler = None
        try:
            self.configure(enabled, directory)
        except OSError as error:
            self.error = f"Logging is unavailable at {directory}: {error}"

    def configure(self, enabled, directory):
        if type(enabled) is not bool:
            raise ValueError("The logging enabled setting must be a boolean.")
        directory = Path(directory)
        if not directory.is_absolute():
            raise ValueError("The logging directory must be an absolute path.")
        engine_handler = file_handler = None
        try:
            if enabled:
                directory.mkdir(parents=True, exist_ok=True)
                engine_handler = StrictRotatingFileHandler(directory / "engine.log", maxBytes=4_000_000,
                                                           backupCount=3, encoding="utf-8")
                engine_handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s %(message)s"))
                file_handler = StrictRotatingFileHandler(directory / "indexer.jsonl", maxBytes=8_000_000,
                                                         backupCount=3, encoding="utf-8")
                file_handler.setFormatter(logging.Formatter("%(message)s"))
        except OSError:
            if engine_handler: engine_handler.close()
            if file_handler: file_handler.close()
            raise
        with self.lock:
            previous = (self.engine_handler, self.file_handler)
            self.engine_handler, self.file_handler = engine_handler, file_handler
            self.enabled, self.directory, self.error = enabled, directory, ""
            for handler in previous:
                if handler: handler.close()

    def _write(self, handler_name, record):
        with self.lock:
            if not self.enabled or self.error:
                return
            handler = getattr(self, handler_name)
            try:
                handler.handle(record)
            except OSError as error:
                self.error = f"Logging stopped at {self.directory}: {error}. Choose a writable log location or re-enable logging."

    def emit(self, record):
        self._write("engine_handler", record)

    def record_file(self, job, outcome, duration_ms, *, passages=0, error=None, code=None, warning=None):
        value = {
            "timestamp": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
            "path": job["path"], "kind": job["kind"], "outcome": outcome,
            "duration_ms": round(duration_ms, 1), "attempt": job["attempts"] + 1,
            "revision": job["revision"], "passages": passages, "warning": warning,
            "error": None if error is None else {
                "type": type(error).__name__, "code": code, "message": str(error),
                "details": "".join(traceback.format_exception(error)),
            },
        }
        record = logging.LogRecord("indexer.files", logging.INFO, "", 0, json.dumps(
            value, ensure_ascii=False, allow_nan=False), (), None)
        self._write("file_handler", record)

    def owns(self, path):
        path = Path(path)
        if path_key(path.parent) != path_key(self.directory):
            return False
        return any(path.name.lower() == name or path.name.lower().startswith(name + ".")
                   for name in ("engine.log", "native-engine.log", "app.log", "indexer.jsonl"))

    def status(self):
        with self.lock:
            return {"enabled": self.enabled, "directory": str(self.directory), "error": self.error}

    def close(self):
        with self.lock:
            for handler in (self.engine_handler, self.file_handler):
                if handler: handler.close()
            self.engine_handler = self.file_handler = None
            self.enabled = False
        super().close()
