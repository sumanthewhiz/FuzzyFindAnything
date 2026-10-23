# Quickstart

**Download the native application, provision its model, and search a small folder.**

Release packages include the .NET and native runtimes. No separate Python, .NET, compiler or package manager is required.

These instructions describe **1.5.0-dev.3**, including per-type search, content-aware chunking, regional image search, independent model sources and memory-aware native workers. Use a matching development build or build this checkout; earlier packages may lack these controls. Read the [chunking guide](docs/CHUNKING.md) for how app-owned boundaries differ from model tokenization, including EmbeddingGemma2.

[Project](README.md) · [Architecture](docs/ARCHITECTURE.md) · [Existing-user migration](docs/MIGRATION.md)

## Before you start

| Requirement | Details |
|---|---|
| Windows | Windows 11 x64, or ARM64 with AVX2-capable x64 emulation |
| CPU | At least two physical cores; all app processes share at most half |
| Memory | At least 5 GiB **available** for the default model; other profiles have their own allowance plus a 2 GiB reserve |
| Storage | Allow 5 GB for application/model extraction and downloads, plus space for your index |
| Documents | Compatible x64 Office/PDF IFilters must already be installed |
| Network | Needed for downloads, not normal indexing or search |

The ARM64 package runs the UI and model inference natively. Its DiskANN coordinator and document helper run as x64 processes. The release includes the matching native runtimes.

## 1. Download the right package

Open **[Build and release](https://github.com/sumanthewhiz/FuzzyFindAnything/actions/workflows/build.yml?query=branch%3Adev)**, select a successful **dev** run matching this checkout, and download the appropriate artifact. Actions downloads require GitHub sign-in and expire.

Published versioned downloads are also listed on the [Releases page](https://github.com/sumanthewhiz/FuzzyFindAnything/releases). Use the application ZIP and quickstart belonging to the same version.

| Your PC | Application ZIP |
|---|---|
| Intel or AMD | `FuzzyFindAnything-v<VERSION>-win-x64.zip` |
| Snapdragon / Windows on ARM | `FuzzyFindAnything-v<VERSION>-win-arm64.zip` |

An Actions artifact may be an outer ZIP containing the application ZIP and checksum. Extract the inner application ZIP completely, for example to:

```text
C:\Users\<you>\Apps\FuzzyFindAnything
```

Keep `engine`, `inference`, `hf-inference`, `extractor`, `tools`, `third-party`, and `runtime.json` beside the executable. Do not copy the executable out by itself or run it from inside an archive.

Compare the downloaded file's SHA-256 with the matching `.zip.sha256`:

```powershell
Get-FileHash .\FuzzyFindAnything-v<VERSION>-win-arm64.zip -Algorithm SHA256
```

Use the actual version/architecture. Packages are unsigned; verify their origin and follow your organization's application policy.

## 2. Provision the native model

The application uses the separately packaged **`embeddinggemma-2-onnx-v1`** export of `google/embeddinggemma-2`. The original Hugging Face `model.safetensors` file is **not** a substitute.

**A public ONNX hosting URL is not configured by default.** Obtain the approved native model bundle from the maintainer or your organization's artifact channel. The repository contains its immutable [file manifest](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/main/model-assets/embeddinggemma-2.json), also included as `model-manifest.json` in application packages, not the approximately 1.8 GB of model payload. End users do not export or convert the model.

Extract the bundle into a separate directory containing these eight files:

```text
config.json
language.onnx
language.onnx.data
vision.onnx
vision.onnx.data
tokenizer.json
tokenizer.model
README.md
```

From the extracted **application** directory:

```powershell
Set-Location "$env:USERPROFILE\Apps\FuzzyFindAnything"
.\Get-Model.ps1 -SourceDirectory 'D:\ApprovedModels\embeddinggemma-2-onnx-v1'
.\Get-Model.ps1 -VerifyOnly
```

The first command copies verified assets to `models\embeddinggemma-2-onnx` beside the application. The second performs a **read-only size and SHA-256 check of every asset**. Neither requires an interpreter or an SDK.

If an approved HTTPS directory hosting the manifest's individual files is available, use its base URL instead:

```powershell
.\Get-Model.ps1 -BaseUri 'https://YOUR-APPROVED-HOST/embeddinggemma-2-onnx-v1/'
```

Replace the example host with an actual approved location. Rerun the same command after an interruption to resume. Downloads are size-bounded, checksummed and staged before replacement; assets in use by native inference cannot be overwritten.

If script policy blocks an unsigned script, follow your device's application policy. Do not disable organization-enforced controls. Model acquisition errors are reported explicitly; a missing bundle is not downloaded from an unverified alternative.

## 3. Launch and index a small folder

```powershell
& .\FuzzyFindAnything.exe
```

For a separate trial without using an existing index:

```powershell
& .\FuzzyFindAnything.exe --data-dir "$env:LOCALAPPDATA\FuzzyFindAnything-NativeTrial"
```

Wait for **Ready**. The progress bar shows completed startup checkpoints and the current phase, not a time-based estimate. Startup validates the selected models; optional extra capacity for the shared default model loads in the background.

After an exit, the app also checks configured folders and subfolders for added, deleted or timestamp-changed files. This runs in the background and retains unchanged embeddings and saved passage progress. You can search the existing index while the status says **Checking indexed folders for changes...** or while incremental updates are queued.

1. Open **Indexed folders**, select **Add folder**, and choose a small local folder containing a few `.txt` files, source files and photos.
2. Wait for **Indexed**. The details show succeeded/failed totals; the footer shows current processing.
3. Open **Search**, enter a natural-language phrase, and select **Search**.
4. Open a document/code result or an image tile in its usual Windows viewer/editor.

For a predictable first test, save this text in `budget.txt`:

> The team is preparing next quarter's budget, including travel expenses, equipment purchases, and the annual conference.

Search for **planning spending for travel and equipment**. For code, try **function that reads a CSV file** against a matching source file. For photos, describe visible content such as **mountains beside a lake**.

Documents, code and images are all selected on first launch. Uncheck any types you do not want to search; the final selected type cannot be unchecked. These are search filters, not changes to indexing scope.

Each type has its own minimum similarity, initially **0.60**. All selections and thresholds survive restart. An existing global threshold is migrated to all three values. Lower only the relevant threshold if that type's results are too narrow; similarity is a score, not a probability.

Hover over **Matched chunk** at the bottom of a document/code result to read the full matched chunk, with symbol/line information where available. Older retained vectors without full provenance say **Matched excerpt**. Code line numbers refer to source; document line numbers refer to extracted text, not original Office/PDF page numbers.

## Everyday controls

| Action | Behavior |
|---|---|
| **Clear** / Escape in the search field | Clears the phrase and all three result sections; ignores late responses |
| Check folders, then **Remove selected** | Removes indexing ownership, never original files |
| **Index information** | Shows live counts, backend version, location, size and refresh time |
| **Dark mode** | Switches and saves appearance |
| **Configuration** | Enables/disables logging and selects a log directory |
| Minimize / Close / Alt+F4 | Hides the window in the system tray, not the taskbar; indexing continues |
| Tray icon / **Open Fuzzy Find Anything** | Restores the existing window maximized |
| **Quit**, in the window or tray menu | Stops the process tree; committed file/passage progress resumes next time |

The application does not register automatic startup at sign-in. Model-process count is not file-worker count: a 12-core PC can run six file pipelines with one model copy. Hover over CPU status for the separate values.

If the tray icon is not immediately visible, open Windows' notification-area overflow menu beside the clock.

## Change embedding models

Open **Model configuration** and enter HTTPS model-card URLs for **Documents / text**, **Source code** and **Images**. All three initially contain `https://huggingface.co/google/embeddinggemma-2`.

**Refresh model sources** checks optional local providers without changing your applied models. Authorized users can retrieve the separate [private companion](https://github.com/sumghosh_microsoft/FuzzyFindAnything-MicrosoftProvider) using an installed, authenticated GitHub CLI. Explicit repository access is required. The Microsoft source appears only after live registry access is also verified; other installations retain Hugging Face alone. A discovery notice clears when discovery completes, without dismissing unrelated errors.

Select **Apply model changes** once. The app validates the URL, required native assets, modality, available memory and local execution before replacing any embeddings. Downloads and device checks are shown in the tab. If the selections are unchanged, Apply does nothing.

| Example model URL | Compatible assignments |
|---|---|
| `https://huggingface.co/google/embeddinggemma-2` | Text, code, images; uses the separately provisioned default bundle |
| `https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2` | Text and code |
| `https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2` | Text and code |
| `https://huggingface.co/Xenova/clip-vit-base-patch32` | Text, code and images |
| `https://huggingface.co/Qwen/Qwen3-Embedding-0.6B` | Text and code; pinned stateless Qdrant ONNX export, 1,024 dimensions |
| `https://huggingface.co/intfloat/e5-base-v2` | Text and code; native mean pooling with correct retrieval prefixes |
| `https://huggingface.co/vectoriseai/e5-small-v2` | Text and code; a second checkpoint using the same reviewed prefix recipe |

These are examples, not a guarantee that every Hugging Face repository is compatible. The app supports reviewed native recipes and compatible BERT mean/CLS/already-pooled export contracts; additional projections, unreviewed instructions, tokenizers or scoring metrics require another reviewed recipe/adapter. CLIP retains its supported direct image/text interface. Qwen Coder generators are not substitutes for embedding models.

EmbeddingGemma2 needs no extra setup. Hugging Face selections use a model-card URL independently for each role. Requirements are resolved automatically without downloading weights; **Model details** appears only where applicable, and approved query-purpose choices remain inside it unless a recipe genuinely requires a choice. Tokenizer, pooling, graph and image-pipeline setup are not user configuration fields. Switching back to a simple model discards inactive query choices.

Compatible document encodings share a DiskANN index; query roles can remain different for text and code. Unrelated vector spaces do not share an index merely because dimensions match. A document-model change rebuilds only affected types; a query-only setting change retains existing vectors. The same XML file stores preferences, with pinned artifact manifests in the local model cache. Feature-specific schema versions are retained rather than downgraded; see the [rollback policy](docs/MIGRATION.md).

CPU, GPU and NPU eligibility is evaluated against actual local execution providers and an inference probe. Windows ML can activate compatible providers already installed on the device. A missing provider/driver can make an otherwise suitable model unavailable; this is not a ban on GPU/NPU-only models. CPU is tried first, followed by available local accelerators when needed. The tab displays the execution provider actually selected.

Search can continue against the applied configuration during downloading and device validation, with a short serialized commit boundary. Under memory pressure, a query may wait for native validation to release its memory lease or for an idle worker to reload. **Cancel model validation** leaves the active assignment intact if it has not committed. After acceptance, affected results can be incomplete during background reindexing; the tab reports completion or failures. It does not retain a separate serving copy of the old affected index.

Similarity scores differ between models; a threshold useful for EmbeddingGemma2 may be too high for CLIP. The application does not silently change your saved threshold. Qwen's reviewed fp32 artifact adds approximately 2.4 GB of model assets. Allow disk space for every selected model. Idle workers can be unloaded and later reloaded to avoid requiring old and replacement models to fit in RAM together; the reserve is never disabled.

### Image descriptions, OCR and regions

Set the **Images** URL to `https://huggingface.co/microsoft/Florence-2-base-ft`, `microsoft/Florence-2-base`, `microsoft/Florence-2-large-ft` or `microsoft/Florence-2-large` using its full Hugging Face URL. The app resolves a pinned ONNX export and automatically configures an image-understanding stage followed by the Documents/text embedder. Base generators add approximately 1.1 GB of assets; large generators add about 3.07 GiB and require a 6.5 GiB allowance plus the 2 GiB reserve. Download size is not peak RAM.

No manual pipeline or tokenizer setup is required. **Image description options** appears only for a compatible understanding model. Caption is the default task; enable OCR there if needed. **Use the Documents / text model** is an explicit dependency: changing Text will also re-embed image descriptions, without regenerating still-valid captions/OCR. Uncheck it to choose a separate compatible text model.

Qualifying image models also offer **Object labels** or **Region descriptions**. Select one regional task, optionally alongside caption/OCR, or use regions alone. The engine embeds each labeled region's text. Search returns the original image once, highlights its best matching region and exposes that region's text on hover; these are not separate cropped-image embeddings.

Select **Apply model changes** and watch the stage/count feedback. Images may have complete, partial, failed or empty/skipped task outcomes. Valid partial results remain searchable with warnings; **Retry incomplete image tasks** retries failed/truncated stages without discarding successful sibling tasks. OCR finding no text is a skipped/no-searchable-text result, not a fabricated embedding.

Search results remain in the image gallery and open the original image. **Generated caption** or **OCR text** identifies the matching evidence; hover for the matched text. Generated descriptions can omit details or hallucinate, and semantic OCR search is not exact literal text matching.

Selecting EmbeddingGemma2 or CLIP again hides all description-specific controls and restores direct visual embedding behavior. No generator is needed for searching already indexed descriptions; generation loads on demand for indexing and releases its worker after an idle period. Hybrid visual-plus-description retrieval and unrestricted VQA are not part of this release.

## Storage

| Data | Default location |
|---|---|
| Native model | `models\embeddinggemma-2-onnx` beside the application |
| Folders, durable queue and index | `%LOCALAPPDATA%\FuzzyFindAnything` |
| Appearance, similarity and logging preferences | `settings.xml` in the data directory |
| Per-file outcomes and diagnostics | `Logs` in the data directory, unless changed |

The catalog contains paths, snippets and embeddings. Logs can contain filenames and errors. Keep them private.

## Troubleshooting

| Problem | Action |
|---|---|
| No application ZIP | Use a successful build artifact for the required version, not GitHub's source-code archive |
| Missing runtime configuration/DLL | Re-extract the entire package; its native runtime folders must remain together |
| Missing or mismatched model asset | Use the approved ONNX bundle and rerun `Get-Model.ps1 -VerifyOnly` |
| Model asset cannot be replaced | Quit applications using that model; check destination permissions |
| Insufficient memory | Close memory-intensive applications and retry; the index and queue remain intact |
| Office/PDF extraction fails | Read the classified error in `indexer.jsonl`; check file readability and installed x64 IFilters |
| Scanned or protected content | OCR-capable IFilter required for scanned PDFs; encrypted files are not decrypted |
| Online-only cloud file | Make it available locally before indexing |
| Large file seems slow | Check passage progress; saved batches are retained and the full supported content is processed |
| No matches | Confirm successful indexing, change the phrase, or lower similarity |

Use **Configuration > Open folder** for logs. Do not post documents, credentials, unredacted logs or the catalog in public issues.

## Update safely

Use **Quit**, not Close. Extract new binaries into a **separate** folder. Never overwrite an active deployment, remove an active SQLite WAL, or replace model files mapped by a worker.

For migration from the earlier Python-backed version, follow [the upgrade and rollback runbook](docs/MIGRATION.md). It preserves configured folders, vectors, queued passages and XML preferences and keeps a recoverable original deployment.
