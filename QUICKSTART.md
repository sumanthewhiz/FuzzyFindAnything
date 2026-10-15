# Quickstart

**Download the native application, provision its model, and search a small folder.**

Release packages include the .NET and native runtimes. No separate Python, .NET, compiler or package manager is required.

[Project](README.md) · [Architecture](docs/ARCHITECTURE.md) · [Existing-user migration](docs/MIGRATION.md)

## Before you start

| Requirement | Details |
|---|---|
| Windows | Windows 11 x64, or ARM64 with AVX2-capable x64 emulation |
| CPU | At least two physical cores; all app processes share at most half |
| Memory | At least 5 GiB **available** for one model; 8 GiB permits an optional second model |
| Storage | Allow 5 GB for application/model extraction and downloads, plus space for your index |
| Documents | Compatible x64 Office/PDF IFilters must already be installed |
| Network | Needed for downloads, not normal indexing or search |

The ARM64 package runs the UI and model inference natively. Its DiskANN coordinator and document helper run as x64 processes. The release includes the matching native runtimes.

## 1. Download the right package

Open **[Build and release](https://github.com/sumanthewhiz/FuzzyFindAnything/actions/workflows/build.yml?query=branch%3Amain)**, select a successful **main** run for version **1.1.0**, and download the appropriate artifact. Actions downloads require GitHub sign-in and expire.

Published versioned downloads are also listed on the [Releases page](https://github.com/sumanthewhiz/FuzzyFindAnything/releases). Use the application ZIP and quickstart belonging to the same version.

| Your PC | Application ZIP |
|---|---|
| Intel or AMD | `FuzzyFindAnything-v<VERSION>-win-x64.zip` |
| Snapdragon / Windows on ARM | `FuzzyFindAnything-v<VERSION>-win-arm64.zip` |

An Actions artifact may be an outer ZIP containing the application ZIP and checksum. Extract the inner application ZIP completely, for example to:

```text
C:\Users\<you>\Apps\FuzzyFindAnything
```

Keep `engine`, `inference`, `extractor`, `tools`, `third-party`, and `runtime.json` beside the executable. Do not copy the executable out by itself or run it from inside an archive.

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

Wait for **Ready**. The progress bar shows completed startup checkpoints and the current phase, not a time-based estimate. Search becomes usable after the first complete model; optional additional capacity loads in the background.

1. Open **Indexed folders**, select **Add folder**, and choose a small local folder containing a few `.txt` files, source files and photos.
2. Wait for **Indexed**. The details show succeeded/failed totals; the footer shows current processing.
3. Open **Search**, enter a natural-language phrase, and select **Search**.
4. Open a document/code result or an image tile in its usual Windows viewer/editor.

For a predictable first test, save this text in `budget.txt`:

> The team is preparing next quarter's budget, including travel expenses, equipment purchases, and the annual conference.

Search for **planning spending for travel and equipment**. For code, try **function that reads a CSV file** against a matching source file. For photos, describe visible content such as **mountains beside a lake**.

Minimum similarity starts at **0.60**, and slider changes survive restarts. Lower it if results are too narrow. Similarity is a ranking score, not a probability.

## Everyday controls

| Action | Behavior |
|---|---|
| **Clear** / Escape in the search field | Clears the phrase and all three result sections; ignores late responses |
| Check folders, then **Remove selected** | Removes indexing ownership, never original files |
| **Index information** | Shows live counts, backend version, location, size and refresh time |
| **Dark mode** | Switches and saves appearance |
| **Configuration** | Enables/disables logging and selects a log directory |
| Close / Alt+F4 | Minimizes to the taskbar; indexing continues |
| **Quit** | Stops the process tree; committed file/passage progress resumes next time |

The application does not register automatic startup at sign-in. Model-process count is not file-worker count: a 12-core PC can run six file pipelines with one model copy. Hover over CPU status for the separate values.

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
| No application ZIP | Use a successful main build's artifact or the matching versioned release, not GitHub's source-code archive |
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
