import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "engine"))

from download_model import REQUIRED_FILES, verify_local_model


class ModelAcquisitionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ffa-model-assets-")
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def populate_assets(self):
        for name in REQUIRED_FILES:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"readiness-fixture")
        (self.root / "config.json").write_text(json.dumps({"model_type": "embedding_gemma2"}), encoding="utf-8")
        (self.root / "modules.json").write_text('[{"type": "sentence_transformers.models.Transformer"}]', encoding="utf-8")

    def test_missing_assets_are_reported_without_creating_files(self):
        with self.assertRaisesRegex(FileNotFoundError, "model.safetensors"):
            verify_local_model(self.root)
        self.assertEqual([], list(self.root.iterdir()))

    def test_wrong_model_and_empty_weights_are_rejected(self):
        self.populate_assets()
        (self.root / "config.json").write_text('{"model_type": "another_model"}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "not EmbeddingGemma 2"):
            verify_local_model(self.root)
        (self.root / "model.safetensors").write_bytes(b"")
        with self.assertRaisesRegex(FileNotFoundError, "model.safetensors"):
            verify_local_model(self.root)

    def test_readiness_check_preserves_existing_assets(self):
        self.populate_assets()
        before = {name: (self.root / name).read_bytes() for name in REQUIRED_FILES}
        verify_local_model(self.root)
        self.assertEqual(before, {name: (self.root / name).read_bytes() for name in REQUIRED_FILES})


if __name__ == "__main__":
    unittest.main(verbosity=2)
