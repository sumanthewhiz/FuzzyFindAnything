"""A separate, local inference process keeps PyTorch and DiskANN native runtimes isolated."""

import argparse
import contextlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))

from cpu_budget import get_budget
get_budget(apply=True)


def send(message):
    sys.stdout.write(json.dumps(message, ensure_ascii=True, allow_nan=False) + "\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True, type=Path)
    args = parser.parse_args()
    try:
        with contextlib.redirect_stdout(sys.stderr):
            from embeddings import EmbeddingModel
            model = EmbeddingModel(args.model)
        send({"ok": True, "result": "ready"})
    except Exception as exc:
        send({"ok": False, "error": str(exc)})
        raise
    for line in sys.stdin:
        try:
            request = json.loads(line)
            operation = request["operation"]
            arguments = request.get("args", {})
            with contextlib.redirect_stdout(sys.stderr):
                if operation == "query":
                    result = model.query(arguments["text"]).tolist()
                elif operation == "document":
                    result = model._encode(arguments["text"]).tolist()
                elif operation == "documents":
                    result = [vector.tolist() for vector in model.encode_documents(arguments["texts"])]
                elif operation == "split":
                    result = model.split_document(arguments["text"])
                elif operation == "image":
                    result = model.image(Path(arguments["path"]))[0][1].tolist()
                elif operation == "shutdown":
                    result = None
                else:
                    raise ValueError(f"Unknown inference operation: {operation}")
            send({"ok": True, "result": result})
            if operation == "shutdown":
                break
        except Exception as exc:
            send({"ok": False, "error": str(exc)})


if __name__ == "__main__":
    main()
