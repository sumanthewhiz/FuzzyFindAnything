# Quickstart

**Download FuzzyFindAnything, add a folder, and search by meaning.**

[Overview](README.md) · [Model compatibility](docs/MODEL-COMPATIBILITY.md) · [Upgrade and rollback](docs/MIGRATION.md) · [Model terms](THIRD-PARTY-NOTICES.md#hugging-face-and-other-models)

The application package includes the .NET and native runtimes. End users do not need Python, Node.js, a development SDK, or a model conversion tool.

## Before you start

| Requirement | Details |
|---|---|
| Windows | Windows 11 x64, or ARM64 with compatible AVX2-capable x64 emulation |
| CPU | At least two physical cores |
| Memory | At least 5 GiB available for the default model, with additional space for the index and other applications |
| Storage | Allow 5 GB for application files, model downloads and extraction, plus index space |
| Documents | Compatible installed x64 Office/PDF IFilters; plain text and source code need no IFilter |
| Network | Required for first-time model acquisition; a completed cache supports offline search |

On ARM64, the interface and inference run natively while the coordinator, DiskANN and document helper use x64 compatibility. The package includes the matching native libraries.

## 1. Download and extract

Use [Releases](https://github.com/sumanthewhiz/FuzzyFindAnything/releases) for published versions. Verified branch builds are available under [Build and release](https://github.com/sumanthewhiz/FuzzyFindAnything/actions/workflows/build.yml?query=branch%3Adev); `dev` builds contain changes awaiting release. Choose a **successful** run and check its version and source branch. Actions downloads require GitHub sign-in and are retained for 14 days.

| PC type | Application download |
|---|---|
| Intel or AMD | `FuzzyFindAnything-v2.0.0-win-x64.zip` |
| Windows on ARM | `FuzzyFindAnything-v2.0.0-win-arm64.zip` |

Download the matching `.zip.sha256` file separately to verify integrity. The application download is **one versioned ZIP containing the application files directly**. It is not a ZIP containing another ZIP. GitHub's automatically generated **Source code** downloads are not application packages.

In the download directory, calculate the archive's SHA-256:

```powershell
Get-FileHash .\FuzzyFindAnything-v2.0.0-win-arm64.zip -Algorithm SHA256
```

Use your architecture's filename and compare the result with the first value in its `.sha256` file. Packages are currently unsigned; a checksum verifies bytes, not publisher identity or code signing.

Extract the entire ZIP into an application folder, for example:

```text
C:\Users\<you>\Apps\FuzzyFindAnything
```

Keep the executable, `runtime.json`, `engine`, `inference`, `hf-inference`, `extractor`, `tools`, documentation and `third-party` files together. Do not copy only the executable or launch it from inside the archive.

## 2. First launch

Open **FuzzyFindAnything.exe**. The window opens maximized and automatically prepares:

```text
https://huggingface.co/onnx-community/embeddinggemma-2-ONNX
```

This is the default for Documents/text, Source code and Images. First launch downloads approximately 1.8 GB of model assets, verifies them and checks native execution. The status shows preparation phases and download progress. No model URL edit or **Apply** click is required.

The model is stored under `%LOCALAPPDATA%\FuzzyFindAnything\models`, not beside the application. A `--data-dir` override also relocates the cache. Completed assets are reused offline; the application folder need not be writable for public-model acquisition.

If preparation fails, resolve the reported problem and retry the unchanged selection with **Apply model changes**. **Cancel model preparation** retains the applied configuration and resumable download progress. The app does not silently substitute another model or device.

Models retain their own [upstream licenses and terms](THIRD-PARTY-NOTICES.md#hugging-face-and-other-models). The package's `public-model-manifest.json` identifies the pinned export and supplemental tokenizer source. Existing installations retain initialized model bindings; see [Migration](docs/MIGRATION.md) for compatibility and first-launch recovery.

## 3. Add a folder

1. Open **Indexed folders** and select **Add folder**.
2. Choose a local folder containing a few documents, source files or images.
3. Wait for **Indexed**, or inspect any per-file errors shown in the folder details.

No folders are added automatically. Each configured root includes its ordinary subfolders. Junctions and symbolic links are not traversed. Removing selected folders removes their index ownership, never the source files; files still covered by another configured root remain indexed.

## 4. Search

Enter a natural-language phrase in **Search** and select **Search**. Document and code matches appear as lists; images appear as a gallery. Select a result to open the original file in its registered Windows viewer or editor.

For a simple first test, save this text in an indexed file named `budget.txt`:

> The team is preparing next quarter's budget, including travel expenses, equipment purchases, and the annual conference.

Search for **planning spending for travel and equipment**. For code, try **function that reads a CSV file** against a matching source file. Describe visible image content, such as **mountains beside a lake**.

Documents, Code and Images are initially selected. Each has an independent minimum similarity, initially **0.60**. Adjust only the relevant type when results are too broad or too narrow. Similarity is a score, not a probability, and thresholds are not interchangeable between models. The final selected search type cannot be unchecked.

Hover over **Matched chunk** on a document/code result to read its full matching source span, with symbol and line information where available. Retained older vectors may show **Matched excerpt** instead. Document line numbers refer to extracted text, not original PDF or Office page numbers.

## Everyday controls

| Action | Behavior |
|---|---|
| **Clear** or Escape in the search box | Clears the phrase and all result sections, without resetting thresholds |
| **Add folder** / **Remove selected** | Changes the indexing scope without changing original files |
| **Index information** | Shows live file counts, backend version, location, size and refresh time |
| **Dark mode** | Changes and saves appearance |
| **Configuration** | Controls logging and its destination |
| Minimize, Close or Alt+F4 | Hides the window in the system tray while indexing continues |
| Tray icon or **Open FuzzyFindAnything 2.0** | Restores the existing window maximized |
| **Quit** in the window or tray menu | Stops the process tree; committed progress resumes next time |

The tray icon may be in Windows' notification-area overflow menu. The application does not register automatic startup at sign-in.

After a restart, configured folders are checked for changes made while the app was stopped. Existing-index search remains available during background reconciliation and incremental indexing. The CPU indicator distinguishes file pipelines from resident model workers; these are separate limits.

## Change embedding models

Use the [model compatibility reference](docs/MODEL-COMPATIBILITY.md) for exact URLs, supported roles and qualified limits. In **Model configuration**, select a supported model independently for Documents/text, Source code and Images, then select **Apply model changes**.

The app acquires missing supported assets and eligible explicitly requested execution-provider runtimes, then validates modality, available memory and native execution before committing the change. Healthy unchanged selections are a no-op; an unchanged selection after startup failure retries preparation.

| Selection | Typical role |
|---|---|
| Public EmbeddingGemma2 ONNX | Documents, code and direct image embeddings |
| Registered Qwen3-Embedding-0.6B | Document/code retrieval with role-specific query instructions |
| Registered E5-base-v2 / E5-small-v2 | Text retrieval with automatic query/passage prefixes |
| Compatible BERT/MiniLM exports | Text and code-as-text embeddings |
| Compatible CLIP exports | Direct image embeddings with matching text queries |
| Registered Florence-2 checkpoints | Caption, OCR and region-description search through a text embedder |

Tokenizer, pooling, graph and preprocessing settings are resolved by supported recipes, not entered manually. Conditional query-purpose controls appear only where applicable. A model card alone does not establish compatibility, and a coding-language generator is not a substitute for an embedding model.

Changing a document-vector or chunking contract reindexes only its dependent content. Query-purpose-only changes can retain existing document vectors. Failed or cancelled preparation leaves the previous configuration usable. After a successful change, affected results may be incomplete while background reindexing finishes.

Search can continue during preparation, though memory admission may briefly delay a query. An explicitly selected device must pass its own execution checks; GPU/NPU eligibility is not inferred solely from the processor or package architecture.

### Image descriptions, OCR and regions

Choose a supported Florence-2 URL under **Images**. The app adds an image-description stage followed by the linked Documents/text embedder. Caption is the default task; enable OCR, object labels or region descriptions as needed. To use a different text embedder, clear **Use the Documents / text model** and choose a supported alternative.

Changing the linked text model also re-embeds generated image descriptions, without regenerating valid cached captions/OCR. Region search returns the original image with its best matching box and text, not a separate cropped-image vector.

Valid partial results remain searchable with warnings. **Retry incomplete image tasks** retries failed or truncated stages without discarding successful sibling tasks. OCR that finds no text is a skipped result, not a fabricated embedding. Generated descriptions may omit or misinterpret content; semantic OCR search is not exact literal text matching.

Selecting a direct Gemma2 or CLIP model hides the description-specific controls. Existing descriptions can be searched without loading their generator; generation workers load as needed for indexing. Hybrid visual-plus-description retrieval and unrestricted visual question answering are not provided.

## Storage and updates

| Data | Default location |
|---|---|
| Application and bundled runtimes | The extracted application folder |
| Folder scope, durable queue and index | `%LOCALAPPDATA%\FuzzyFindAnything` |
| Public/compatible model cache | `models` under the selected data directory |
| Appearance, search, model and logging preferences | `settings.xml` in the data directory |
| Per-file outcomes and diagnostics | `Logs` in the data directory, unless changed |
| Optional Microsoft companion | `%LOCALAPPDATA%\FuzzyFindAnythingProviders` |
| Initialized legacy custom-export bundle | The `model` location in `runtime.json`; not required by the public default |

The index contains paths, content-derived text and embeddings. Logs may contain filenames and errors. Keep them private.

For a separate application profile:

```powershell
& .\FuzzyFindAnything.exe --data-dir "$env:LOCALAPPDATA\FuzzyFindAnything-Profile"
```

Use that argument on subsequent launches of the same profile. Before an application upgrade, use **Quit**, back up the complete data directory, and extract the new ZIP into a separate folder. Follow [Upgrade and rollback](docs/MIGRATION.md); do not overwrite an active deployment or delete an active SQLite WAL.

## Troubleshooting

| Symptom | Action |
|---|---|
| Wrong download or an outer ZIP from an older workflow run | Select the current successful run's exact versioned application ZIP, not the source-code archive |
| Missing runtime configuration or DLL | Re-extract the full application package; keep its directories together |
| Public-model download or verification failure | Check connectivity and cache permissions, then retry the unchanged selection; valid partial downloads are retained |
| Missing legacy custom-export assets | Follow [legacy bundle repair](docs/MIGRATION.md#legacy-model-bundles), not the public first-launch instructions |
| Model files cannot be replaced | Quit applications using that model and check destination permissions |
| Insufficient memory | Free memory and retry; the index and queue are retained |
| Office/PDF extraction failure | Check the classified error in `indexer.jsonl`, source readability and installed x64 IFilters |
| Scanned or protected content | Scanned PDFs need an OCR-capable filter; encrypted documents are not decrypted |
| Online-only cloud file | Make the file available locally before indexing |
| Large file is slow | Inspect passage progress; committed batches are retained while supported content is processed |
| No matching files | Confirm successful indexing, change the phrase, or adjust that type's similarity threshold |

Use **Configuration > Open folder** to inspect logs. For unresolved problems, open an [issue](https://github.com/sumanthewhiz/FuzzyFindAnything/issues) with the app version, architecture and a minimal synthetic example. Do not post source documents, credentials, unredacted logs or the catalog.

## Microsoft employees: PSAPI models

This optional provider is separate from the public Hugging Face workflow. It requires both authorized GitHub enterprise access and model-registry access. Public packages do not contain the companion or its private SDK/model payloads.

### Prerequisites

- An authorized Microsoft work account with access to the **Internal** [employee companion channel](https://github.com/experiences-and-devices/APT/releases/tag/ffa-microsoft-provider-v1.5.0-internal.3).
- Corporate identity and network/VPN access to the approved model registry where required. GitHub access alone does not grant model access.
- GitHub CLI and the native Azure Artifacts Credential Provider.

Install GitHub CLI through the approved software channel, or with Windows Package Manager:

```powershell
winget install --id GitHub.cli --exact
gh auth login --hostname github.com --git-protocol https --web
gh auth status --hostname github.com
gh api repos/experiences-and-devices/APT --jq .visibility
```

Sign in with the authorized **work** account and complete enterprise SSO. The last command should return `internal`. With multiple accounts, select the work login using `gh auth switch --hostname github.com --user YOUR_WORK_LOGIN`.

Install the self-contained credential provider using Microsoft's [Windows instructions](https://github.com/microsoft/artifacts-credprovider#installation-on-windows). The following uses the official helper without requiring a development SDK:

```powershell
$Installer = Join-Path $env:TEMP 'ffa-install-artifacts-credprovider.ps1'
Invoke-WebRequest 'https://aka.ms/install-artifacts-credprovider.ps1' -OutFile $Installer
# Review the helper and follow the device's execution policy before running it.
& $Installer -RuntimeIdentifier 'win-arm64'  # Use win-x64 for Intel/AMD.
Test-Path "$env:USERPROFILE\.nuget\plugins\netcore\CredentialProvider.Microsoft\CredentialProvider.Microsoft.exe"
```

The check should return `True`. Retain an existing working installation. The native plugin location is required, not only a `dotnet tool` shim.

### Select and apply

1. Open **Model configuration > Refresh model sources**. The app acquires the pinned companion when absent using the existing authorized GitHub authentication.
2. Complete any model-registry sign-in. Microsoft becomes available only after live access is verified. If discovery fails, inspect the source status and logs; do not copy credentials or edit private feed URLs.
3. Select **Microsoft (PSAPI)** for the desired roles and choose a catalog-listed model and device variant.
4. Use **PSAPI drop version** when a different qualified SDK drop is required. Refreshing the catalogue does not silently change applied versions.
5. Apply the selection and wait for native execution checks and any dependent reindexing.

Model names and device variants come from the caller's authorized catalogue and detected hardware. An unavailable explicit device is rejected. Direct paired image embeddings do not expose caption/OCR generation controls. On access loss, saved bindings and local data are retained even when only Hugging Face is offered for new selections.

See [model and component terms](THIRD-PARTY-NOTICES.md) before using or redistributing any third-party artifacts.
