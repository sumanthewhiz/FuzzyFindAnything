# FuzzyFindAnything 2.0: binary quickstart

**Download the app, prepare local models, and search a small folder. No source build is required.**

Release packages include the .NET and native runtimes. No separate Python, .NET, compiler or package manager is required.

FuzzyFindAnything 2.0 is authored by **Suman Ghosh**. Read the [chunking guide](docs/CHUNKING.md) for how app-owned boundaries differ from model tokenization, including EmbeddingGemma2.

[Project](README.md) · [Architecture](docs/ARCHITECTURE.md) · [Existing-user migration](docs/MIGRATION.md)

**On this page:** [Download](#1-download-the-right-package) · [Prepare models](#2-prepare-models) · [First search](#3-launch-and-index-a-small-folder) · [Microsoft employees / PSAPI](#microsoft-employees-psapi-models) · [Troubleshooting](#troubleshooting)

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

For a published 2.0 release, use its application ZIP from **[Releases](https://github.com/sumanthewhiz/FuzzyFindAnything/releases)**. If 2.0 is not yet listed, open **[Build and release](https://github.com/sumanthewhiz/FuzzyFindAnything/actions/workflows/build.yml?query=branch%3Amain)**, choose a successful **main** run for version **2.0.0**, and download the appropriate artifact. Actions downloads require GitHub sign-in and are retained for 14 days; this path still needs no build tools.

Use the application ZIP and quickstart belonging to the same version. **Do not download an older release or the automatically generated “Source code” archive** when following these 2.0 instructions.

| Your PC | Application ZIP |
|---|---|
| Intel or AMD | `FuzzyFindAnything-v2.0.0-win-x64.zip` |
| Snapdragon / Windows on ARM | `FuzzyFindAnything-v2.0.0-win-arm64.zip` |

An Actions artifact may be an outer ZIP containing the application ZIP and checksum. Extract the inner application ZIP completely, for example to:

```text
C:\Users\<you>\Apps\FuzzyFindAnything
```

Keep `engine`, `inference`, `hf-inference`, `extractor`, `tools`, `third-party`, and `runtime.json` beside the executable. Do not copy the executable out by itself or run it from inside an archive.

Compare the downloaded file's SHA-256 with the matching `.zip.sha256`:

```powershell
Get-FileHash .\FuzzyFindAnything-v2.0.0-win-arm64.zip -Algorithm SHA256
```

Use your architecture and compare with the matching `.sha256` file. Packages are unsigned; verify their origin and follow your organization's application policy. Read `LICENSE` / `APPLICATION-LICENSE`, `THIRD-PARTY-NOTICES.md` and the original component terms under `third-party` before use or redistribution.

## 2. Prepare models

Choose one setup path. **A: public starter models** does not require a private native bundle. **B: the default EmbeddingGemma2 bundle** retains the app's built-in default. Both run inference locally after preparation.

Models are separate downloads governed by their creators' licenses and terms, not by the app's MIT license. Review the [model licensing guidance](THIRD-PARTY-NOTICES.md#hugging-face-and-other-models) first.

### A. Public starter models: MiniLM for text/code, CLIP for images

Use this on a **new installation only**. Open PowerShell in the extracted application directory and run the block below. It uses the packaged native utility; no .NET SDK, Python, Git or compiler is needed.

```powershell
$Data = Join-Path $env:LOCALAPPDATA 'FuzzyFindAnything'
$Settings = Join-Path $Data 'settings.xml'
if ((Test-Path $Settings) -or (Test-Path (Join-Path $Data 'catalog.sqlite3'))) {
    throw 'Existing application data found. Keep it and use Model configuration, or choose a new trial data directory.'
}
$Cache = Join-Path $Data 'models'
$DefaultBundle = Join-Path $PWD 'models\embeddinggemma-2-onnx'
$TextModel = 'https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2'
$ImageModel = 'https://huggingface.co/Xenova/clip-vit-base-patch32'
foreach ($Url in @($TextModel, $ImageModel)) {
    & .\tools\FuzzyFindAnything.Tools.exe prepare-hf-model --cache $Cache --default-model $DefaultBundle --url $Url
    if ($LASTEXITCODE -ne 0) { throw 'Model preparation failed. Do not continue until the reported error is resolved.' }
}
$Configuration = @"
<FuzzyFindAnythingConfiguration version="3">
  <Models>
    <Text modelUrl="$TextModel" />
    <Code modelUrl="$TextModel" />
    <Image modelUrl="$ImageModel" />
  </Models>
</FuzzyFindAnythingConfiguration>
"@
[IO.File]::WriteAllText($Settings, $Configuration, [Text.UTF8Encoding]::new($false))
& .\FuzzyFindAnything.exe --data-dir $Data
```

The utility resolves and checksums the compatible ONNX exports. The missing `$DefaultBundle` directory is harmless for this explicit HF-only configuration; it is not downloaded or used. MiniLM is a lightweight general-text encoder that also accepts code, not a code-specialized model.

Keep the same data directory on later launches. With the default path above, opening `FuzzyFindAnything.exe` normally restores it. If you changed `$Data` for a trial, continue passing `--data-dir` for that trial.

CLIP scores differ from text-model scores. If image results are too narrow, lower **Images > Minimum** explicitly (for example, to 0.20); the app does not change thresholds silently.

### B. Default EmbeddingGemma2 native bundle

The app's built-in default uses the separately packaged **`embeddinggemma-2-onnx-v1`** export of `google/embeddinggemma-2`. The original Hugging Face `model.safetensors` file is **not** a substitute.

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
& .\tools\FuzzyFindAnything.Tools.exe acquire-model --destination .\models\embeddinggemma-2-onnx --source-directory 'D:\ApprovedModels\embeddinggemma-2-onnx-v1'
if ($LASTEXITCODE -ne 0) { throw 'Model acquisition failed.' }
& .\tools\FuzzyFindAnything.Tools.exe verify-model --destination .\models\embeddinggemma-2-onnx
if ($LASTEXITCODE -ne 0) { throw 'Model verification failed.' }
```

The first command copies verified assets to `models\embeddinggemma-2-onnx` beside the application. The second performs a **read-only size and SHA-256 check of every asset**. Neither requires an interpreter or an SDK.

If an approved HTTPS directory hosting the manifest's individual files is available, use its base URL instead:

```powershell
& .\tools\FuzzyFindAnything.Tools.exe acquire-model --destination .\models\embeddinggemma-2-onnx --base-uri 'https://YOUR-APPROVED-HOST/embeddinggemma-2-onnx-v1/'
```

Replace the example host with an actual approved location. Rerun the same command after an interruption to resume. Downloads are size-bounded, checksummed and staged before replacement; assets in use by native inference cannot be overwritten.

If script policy blocks an unsigned script, follow your device's application policy. Do not disable organization-enforced controls. Model acquisition errors are reported explicitly; a missing bundle is not downloaded from an unverified alternative.
The included `Get-Model.ps1` provides the same acquisition/verification workflow for PowerShell 7 users. Existing users should keep their saved model bindings rather than overwrite `settings.xml`.

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
| Tray icon / **Open FuzzyFindAnything 2.0** | Restores the existing window maximized |
| **Quit**, in the window or tray menu | Stops the process tree; committed file/passage progress resumes next time |

The application does not register automatic startup at sign-in. Model-process count is not file-worker count: a 12-core PC can run six file pipelines with one model copy. Hover over CPU status for the separate values.

If the tray icon is not immediately visible, open Windows' notification-area overflow menu beside the clock.

## Change embedding models

Open **Model configuration** and enter HTTPS model-card URLs independently for **Documents / text**, **Source code** and **Images**. The built-in default is `https://huggingface.co/google/embeddinggemma-2`; the public starter setup instead shows its prepared MiniLM/CLIP selections. Existing saved choices are retained.

**Refresh model sources** checks optional local providers without changing applied models. Microsoft employees should follow [the PSAPI setup](#microsoft-employees-psapi-models) below. Other users retain the Hugging Face workflow.

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

Once model assets are prepared, tokenizer and inference settings are resolved automatically. Hugging Face selections use a model-card URL independently for each role. Requirements are resolved automatically without downloading weights; **Model details** appears only where applicable, and approved query-purpose choices remain inside it unless a recipe genuinely requires a choice. Tokenizer, pooling, graph and image-pipeline setup are not user configuration fields. Switching back to a simple model discards inactive query choices.

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

## Microsoft employees: PSAPI models

This is optional. Public users can use Hugging Face models without the internal companion. Complete one of the initial model setups above before switching providers.

### Prerequisites

- A Microsoft GitHub enterprise work account that can read the **Internal** [employee companion channel](https://github.com/experiences-and-devices/APT/releases/tag/ffa-microsoft-provider-v1.5.0-internal.3). Personal-repository collaborator invitations are not needed.
- Access to the approved model registry using your corporate identity and the required corporate network/VPN where applicable. GitHub access alone does not grant model access.
- The GitHub CLI and the native Azure Artifacts Credential Provider. These are optional authentication prerequisites, not app build tools.

Install the GitHub CLI through your approved software channel, or with Windows Package Manager:

```powershell
winget install --id GitHub.cli --exact
gh auth login --hostname github.com --git-protocol https --web
gh auth status --hostname github.com
gh api repos/experiences-and-devices/APT --jq .visibility
```

Sign in with your **work** account and complete Microsoft enterprise SSO. The last command should return `internal`. If you have multiple accounts, select the authorized work login with `gh auth switch --hostname github.com --user YOUR_WORK_LOGIN`.

Install the self-contained credential provider using Microsoft's [official Windows instructions](https://github.com/microsoft/artifacts-credprovider#installation-on-windows). For a reproducible no-SDK installation, download and review the official helper, then request the matching Windows runtime:

```powershell
$Installer = Join-Path $env:TEMP 'ffa-install-artifacts-credprovider.ps1'
Invoke-WebRequest 'https://aka.ms/install-artifacts-credprovider.ps1' -OutFile $Installer
# Review the downloaded script and follow your organization's execution policy.
& $Installer -RuntimeIdentifier 'win-arm64'  # use 'win-x64' for Intel/AMD
Test-Path "$env:USERPROFILE\.nuget\plugins\netcore\CredentialProvider.Microsoft\CredentialProvider.Microsoft.exe"
```

The check should return `True`. Keep an existing working installation; do not overwrite it unnecessarily. The self-contained variant avoids requiring a separate .NET SDK or shared runtime. The companion expects this native plugin location, not only the unrelated `dotnet tool` shim.

### Select and apply models

1. Open **Model configuration** and choose **Refresh model sources**. The app retrieves the pinned companion package if it is absent, using your existing GitHub CLI authentication.
2. Complete any corporate registry sign-in request. Microsoft appears as a source only after live registry access is verified. If it stays hidden, check the prerequisites and the source-discovery/log message; do not edit private feed URLs or copy credentials into settings.
3. Choose **Microsoft (PSAPI)** independently for Documents/text, Code or Images. Choose a catalog-listed model **and its CPU/NPU/GPU variant**.
4. Expand **PSAPI drop version** to inspect or refresh compatible SDK versions. Use the provider's recommended default unless you need another qualified drop. Existing applied versions are retained; a refreshed catalog does not silently change them.
5. Select **Apply model changes**. Native execution and memory checks must succeed before affected files are reindexed. Unchanged roles retain their vectors.

Model names, roles and available device variants come from the caller's authenticated catalog and detected hardware. Being ARM64 does not alone qualify an NPU profile. An explicitly selected unavailable device is rejected rather than silently replaced. Direct paired image embeddings do not expose caption/OCR/region-generation options.

The companion is installed separately under `%LOCALAPPDATA%\FuzzyFindAnythingProviders`; private SDK/model payloads are not included in the public app ZIP. Downloads are pinned by GitHub host, internal repository identity, release/tag, version and archive checksum. Registry availability is checked separately. On access loss, only Hugging Face is offered for new selections; saved bindings and local data are retained.

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

Follow [the upgrade and rollback runbook](docs/MIGRATION.md) when replacing an existing installation. It preserves configured folders, vectors, queued passages and XML preferences and keeps a recoverable original deployment.
