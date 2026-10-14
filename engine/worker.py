import argparse
import faulthandler
import json
import logging
import msvcrt
import os
from pathlib import Path
import sys
import threading

# Native DiskANN writes to the C stdout descriptor. Keep that away from the JSON IPC channel.
protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
sys.stdout = sys.stderr
sys.path.insert(0, str(Path(__file__).resolve().parent))

from cpu_budget import get_budget
CPU_BUDGET = get_budget(apply=True)

from indexer import IndexingEngine
from indexer_logging import IndexerLogging, load_logging_settings

output_lock = threading.Lock()


def send(value: dict):
    with output_lock:
        protocol.write(json.dumps(value, ensure_ascii=True, allow_nan=False) + "\n")
        protocol.flush()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--extractor", type=Path, required=True)
    parser.add_argument("--app-dir", type=Path, required=True)
    args = parser.parse_args()
    args.data_dir = args.data_dir.resolve()
    args.data_dir.mkdir(parents=True, exist_ok=True)
    trace_file = None
    if os.environ.get("FFA_TRACE_HANGS") == "1":
        trace_file = (args.data_dir / "threads.log").open("w", encoding="utf-8")
        faulthandler.dump_traceback_later(30, repeat=True, file=trace_file)
    lease = (args.data_dir / "engine.lock").open("a+b")
    if lease.tell() == 0:
        lease.write(b"\0")
        lease.flush()
    lease.seek(0)
    try:
        msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError as exc:
        lease.close()
        raise RuntimeError("Another engine is already using this local index.") from exc
    engine = None
    logs = None
    stopped = threading.Event()
    reporter = None
    try:
        logs = IndexerLogging(*load_logging_settings(args.data_dir))
        logging.basicConfig(level=logging.INFO, handlers=[logs], force=True)
        engine = IndexingEngine(args.data_dir, args.model, args.extractor, args.app_dir, outcome_log=logs)
        engine.start()

        def report():
            previous_state = None
            while not stopped.is_set():
                try:
                    status = engine.status()
                except Exception as exc:
                    logging.exception("Could not refresh index status")
                    try:
                        send({"event": "fatal", "error": f"Could not refresh index status: {exc}"})
                    except OSError:
                        logging.warning("The UI pipe closed while reporting the status failure.")
                    stopped.set()
                    return
                try:
                    if status["model_state"] != previous_state:
                        logging.info("Publishing engine state: %s", status["model_state"])
                        previous_state = status["model_state"]
                    send({"event": "status", "data": status})
                except (BrokenPipeError, OSError):
                    stopped.set()
                    return
                stopped.wait(1)

        reporter = threading.Thread(target=report, name="UI status", daemon=True)
        reporter.start()
        for line in sys.stdin:
            request_id = None
            try:
                request = json.loads(line)
                if not isinstance(request, dict):
                    raise ValueError("A request must be a JSON object.")
                request_id = request.get("id")
                command = request.get("command")
                arguments = request.get("args") or {}
                if command == "status":
                    result = engine.status()
                elif command == "add_folder":
                    result = {"id": engine.add_folder(arguments["path"])}
                elif command == "remove_folders":
                    engine.remove_folders(arguments["ids"])
                    result = engine.status()
                elif command == "search":
                    result = engine.search(
                        arguments["query"], float(arguments.get("threshold", 0.6)), int(arguments.get("limit", 50))
                    )
                elif command == "rescan":
                    for folder in engine.store.folders():
                        engine.schedule(folder["id"], force=True, immediate=True)
                    result = engine.status()
                elif command == "reload_configuration":
                    logs.configure(*load_logging_settings(args.data_dir))
                    result = engine.status()
                elif command == "shutdown":
                    send({"id": request_id, "ok": True, "result": None})
                    break
                else:
                    raise ValueError(f"Unknown engine command: {command}")
                send({"id": request_id, "ok": True, "result": result})
            except Exception as exc:
                logging.exception("Engine request failed")
                send({"id": request_id, "ok": False, "error": str(exc)})
    finally:
        if trace_file:
            faulthandler.cancel_dump_traceback_later()
            trace_file.close()
        stopped.set()
        if reporter:
            reporter.join(timeout=3)
        try:
            if engine:
                engine.stop()
        finally:
            lease.seek(0)
            msvcrt.locking(lease.fileno(), msvcrt.LK_UNLCK, 1)
            lease.close()
            if logs:
                logs.close()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        logging.exception("The local engine stopped unexpectedly")
        try:
            send({"event": "fatal", "error": str(exc)})
        except (BrokenPipeError, OSError):
            pass
        sys.exit(1)
