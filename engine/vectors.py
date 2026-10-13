import numpy as np

DIMENSIONS = 768
MODEL_FINGERPRINT = "google/embeddinggemma-2@914f7f89142e33e77833254d9c9b90c3cef7303b:768:f32:v1"


def normalized(vector: np.ndarray) -> np.ndarray:
    vector = np.asarray(vector, dtype=np.float32).reshape(-1)
    if vector.shape != (DIMENSIONS,) or not np.isfinite(vector).all():
        raise ValueError(f"The embedding model must return {DIMENSIONS} finite values.")
    length = float(np.linalg.norm(vector))
    if length < 1e-12:
        raise ValueError("The embedding model returned a zero-length vector.")
    return np.ascontiguousarray(vector / length, dtype=np.float32)
