"""A separate, local inference process keeps PyTorch and DiskANN native runtimes isolated."""

import argparse
import contextlib
import json
import logging
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))

from cpu_budget import get_budget
budget = get_budget(apply=True)
threads = int(os.environ.get("FFA_INFERENCE_THREADS", budget.threads_per_worker))
if not 1 <= threads <= budget.processors:
    raise ValueError("Inference thread count exceeds the inherited CPU budget.")
for variable in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
    os.environ[variable] = str(threads)
protocol = sys.stdout

from startup import MODEL_STEPS


def send(message):
    protocol.write(json.dumps(message, ensure_ascii=True, allow_nan=False) + "\n")
    protocol.flush()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True, type=Path)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, stream=sys.stderr,
                        format="%(asctime)s %(levelname)s model-worker[%(process)d] %(message)s")

    def progress(completed, stage):
        logging.info("Startup checkpoint %s/%s: %s", completed, MODEL_STEPS, stage)
        send({"event": "startup", "completed": completed, "total": MODEL_STEPS, "stage": stage})

    try:
        progress(1, "Importing numerical inference runtime")
        with contextlib.redirect_stdout(sys.stderr):
            from embeddings import EmbeddingModel
            model = EmbeddingModel(args.model, progress=progress)
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
                elif operation == "images":
                    result = [vector.tolist() for vector in model.encode_images([Path(path) for path in arguments["paths"]])]
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
