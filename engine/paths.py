import os
from pathlib import Path

DOCUMENT_EXTENSIONS = {".txt", ".rtf", ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx"}
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico"}
SUPPORTED_EXTENSIONS = DOCUMENT_EXTENSIONS | IMAGE_EXTENSIONS


def path_key(path: str | Path) -> str:
    return os.path.normcase(os.path.abspath(os.path.normpath(str(path))))


def is_within(path: str | Path, folder: str | Path) -> bool:
    try:
        return os.path.commonpath([path_key(path), path_key(folder)]) == path_key(folder)
    except ValueError:
        return False


def file_kind(path: str | Path) -> str | None:
    suffix = Path(path).suffix.lower()
    if suffix in IMAGE_EXTENSIONS:
        return "Image"
    if suffix in DOCUMENT_EXTENSIONS:
        return "Document"
    return None


def fingerprint(path: str | Path) -> tuple[int, int, str]:
    info = os.stat(path)
    return info.st_mtime_ns, info.st_size, str(info.st_ino)
