"""Download only public model assets. No document content leaves this machine."""

import os
import json
from pathlib import Path

os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["DO_NOT_TRACK"] = "1"

MODEL_ID = "google/embeddinggemma-2"
MODEL_REVISION = "914f7f89142e33e77833254d9c9b90c3cef7303b"
REQUIRED_FILES = (
    "config.json",
    "config_sentence_transformers.json",
    "modules.json",
    "model.safetensors",
    "sentence_bert_config.json",
    "tokenizer.json",
    "tokenizer.model",
    "tokenizer_config.json",
    "processor_config.json",
    "preprocessor_config.json",
    "chat_template.jinja",
    "1_Pooling\\config.json",
    "2_Normalize\\config.json",
)


def verify_local_model(destination: Path) -> None:
    missing = [
        name for name in REQUIRED_FILES
        if not (destination / name).is_file() or (destination / name).stat().st_size == 0
    ]
    if missing:
        raise FileNotFoundError("Missing or empty model assets: " + ", ".join(missing))
    config = json.loads((destination / "config.json").read_text(encoding="utf-8"))
    if config.get("model_type") != "embedding_gemma2":
        raise ValueError("The local model configuration is not EmbeddingGemma 2.")
    modules = json.loads((destination / "modules.json").read_text(encoding="utf-8"))
    if not isinstance(modules, list) or not modules:
        raise ValueError("The local SentenceTransformers module configuration is invalid.")


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--destination", required=True, type=Path)
    parser.add_argument("--verify-only", action="store_true", help="Check local asset readiness without network access or writes.")
    args = parser.parse_args()
    if not args.verify_only:
        from huggingface_hub import snapshot_download

        snapshot_download(
            MODEL_ID,
            revision=MODEL_REVISION,
            local_dir=str(args.destination),
            allow_patterns=["*.json", "*.safetensors", "*.model", "*.jinja", "README.md"],
        )
    verify_local_model(args.destination)
    print(f"Model assets ready: {args.destination.resolve()}")
    print(f"Acquisition source: {MODEL_ID} @ {MODEL_REVISION}")
    if args.verify_only:
        print("Readiness check only: file presence and model configuration; not a weight checksum or inference test.")
