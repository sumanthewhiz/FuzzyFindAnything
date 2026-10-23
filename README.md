# Fuzzy Find Anything

**Find documents, source code and images by meaning, on your Windows PC.**

This checkout is **1.5.0-dev.1**, adding region-based image retrieval, larger Florence-2 profiles and an opt-in internal Microsoft model-provider integration to the extensible native model pipelines. Earlier release packages do not contain these additions.

[![Build and release](https://github.com/sumanthewhiz/FuzzyFindAnything/actions/workflows/build.yml/badge.svg?branch=dev)](https://github.com/sumanthewhiz/FuzzyFindAnything/actions/workflows/build.yml)
![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4)
![.NET 10](https://img.shields.io/badge/runtime-.NET%2010-512BD4)

[**Quickstart**](QUICKSTART.md) ·
[**Download builds**](https://github.com/sumanthewhiz/FuzzyFindAnything/actions/workflows/build.yml?query=branch%3Adev) ·
[Architecture](docs/ARCHITECTURE.md) ·
[Chunking](docs/CHUNKING.md) ·
[Upgrade and rollback](docs/MIGRATION.md) ·
[Report an issue](https://github.com/sumanthewhiz/FuzzyFindAnything/issues)

Fuzzy Find Anything is a native WPF desktop application for finding files when you remember their contents rather than their names. Describe a topic, code behavior or image in natural language, search a persistent local vector index, and open matches in their registered Windows applications.

The application uses .NET and native Windows libraries, with ONNX Runtime for local EmbeddingGemma 2 inference and Microsoft DiskANN for vector search. Model assets are acquired separately; see the [model provisioning instructions](QUICKSTART.md#2-provision-the-native-model).

## What it provides

| Capability | Behavior |
|---|---|
| Semantic retrieval | EmbeddingGemma 2 is the default; compatible document spaces share indexes while query roles remain explicit |
| Three result sections | Openable document and code lists, plus a clickable image thumbnail gallery |
| Per-type search | Select documents, code and/or images with independent saved similarity thresholds; at least one type stays selected |
| Content-aware chunks | Document sections/paragraphs and native syntax-aware code units, with model-token limits and source provenance |
| Matched-chunk previews | Hover at the bottom of a document/code match to inspect the actual retained chunk and source location |
| Explicit indexing scope | Select folders and ordinary subfolders; removing scope never deletes source files |
| Background updates | NTFS/ReFS USN replay, directory notifications, and conservative reconciliation |
| Changes while the app is closed | Background timestamp/size/identity checks on restart update only added, changed or deleted files |
| Parallel indexing | Independent file pipelines and bounded cross-file inference batches |
| Enforced CPU ceiling | The entire process tree shares at most half the physical cores, even when other cores are idle |
| Durable progress | Completed files and committed passage batches survive crashes and normal exits |
| Startup visibility | Sequenced percentage/phase checkpoints; the first ready model enables search |
| Live index information | Backend/version, location, size, file/document/code/image counts and refresh time |
| Model configuration | Independent Hugging Face or approved Microsoft sources per file type; automatic model-specific preparation and conditional controls |
| Internal Microsoft models | A separately installed provider supplies version-aware fixed model choices, native tokenization and actual local device qualification; private payloads are not bundled |
| Image understanding | Reviewed caption/OCR providers feed a linked or separate text embedder; completed generation stages are durable and reusable |
| Regional image search | Qualified detectors/describers embed each region's text; gallery matches identify and highlight the best matching region |
| Polished desktop controls | Light/dark appearance, persisted similarity, search clearing, configurable logs and keyboard access |

### Supported content

| Category | Formats |
|---|---|
| Documents | `.txt`, `.rtf`, `.pdf`, `.doc`, `.docx`, `.ppt`, `.pptx`, `.xls`, `.xlsx` |
| Images | `.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, `.ico` |
| Source code | Python, JavaScript/TypeScript, Java, C/C++, Go, Rust, SQL, Bash, C#, PHP, Ruby, Swift, Kotlin and R |

Text and code are read directly; source code is **never executed**. Other document formats use installed Windows/Office/PDF IFilters, including registered and Office Click-to-Run filters. Images use direct visual embeddings or a reviewed image-to-text-to-embedding pipeline, never filename substitutes for failed inference. See [format details and limitations](docs/ARCHITECTURE.md#supported-content).

## Get started

1. Download a native package matching your PC, and extract the **complete** ZIP.
2. Acquire the approved, pre-exported ONNX model bundle with the included `Get-Model.ps1`.
3. Open `FuzzyFindAnything.exe`, add a small folder, and try a natural-language search.

A release includes .NET, ONNX Runtime, SQLite, DiskANN and architecture-matched app-local C++ runtimes. Recipients do not need an SDK, Python, pip, Git or Visual Studio. Model assets occupy approximately **1.8 GB uncompressed** and are intentionally separate from the application.

The [quickstart](QUICKSTART.md) includes exact download, verification, model and first-search steps. Existing users should read the [migration runbook](docs/MIGRATION.md) before switching deployments.

## Desktop experience

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/search-dark.png">
  <img alt="Fuzzy Find Anything showing separate document, code and image results" src="docs/images/search-light.png">
</picture>

*The actual application layout, rendered with synthetic demonstration data.*

| Tab | Purpose |
|---|---|
| **Search** | Enter a phrase; inspect document, code and image matches; adjust saved similarity; clear results |
| **Indexed folders** | Add roots, review concise succeeded/failed counts, and remove checked folders |
| **Index information** | Inspect continuously refreshed index metadata |
| **Configuration** | Enable/disable logging and choose its destination |
| **Model configuration** | Apply compatible local embedding models and inspect their indexes and execution devices |

The window opens maximized. Minimizing or closing it hides it in the **system tray** while indexing continues, without leaving a taskbar button. Click the tray icon or choose **Open Fuzzy Find Anything** from its menu to restore the window; **Quit** in either the window or tray menu stops the application. Windows may place the icon in the notification-area overflow menu. The app does not install a service or register automatic startup at sign-in.

## Architecture

```mermaid
flowchart LR
    UI["WPF / .NET desktop"] <-->|Local JSON pipes| Engine[".NET indexing coordinator"]
    Changes["USN + directory notifications"] --> Engine
    Engine <-->|Bounded requests| Inference[".NET + ONNX Runtime"]
    Engine --> Filters["Isolated x64 IFilter helper"]
    Engine <--> Catalog[("SQLite catalog + durable queue")]
    Engine <--> ANN["Native Microsoft DiskANN"]
    UI --> Shell["Windows thumbnails + file associations"]
```

The ARM64 package uses **native ARM64 UI and inference**, with an **x64 coordinator, DiskANN and IFilter helper**. The current C++ DiskANN/MKL implementation requires this deliberate process boundary; no mixed-architecture DLLs are loaded into one process. The x64 package uses x64 throughout.

EmbeddingGemma2 remains the pre-filled default for every file type, with no tokenizer or pipeline setup. The **Model configuration** tab resolves reviewed native recipes for text/code embeddings and retains direct CLIP image support. Query instructions, tokenization, pooling, tensor interfaces and device qualification are part of the recipe, not arbitrary downloaded model code. Unsupported families, missing exports, text-only image assignments and unusable device profiles are rejected before an index is changed.

Examples include `Qwen/Qwen3-Embedding-0.6B`, `intfloat/e5-base-v2`, `vectoriseai/e5-small-v2`, the existing MiniLM examples and `Xenova/clip-vit-base-patch32`. The Qwen card resolves to a pinned stateless Qdrant ONNX export; the UI discloses the actual artifact source. Qwen Coder generation models are not interchangeable with text embedding checkpoints.

For image understanding, `microsoft/Florence-2-base-ft`, `microsoft/Florence-2-base`, `microsoft/Florence-2-large-ft` and `microsoft/Florence-2-large` use the reusable native encoder-decoder recipe. Captions, optional OCR and optional **Object labels** (`<OD>`) or **Region descriptions** (`<DENSE_REGION_CAPTION>`) feed the selected text embedder. Region-only indexing is also supported. Each region's label/description is embedded separately; the gallery returns the original image once, with its best matching region highlighted and the matching text available on hover.

The image recipe resolves its tokenizer, processor, graph bindings and task prompts automatically. Region controls appear only for a qualifying image model; direct Gemma/CLIP embeddings do not acquire a generation pipeline. Other compatible checkpoints can use reviewed recipe data; different architectures or output formats still require a native adapter/postprocessor and qualification. This is not unrestricted support for every vision model, and generated labels are not verified facts or confidence scores.

The compatible-model host uses self-contained Windows ML to discover usable CPU, GPU and NPU execution providers. Acceptance depends on hardware, driver/provider, resource limits and real inference, not a blanket accelerator restriction. Query-only changes retain file embeddings; accepted document-model changes rebuild only affected file types. See [model configuration and device checks](docs/ARCHITECTURE.md#configurable-models-and-device-checks).

For an authorized internal installation, each role can instead select **Microsoft (PSAPI)**. The provider supplies a fixed model list for the selected SDK drop; **PSAPI drop version / Refresh available versions** queries its approved feed. Hugging Face selections retain the model-card URL field. Native query/document purposes and matching tokenizers are automatic, and direct Microsoft image embeddings do not expose caption/region-generation controls. Model/package availability and detected hardware determine the offered choices; Apply still runs real native execution checks. An unavailable model is not silently replaced when the drop changes.

The internal provider is registered separately under `%LOCALAPPDATA%\FuzzyFindAnythingProviders`. It is not included in the public application, source checkout or release ZIPs. Without it, Hugging Face behavior and defaults remain unchanged, and selecting Microsoft gives an explicit installation diagnostic. Feed authentication uses the native .NET Azure Artifacts credential provider, not Azure CLI or Python. See the provider's internal installation instructions for the approved SDK/model channel.

The active mutable DiskANN graph resides in memory and is checkpointed to disk. SQLite retains vectors, folder ownership, source revisions and passage progress so a damaged or stale graph can recover without repeating committed inference. It is not an out-of-core index.

On a 12-core PC, six file pipelines can overlap reading, extraction and preparation even when only one model fits in memory. Text requests coalesce into batches of up to eight passages; up to two images are scheduled together using separate, valid single-image vision calls. The status display distinguishes file workers, model processes, compute threads and batch size. Eligible cores are a ceiling, not a promise of constant CPU utilization.

## Privacy and operational safety

- Inference and search run locally. Source files and queries are not sent to model services.
- Only configured folders define indexing scope. Source content is read, not moved, edited or deleted.
- Interrupted work resumes at the last durable passage commit. An interrupted, uncommitted native operation may run again.
- Repeated notifications and parent-directory changes do not restart unchanged jobs.
- Every restart checks configured folders and subfolders in the background for changes made while the app was stopped. Search becomes available when the model is ready, without waiting for that check or the incremental indexing queue.
- Content errors, unavailable dependencies and temporary failures have explicit diagnostics and retry policies.
- The index contains paths, snippets and embeddings; logs contain paths and error details. Protect both with Windows permissions and disk encryption.

Process isolation is fault containment, not a security sandbox. Installed IFilters and registered viewers remain part of the machine's trust boundary.

## Build from source

Use **PowerShell 7**, Windows 11, the **.NET 10 SDK**, Git and Visual Studio 2022 or later with **Desktop development with C++**, CMake and a Windows SDK. ARM64 packages also need the ARM64 C++ redistributable files supplied with the corresponding MSVC tools.

```powershell
git clone --branch dev https://github.com/sumanthewhiz/FuzzyFindAnything.git
Set-Location .\FuzzyFindAnything
.\Setup.ps1 -SkipModel
.\Get-Model.ps1 -SourceDirectory 'D:\ApprovedModels\embeddinggemma-2-onnx-v1'
.\Build.ps1 -SkipSetup
& .\artifacts\FuzzyFindAnything\FuzzyFindAnything.exe
```

Setup builds the pinned DiskANN source and native utilities; NuGet restores managed dependencies. No global interpreter environment is installed or modified. Models, downloads, build output and personal data are excluded from Git.

### Verification and distribution

```powershell
.\tests\RunChecks.ps1
.\Build-Release.ps1 -SkipSetup -RuntimeIdentifier win-x64
.\Build-Release.ps1 -SkipSetup -RuntimeIdentifier win-arm64
```

The [build workflow](.github/workflows/build.yml) produces self-contained architecture-specific ZIPs and SHA-256 sidecars. Release verification checks the complete file inventory, architecture boundaries, relative runtime paths, model/user-data exclusion, and absence of interpreter payloads. Relocated native smoke tests run with Python absent from `PATH`.

Pull requests and branch builds produce temporary Actions artifacts. A matching version tag publishes release downloads. Model bundles are prepared separately and are **not automatically uploaded**.

Packages are unsigned. Checksums detect altered downloads but do not replace code signing or publisher verification.

## Requirements and limits

- Windows 11, at least two physical cores, and an AVX2-capable x64 CPU or Windows ARM64 with compatible x64 emulation.
- At least **5 GiB available RAM** for one model; approximately **8 GiB available** permits two. These are planning estimates, not quotas. Large indexes need additional memory; 16 GB system RAM or more is recommended.
- Florence-2 Large profiles require approximately **3.07 GiB of downloaded graph assets** and a **6.5 GiB generator memory allowance plus 2 GiB system reserve**, in addition to the selected text embedder and other application memory. Admission is checked before loading; download size is not peak RAM.
- Scanned PDFs need an OCR-capable installed IFilter. Protected documents are not decrypted.
- Online-only files must be available locally. Junctions and symbolic links are not traversed.
- Installed filters determine document coverage. Some Excel filters omit inline-string cells in otherwise valid workbooks.
- Large workbooks and image collections remain compute-intensive. All distinct supported passages are processed; content is not silently sampled.

## Documentation and support

| Reference | Contents |
|---|---|
| [Quickstart](QUICKSTART.md) | Packages, native model provisioning and a first search |
| [Architecture and operations](docs/ARCHITECTURE.md) | Process boundaries, model contract, queue, monitoring, settings, logs and maintenance |
| [Migration and rollback](docs/MIGRATION.md) | Preserving existing scope, vectors, queued work and preferences |
| [Validation](docs/VALIDATION.md) | Acceptance commands, evidence and reproducible test coverage |

For issues, include the version, Windows/CPU architecture and relevant error text. **Do not upload private documents, unredacted logs, credentials or your catalog.**

## Licensing and attribution

Copyright © 2026 **Suman Ghosh**.

An open-source license for the application's own source has not been designated. Dependency licenses do not license the application. Packages retain component notices and a versioned inventory. EmbeddingGemma 2 and its model card/terms are acquired separately; Office/PDF filters are not redistributed.

[LinkedIn](https://www.linkedin.com/in/ghoshsuman/) · [GitHub](https://github.com/sumanthewhiz)
