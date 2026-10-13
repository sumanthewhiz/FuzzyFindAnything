"""Native NTFS USN replay plus recursive ReadDirectoryChangesW notifications."""

import ctypes
from ctypes import wintypes
import json
import logging
import os
from pathlib import Path
import struct
import threading

from paths import path_key

log = logging.getLogger(__name__)
QUERY_USN = 0x000900F4
READ_USN = 0x000900BB
READ_UNPRIVILEGED_USN = 0x000903AB
INVALID_HANDLE = ctypes.c_void_p(-1).value


def parse_usn_records(data: bytes) -> tuple[int, list[tuple[int, int, str]], bool]:
    if len(data) < 8:
        raise ValueError("USN journal buffer is shorter than its continuation cursor.")
    cursor = struct.unpack_from("<q", data)[0]
    records = []
    offset = 8
    unknown_version = False
    while offset < len(data):
        if offset + 8 > len(data):
            raise ValueError("Truncated USN record header.")
        length, version = struct.unpack_from("<IH", data, offset)
        if length < 8 or offset + length > len(data):
            raise ValueError("Invalid USN record length.")
        record = data[offset : offset + length]
        if version == 2:
            if length < 60:
                raise ValueError("Truncated USN_RECORD_V2.")
            file_id, parent_id = struct.unpack_from("<QQ", record, 8)
            name_length, name_offset = struct.unpack_from("<HH", record, 56)
        elif version == 3:
            if length < 76:
                raise ValueError("Truncated USN_RECORD_V3.")
            file_id = int.from_bytes(record[8:24], "little")
            parent_id = int.from_bytes(record[24:40], "little")
            name_length, name_offset = struct.unpack_from("<HH", record, 72)
        else:
            unknown_version = True
            offset += length
            continue
        if name_offset + name_length > length or name_length % 2:
            raise ValueError("Invalid USN filename bounds.")
        name = record[name_offset : name_offset + name_length].decode("utf-16-le")
        records.append((file_id, parent_id, name))
        offset += length
    return cursor, records, unknown_version


def parse_directory_records(data: bytes) -> list[str]:
    names = []
    offset = 0
    while offset < len(data):
        if offset + 12 > len(data):
            raise ValueError("Truncated directory notification.")
        next_offset, _, length = struct.unpack_from("<III", data, offset)
        if length % 2 or offset + 12 + length > len(data):
            raise ValueError("Invalid directory notification filename.")
        names.append(data[offset + 12 : offset + 12 + length].decode("utf-16-le"))
        if not next_offset:
            break
        if next_offset < 12 + length:
            raise ValueError("Invalid directory notification continuation.")
        offset += next_offset
    return names


class Overlapped(ctypes.Structure):
    _fields_ = [
        ("internal", ctypes.c_size_t), ("internal_high", ctypes.c_size_t),
        ("offset", wintypes.DWORD), ("offset_high", wintypes.DWORD), ("event", wintypes.HANDLE),
    ]


class WinApi:
    def __init__(self):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        declarations = {
            "CreateFileW": ([wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                             wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE], wintypes.HANDLE),
            "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
            "DeviceIoControl": ([wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD,
                                 ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                                 ctypes.c_void_p], wintypes.BOOL),
            "CreateEventW": ([ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR], wintypes.HANDLE),
            "ResetEvent": ([wintypes.HANDLE], wintypes.BOOL),
            "WaitForSingleObject": ([wintypes.HANDLE, wintypes.DWORD], wintypes.DWORD),
            "ReadDirectoryChangesW": ([wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, wintypes.BOOL,
                                       wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                                       ctypes.POINTER(Overlapped), ctypes.c_void_p], wintypes.BOOL),
            "GetOverlappedResult": ([wintypes.HANDLE, ctypes.POINTER(Overlapped),
                                     ctypes.POINTER(wintypes.DWORD), wintypes.BOOL], wintypes.BOOL),
            "CancelIoEx": ([wintypes.HANDLE, ctypes.c_void_p], wintypes.BOOL),
        }
        for name, (arguments, result) in declarations.items():
            function = getattr(self.kernel, name)
            function.argtypes = arguments
            function.restype = result

    def open(self, name: str, access: int, flags: int = 0):
        handle = self.kernel.CreateFileW(name, access, 7, None, 3, flags, None)
        if handle == INVALID_HANDLE:
            raise ctypes.WinError(ctypes.get_last_error())
        return handle

    def ioctl(self, handle, code: int, data: bytes = b"", size: int = 65536) -> bytes:
        source = ctypes.create_string_buffer(data) if data else None
        output = ctypes.create_string_buffer(size)
        returned = wintypes.DWORD()
        if not self.kernel.DeviceIoControl(
            handle, code, source, len(data), output, size, ctypes.byref(returned), None
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        return output.raw[: returned.value]


class DirectoryWatcher:
    def __init__(self, api: WinApi, path: str, callback, on_error):
        self.api = api
        self.path = path
        self.callback = callback
        self.on_error = on_error
        self.stop_event = threading.Event()
        self.handle = api.open(path, 1, 0x02000000 | 0x40000000)
        self.thread = threading.Thread(target=self._run, name="Directory notifications", daemon=True)
        self.thread.start()

    def _run(self):
        kernel = self.api.kernel
        event = kernel.CreateEventW(None, True, False, None)
        try:
            if not event:
                raise ctypes.WinError(ctypes.get_last_error())
            buffer = ctypes.create_string_buffer(65536)
            while not self.stop_event.is_set():
                kernel.ResetEvent(event)
                overlapped = Overlapped(event=event)
                returned = wintypes.DWORD()
                if not kernel.ReadDirectoryChangesW(
                    self.handle, buffer, len(buffer), True, 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x40,
                    None, ctypes.byref(overlapped), None
                ):
                    raise ctypes.WinError(ctypes.get_last_error())
                while kernel.WaitForSingleObject(event, 500) == 258:
                    if self.stop_event.is_set():
                        kernel.CancelIoEx(self.handle, ctypes.byref(overlapped))
                        break
                if self.stop_event.is_set():
                    kernel.CancelIoEx(self.handle, ctypes.byref(overlapped))
                    kernel.GetOverlappedResult(self.handle, ctypes.byref(overlapped), ctypes.byref(returned), True)
                    break
                if not kernel.GetOverlappedResult(self.handle, ctypes.byref(overlapped), ctypes.byref(returned), True):
                    error = ctypes.get_last_error()
                    if error == 1022:
                        self.callback(None, True)
                        continue
                    raise ctypes.WinError(error)
                if not returned.value:
                    self.callback(None, True)
                    continue
                try:
                    names = parse_directory_records(buffer.raw[: returned.value])
                except ValueError:
                    log.exception("Directory notification overflow/corruption; scheduling a full reconciliation")
                    self.callback(None, True)
                    continue
                for name in names:
                    self.callback(str(Path(self.path) / name), False)
        except OSError as exc:
            if not self.stop_event.is_set():
                log.warning("Directory notifications failed for %s: %s", self.path, exc)
                self.on_error(str(exc))
                self.callback(None, True)
        finally:
            if event:
                kernel.CloseHandle(event)
            kernel.CloseHandle(self.handle)

    def stop(self):
        self.stop_event.set()
        self.thread.join(timeout=3)


class Journal:
    def __init__(self, api: WinApi, drive: str, saved: dict | None):
        self.api = api
        self.drive = drive
        # Unprivileged journal controls require a directory handle, not a raw-volume handle.
        self.handle = api.open(drive + "\\", 0, 0x02000000)
        self.read_code = READ_UNPRIVILEGED_USN
        try:
            data = api.ioctl(self.handle, QUERY_USN)
            self.journal_id, first, next_usn, lowest = struct.unpack_from("<Qqqq", data)
            valid = saved and saved["id"] == self.journal_id and max(first, lowest) <= saved["cursor"] <= next_usn
            self.cursor = saved["cursor"] if valid else next_usn
            self.gap = bool(saved) and not valid
            self._read(self.cursor)
        except OSError:
            api.kernel.CloseHandle(self.handle)
            self.handle = api.open("\\\\.\\" + drive, 0x80000000)
            self.read_code = READ_USN
            try:
                data = api.ioctl(self.handle, QUERY_USN)
                self.journal_id, first, next_usn, lowest = struct.unpack_from("<Qqqq", data)
                valid = saved and saved["id"] == self.journal_id and max(first, lowest) <= saved["cursor"] <= next_usn
                self.cursor = saved["cursor"] if valid else next_usn
                self.gap = bool(saved) and not valid
                self._read(self.cursor)
            except (OSError, ValueError):
                api.kernel.CloseHandle(self.handle)
                raise

    def _read(self, cursor: int) -> bytes:
        request = struct.pack("<qIIQQQHH4x", cursor, 0xFFFFFFFF, 0, 0, 0, self.journal_id, 2, 3)
        return self.api.ioctl(self.handle, self.read_code, request)

    def read(self) -> tuple[int, list[tuple[int, int, str]], bool]:
        return parse_usn_records(self._read(self.cursor))

    def reset(self):
        data = self.api.ioctl(self.handle, QUERY_USN)
        self.journal_id, _, self.cursor = struct.unpack_from("<Qqq", data)

    def close(self):
        self.api.kernel.CloseHandle(self.handle)


class ChangeMonitor:
    def __init__(self, store, condition: threading.Condition, on_change):
        self.store = store
        self.condition = condition
        self.on_change = on_change
        self.api = WinApi()
        self.watchers: dict[int, DirectoryWatcher] = {}
        self.journals: dict[str, Journal] = {}
        self.roots: dict[int, tuple[str, str]] = {}
        self.identities: dict[int, dict[int, str]] = {}
        self.warnings: dict[str, str] = {}
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self._run, name="USN journal replay", daemon=True)

    def configure(self, folders: list[dict]):
        requested = {folder["id"]: folder["path"] for folder in folders}
        retired = []
        with self.condition:
            for identifier in set(self.roots) - set(requested):
                watcher = self.watchers.pop(identifier, None)
                if watcher:
                    retired.append(watcher)
                self.roots.pop(identifier)
                self.identities.pop(identifier, None)
                self.warnings.pop(str(identifier), None)
            for identifier, path in requested.items():
                if identifier in self.roots:
                    continue
                drive = os.path.splitdrive(path)[0].upper()
                self.roots[identifier] = (path, drive)
                try:
                    self.identities[identifier] = {os.stat(path).st_ino: path}
                    self.watchers[identifier] = DirectoryWatcher(
                        self.api, path,
                        lambda changed, force, root=identifier: self.on_change(root, changed, force),
                        lambda error, root=identifier: self._watch_error(root, error),
                    )
                except OSError as exc:
                    self.warnings[str(identifier)] = f"Directory notifications unavailable: {exc}"
                    log.warning("Watching %s failed: %s", path, exc)
                if drive not in self.journals and drive not in self.warnings:
                    try:
                        if len(drive) != 2 or drive[1] != ":":
                            raise OSError("USN journals require a local NTFS/ReFS volume.")
                        saved_text = self.store.get_setting("usn:" + drive)
                        saved = json.loads(saved_text) if saved_text else None
                        journal = Journal(self.api, drive, saved)
                        self.journals[drive] = journal
                        if journal.gap:
                            self._dirty_volume(drive, True)
                    except (OSError, ValueError, KeyError) as exc:
                        self.warnings[drive] = f"USN unavailable on {drive}: {exc}"
                        log.warning(self.warnings[drive])
            used_drives = {drive for _, drive in self.roots.values()}
            for drive in set(self.journals) - used_drives:
                self.journals.pop(drive).close()
                self.warnings.pop(drive, None)
        for watcher in retired:
            watcher.stop()

    def _watch_error(self, identifier: int, error: str):
        with self.condition:
            self.warnings[str(identifier)] = f"Directory notifications stopped: {error}; reconciliation remains active."

    def update_identities(self, identifier: int, identities: dict[int, str]):
        with self.condition:
            if identifier in self.roots:
                self.identities[identifier] = identities

    def _dirty_volume(self, drive: str, force: bool):
        for identifier, (_, root_drive) in self.roots.items():
            if drive == root_drive:
                self.on_change(identifier, None, force)

    def _run(self):
        while not self.stop_event.wait(2):
            with self.condition:
                for drive, journal in list(self.journals.items()):
                    try:
                        for _ in range(32):
                            cursor, records, unknown = journal.read()
                            if unknown:
                                self._dirty_volume(drive, True)
                            for file_id, parent_id, name in records:
                                for identifier, (_, root_drive) in self.roots.items():
                                    if drive != root_drive:
                                        continue
                                    known = self.identities.get(identifier, {})
                                    path = known.get(file_id)
                                    if path is None and parent_id in known:
                                        path = str(Path(known[parent_id]) / name)
                                    if path:
                                        self.on_change(identifier, path, False)
                            unchanged = cursor == journal.cursor
                            journal.cursor = cursor
                            if unchanged:
                                break
                    except (OSError, ValueError) as exc:
                        log.warning("USN replay needs reconciliation on %s: %s", drive, exc)
                        self._dirty_volume(drive, True)
                        try:
                            journal.reset()
                        except OSError as reset_error:
                            self.warnings[drive] = f"USN unavailable on {drive}: {reset_error}"
                            self.journals.pop(drive).close()

    def checkpoint_cursors(self):
        # The engine calls this under the same condition lock, only after its pending queue is drained.
        for drive, journal in self.journals.items():
            self.store.set_setting("usn:" + drive, json.dumps({"id": journal.journal_id, "cursor": journal.cursor}))

    @property
    def description(self) -> str:
        with self.condition:
            if not self.roots:
                return "Add a folder to enable automatic change tracking."
            base = "USN journal + directory notifications" if self.journals else "Directory notifications + periodic reconciliation"
            if self.warnings:
                base += ". " + " ".join(self.warnings.values())
            return base

    def start(self):
        self.thread.start()

    def stop(self):
        self.stop_event.set()
        if self.thread.ident is not None:
            self.thread.join(timeout=5)
        for watcher in list(self.watchers.values()):
            watcher.stop()
        for journal in self.journals.values():
            journal.close()
