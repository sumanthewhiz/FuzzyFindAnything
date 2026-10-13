"""Offline index reset; folder scope is retained unless --clear-folders is explicit."""

import argparse
from datetime import datetime
import json
import msvcrt
import os
from pathlib import Path
import re
import shutil
import sqlite3
from contextlib import closing


def reset(data_directory: Path, *, clear_folders: bool = False):
    data_directory = data_directory.resolve(strict=True)
    catalog = data_directory / "catalog.sqlite3"
    if not catalog.is_file():
        raise FileNotFoundError(f"No existing catalog at {catalog}")
    index = data_directory / "diskann"
    if index.exists() and getattr(index.lstat(), "st_reparse_tag", 0) & 0x20000000:
        raise ValueError("The DiskANN directory is a reparse link; resolve its storage location before resetting.")
    with (data_directory / "engine.lock").open("r+b") as lease:
        lease.seek(0)
        try:
            msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as exc:
            raise RuntimeError("The indexing engine is running. Quit the application before resetting its index.") from exc
        try:
            with closing(sqlite3.connect(catalog, timeout=5)) as connection:
                connection.execute("PRAGMA foreign_keys=ON")
                folders = connection.execute("SELECT id,path,path_key FROM folders ORDER BY id").fetchall()
                backup = data_directory / f"configured-folders-{datetime.now():%Y%m%d-%H%M%S-%f}.json"
                with backup.open("x", encoding="utf-8") as stream:
                    json.dump([{"id": row[0], "path": row[1], "path_key": row[2]} for row in folders], stream, ensure_ascii=False, indent=2)
                    stream.flush()
                    os.fsync(stream.fileno())
                with connection:
                    connection.execute("BEGIN EXCLUSIVE")
                    if connection.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='index_jobs'").fetchone():
                        connection.execute("DELETE FROM index_jobs")
                    connection.execute("DELETE FROM folder_files")
                    connection.execute("DELETE FROM vectors")
                    connection.execute("DELETE FROM files")
                    connection.execute("DELETE FROM sqlite_sequence WHERE name IN ('files','vectors','index_jobs')")
                    connection.execute("DELETE FROM settings WHERE key LIKE 'usn:%' OR key LIKE 'scan_error:%' OR key='last_refreshed'")
                    connection.execute("UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='generation'")
                    if clear_folders:
                        connection.execute("DELETE FROM folders")
                        connection.execute("DELETE FROM sqlite_sequence WHERE name='folders'")
                    else:
                        connection.execute(
                            "UPDATE folders SET status='In Progress', detail='Waiting for full reindex', completed=0,total=0,errors=0"
                        )
                connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                connection.execute("VACUUM")
                expected = [] if clear_folders else folders
                assert connection.execute("SELECT id,path,path_key FROM folders ORDER BY id").fetchall() == expected
                assert connection.execute("SELECT COUNT(*) FROM files").fetchone()[0] == 0
                assert connection.execute("SELECT COUNT(*) FROM vectors").fetchone()[0] == 0
            if index.is_dir():
                for name in ("current.json", "current.tmp"):
                    (index / name).unlink(missing_ok=True)
                for snapshot in index.iterdir():
                    if (snapshot.is_dir() and not getattr(snapshot.lstat(), "st_reparse_tag", 0) & 0x20000000
                            and re.fullmatch(r"snapshot-[0-9a-f]{32}", snapshot.name)):
                        shutil.rmtree(snapshot)
            print(json.dumps({
                "folders_preserved": 0 if clear_folders else len(folders),
                "folders_cleared": len(folders) if clear_folders else 0,
                "folder_backup": str(backup),
                "files": 0, "vectors": 0, "queued_jobs": 0,
                "catalog": str(catalog),
            }, ensure_ascii=True))
        finally:
            lease.seek(0)
            msvcrt.locking(lease.fileno(), msvcrt.LK_UNLCK, 1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", required=True, type=Path)
    parser.add_argument("--clear-folders", action="store_true", help="Also remove the configured folder list.")
    args = parser.parse_args()
    reset(args.data_dir, clear_folders=args.clear_folders)
