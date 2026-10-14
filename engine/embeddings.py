import os
import logging
import threading
import warnings
from collections.abc import Callable
from contextlib import contextmanager
from pathlib import Path

os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["DO_NOT_TRACK"] = "1"
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"

import numpy as np
import torch

from vectors import DIMENSIONS, MODEL_FINGERPRINT, normalized

log = logging.getLogger(__name__)


class EmbeddingModel:
    def __init__(self, model_path: Path, *, progress=None):
        if not (model_path / "model.safetensors").is_file():
            raise FileNotFoundError(
                "EmbeddingGemma 2 is not downloaded. Run Get-Model.ps1, then restart the app."
            )
        if progress: progress(2, "Importing embedding libraries")
        log.info("Importing the local sentence-transformers runtime.")
        from sentence_transformers import SentenceTransformer

        if progress: progress(3, "Configuring CPU inference")
        log.info("Configuring CPU inference threads.")
        torch.set_num_threads(max(1, int(os.environ.get("FFA_INFERENCE_THREADS", os.environ.get("FFA_CPU_LIMIT", "2")))))
        torch.set_num_interop_threads(1)
        if progress: progress(4, "Loading model weights and tokenizer")
        log.info("Loading the local model weights.")
        self.model = SentenceTransformer(
            str(model_path),
            device="cpu",
            local_files_only=True,
            trust_remote_code=False,
            config_kwargs={"audio_config": None},
            model_kwargs={"torch_dtype": torch.float32},
        )
        if progress: progress(5, "Finalizing text and vision inference")
        self.model.eval()
        self.condition = threading.Condition()
        self.busy = False
        self.waiting_queries = 0
        if progress: progress(6, "Search model loaded")

    @contextmanager
    def _slot(self, query: bool = False):
        with self.condition:
            if query:
                self.waiting_queries += 1
            try:
                self.condition.wait_for(lambda: not self.busy and (query or self.waiting_queries == 0))
                self.busy = True
            finally:
                if query:
                    self.waiting_queries -= 1
        try:
            yield
        finally:
            with self.condition:
                self.busy = False
                self.condition.notify_all()

    def _encode(self, value, *, query: bool = False) -> np.ndarray:
        with self._slot(query):
            kwargs = {"prompt_name": "SearchQuery"} if query else {}
            return normalized(
                self.model.encode(
                    value,
                    normalize_embeddings=True,
                    convert_to_numpy=True,
                    show_progress_bar=False,
                    batch_size=1,
                    **kwargs,
                )
            )

    def query(self, text: str) -> np.ndarray:
        return self._encode(text, query=True)

    def encode_documents(self, texts: list[str]) -> list[np.ndarray]:
        if not texts:
            return []
        with self._slot():
            values = self.model.encode(
                texts, normalize_embeddings=True, convert_to_numpy=True,
                show_progress_bar=False, batch_size=min(4, len(texts)),
            )
            return [normalized(value) for value in values]

    def document(
        self, title: str, text: str, is_cancelled: Callable[[], bool]
    ) -> list[tuple[str, np.ndarray]]:
        chunks = []
        for passage in self.split_document(text):
            if is_cancelled():
                raise InterruptedError("The folder was removed or the engine is shutting down.")
            vector = self._encode(f"title: {title[:160]} | text: {passage}")
            chunks.append((" ".join(passage.split())[:400], vector))
        return chunks

    def split_document(self, text: str) -> list[str]:
        with self._slot():
            tokens = self.model.tokenizer.encode(text, add_special_tokens=False, verbose=False)
            if not tokens:
                raise ValueError("No searchable text was extracted from this document.")
            passages = []
            seen = set()
            for start in range(0, len(tokens), 448):
                passage = self.model.tokenizer.decode(tokens[start : start + 512], skip_special_tokens=True)
                key = " ".join(passage.split())
                if key and key not in seen:
                    passages.append(passage)
                    seen.add(key)
                if start + 512 >= len(tokens):
                    break
            return passages

    def image(self, path: Path) -> list[tuple[str, np.ndarray]]:
        from PIL import Image, ImageOps

        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(path) as original:
                original.seek(0)
                image = ImageOps.exif_transpose(original).convert("RGB")
                image.thumbnail((2048, 2048))
                vector = self._encode({"image": image})
        return [("Visual content indexed with EmbeddingGemma 2.", vector)]
