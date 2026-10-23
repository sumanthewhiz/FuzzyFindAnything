# Native validation

[Overview](../README.md) · [Architecture](ARCHITECTURE.md) · [Migration](MIGRATION.md)

## Release 1.1.0 acceptance

Version **1.1.0** was rebuilt from `main` after consolidating the application changes. The runtime, native bridge, model contract and build implementation match the validated native baseline at `33c3635`; this promotion changes release metadata and documentation, not indexing behavior.

Fresh acceptance covered:

- A clean native DiskANN build, the .NET solution, formatting, workflow lint, native core regressions and headless UI/settings checks.
- Both self-contained release packages, their complete checksum inventories, PE architecture boundaries and relocated runtime smoke tests.
- The full numerical matrix on each architecture: 40 embedding cases, 250 tokenizer cases, 16 chunk/decode fixtures, text batches through eight inputs and two-image scheduling.
- Real document/code/image search, installed IFilters, live updates and abrupt-exit recovery in each extracted package. Both retained eight committed passages and computed only the remaining 39.
- Model acquisition/verification and the complete private-desktop UI suite, including both themes, gallery thumbnails, clearing, folder management, logging, saved preferences and window lifecycle.

These runs used isolated data directories and did not reset or launch against the existing production index. Detailed machine-readable results are retained in the release workspace's ignored `.test-data` directory. The sections below describe the acceptance contract and prior migration evidence.

## Acceptance layers

### Structured chunking and per-type search

The `chunking` native mode validates packaged syntax grammars, function/document boundaries, leading documentation, source/Unicode coverage, token-bounded oversized units, explicit fallback and selective profile migration. `chunking-runtime <application-directory> <default-model-directory> <HF-cache> <output>` checks real Gemma and Qwen token counts, source provenance and encoding of the prepared chunks. The model tokenizer measures the inputs; it does not choose file boundaries.

`search-options` verifies that excluded types do not request query embeddings, shared indexes apply independent thresholds and per-type limits, and empty/non-finite preferences are rejected. Headless WPF checks cover XML version-3 persistence, read-only migration of the legacy threshold, last-selection protection and full matched-chunk view models.

The enhanced isolated UI workflow checks all-three-selected defaults, each slider, image-only and mixed-type retrieval, the final-selected-type guardrail, matched-chunk hover content, and persistence across appearance/logging updates and a complete restart. It retains the existing thumbnail, model configuration, scope, tray and cancellation checks.

### Image-understanding acceptance

`image-pipeline` on the native test executable covers configuration/dependency normalization, stable source IDs, durable generation artifacts, selective image re-embedding, provenance, folder ownership, finite retries, empty OCR outcomes, per-operation completion and restart that reuses completed caption work. A deterministic implementation of `IImageUnderstanding` exercises orchestration independently of the native generator adapter.

`image-models <application-directory> <default-model-directory> <embedding-cache> <image-cache> <output-directory>` exercises both reviewed Florence checkpoints through the same native task ABI. A synthetic image contains a red circle and `HELLO SEARCH 123`; tokenizer IDs are checked against the pinned upstream definition, the caption must describe the reference content and OCR must recover its text. The suite then verifies native image search/evidence, caption/OCR cache reuse on embedder changes, linked versus custom Text dependencies, invalid-task preservation, lazy generator restart, and restoration of direct CLIP embeddings. This is compatibility/functional evidence, not a broad caption/OCR accuracy benchmark.

`RunUiSmoke.ps1 -ModelCache <embedding-cache> -ImageModelCache <image-cache> -ApplicationDirectory <build>` selects an isolated lightweight baseline, tests model-relevant task controls and their XML persistence, OCR-backed gallery results, hidden controls after returning to a direct model, themes and tray lifecycle. It does not replace or stop the user's running deployment. The native memory reserve is not lowered to force tests to run alongside other applications.

### Extensible embedding acceptance

`embedding-recipes` on the native test executable covers reviewed registry identities/aliases, query-versus-document fingerprints, padding-aware mean/CLS/last-token/pre-pooled contracts, malformed ONNX metadata/external paths, shared-space source-kind filtering and query-only XML/catalog updates. It also checks the version-2 logging reader and retention of unfinished reindex status. These checks run as part of the ordinary native core suite without downloading models.

`extensible-models <application-directory> <default-model-directory> <recipe-cache> <output-directory>` runs the real native Qwen3-Embedding-0.6B, E5-base-v2 and E5-small-v2 recipes. It requires those reviewed assets to have been acquired into the supplied isolated cache with the packaged `prepare-hf-model` tool. Coverage includes exact Qwen tokenizer fixtures, the upstream score matrix (maximum error `0.0001`), mixed-length batch/single agreement (cosine above `0.99999`), chunk/prefix token budgets and source coverage, code retrieval, maximum-profile workload probes, cancellation, incompatible image rejection, shared text/code spaces with distinct query roles, query-only changes, unavailable strict-device rejection, restart without re-embedding and restoration of the defaults.

The first new artifact profile is stateless fp32, 1,024 dimensions and a 2,048-token input ceiling; the 512-token chunk and total batch-token budgets are distinct. The two E5 checkpoints use the same prefix/mean-pooling recipe without adding model-specific UI or indexing logic. This acceptance does not claim support for Qwen generation models, Florence, every checkpoint or every GPU/NPU.

The private-desktop runner accepts `-EmbeddingModelCache <recipe-cache>` in addition to the existing `-ModelCache`. It checks automatic requirements, optional query-purpose disclosure, search availability during validation, real Qwen Apply, query-only persistence, and returning to the simple Gemma form without leaked hidden settings. Offscreen WPF renders cover the collapsed/expanded recipe details in both themes.

All model and UI runs use isolated corpora/catalogs. Existing configured folders, user selections, database files and native snapshots are not reset to exercise the new recipes. Runtime device checks execute again on worker startup; an old persisted compatibility certificate is not used to skip them.

### Configurable-model acceptance

The new model-routing tests use `all-MiniLM-L6-v2`, `paraphrase-MiniLM-L3-v2`, `Xenova/clip-vit-base-patch32` and the existing EmbeddingGemma2 bundle. Seven real configurations exercise three distinct models, shared text/code, one multimodal model for all types, shared text/images, shared code/images, default text/images with alternate code, and restoration of all defaults. Both architecture builds passed these permutations.

Each case checks index count and file ownership, matching semantic queries, retained vector IDs for unchanged assignments, no-op Apply, rejection of a text-only image selection, and XML persistence. Additional checks cover non-default restart without re-embedding, XML write rollback and recovery when saved XML intent precedes the catalog commit.

On the ARM64 acceptance device, explicit MiniLM inference probes succeeded on CPU, DirectML GPU and Qualcomm QNN NPU. Runtime profiling confirmed actual accelerator kernel execution rather than an all-CPU fallback. A nonexistent provider/device was rejected. GPU/NPU eligibility is conditional on hardware, usable drivers/providers and graph compatibility; these results do not certify all models on all devices.

The model-tab UI check verifies the three default URLs, unchanged Apply, invalid-host preservation, an actual code-model change and restoration of EmbeddingGemma2. Its corpus/catalog are isolated from the user's configuration and indexes. The existing default production-data hashes are checked separately for preservation.

Validation uses temporary catalogs and synthetic fixtures. It does not launch the production deployment, clear configured folders or reset the live index.

| Layer | Command / entry point | Coverage |
|---|---|---|
| Native core | `.\tests\RunChecks.ps1` | Real DiskANN, SQL transactions, ownership, revisions, recovery, batching, CPU plans, process lifecycle, USN, extraction supervision, acquisition and logging |
| Managed UI contracts | Included in `RunChecks.ps1` | XML defaults/persistence, invalid values, document/code/image grouping, search clearing, startup sequencing and footer/layout |
| Model acceptance | `.\tests\RunChecks.ps1 -WithModel` | Exact tokenization/chunking, text/image embeddings, mixed batches, norm/similarity gates and retrieval |
| Packaged integration | `.\tests\RunChecks.ps1 -Integration -ApplicationDirectory <published-folder>` | Real model, real DiskANN, installed IFilters, live deltas, queries and abrupt-exit resume |
| Private-desktop UI | `.\tests\RunUiSmoke.ps1 -Enhanced -ApplicationDirectory <published-folder>` | Native WPF controls, themes, three result sections, thumbnails, metadata, logging, settings and window lifecycle |
| Release acceptance | `Build-Release.ps1` | Exact inventory/hashes, PE architecture, relative paths, payload exclusions and relocated runtime smoke |
| Publication safety | `.\tests\PublishSafety.ps1` | Locked directory refusal without partial replacement, followed by successful unlocked publication |
| Formatting/build | `dotnet build` and `dotnet format --verify-no-changes` | Nullable/warnings-as-errors compilation and repository formatting |

Build dependencies first with `Setup.ps1 -SkipModel`. Model acceptance requires the approved native asset bundle. Real document integration requires installed PDF/Office IFilters; portable core tests use isolated helper fixtures and do not install filters.

The native core executable accepts `core --require-usn` to require an active local journal. Without that option it explicitly reports a skipped live USN test on unsupported/inactive volumes; parser and directory-notification regressions still run. The migration acceptance host exercised the live USN-only path, not just directory notifications.

## Numerical evidence

The committed `tests\Fixtures\goldens.json` and small synthetic images/Office files are frozen reference data, not model weights. They were captured independently from the pinned reference pipeline. Native tests consume them directly; fixture creation or Python is not a test prerequisite.

The production native implementation is checked against:

- The original 15 text and 8 image cases.
- 250 tokenizer stress inputs.
- 16 exact chunk/decode cases covering boundaries, overlap, whitespace, special tokens, Unicode and multiline code.
- Nine additional text inputs and eight additional image cases, for **40 embedding cases** in total.
- Mixed-length text batches of **1, 2, 4, 6 and 8**, including long passages, Unicode titles and the query-length boundary.
- Eight two-image scheduling pairs, including different formats and geometries.
- Unit normalization, pairwise similarity drift and clearly separated retrieval winners.

| Gate | Required |
|---|---|
| Token IDs / chunk boundaries and decoded passages | Exact |
| Text embeddings | Cosine >= 0.99999; maximum absolute difference <= 0.0002 |
| Fully native image embeddings | Cosine >= 0.999 |
| Unit-norm error | <= 0.00001 |
| Pairwise cosine drift | <= 0.005 |
| Clearly separated top result | Same winner; near ties recorded separately |

Both ARM64 and x64 inference passed the numerical matrix. The ARM64 acceptance run recorded minimum embedding cosine **0.999942543**, maximum unit-norm error **1.58e-8**, and maximum pairwise drift **0.001227515**. All exact tokenizer/chunk cases and batch gates passed. Ten query-ranking comparisons included three explicitly recorded near ties.

Expanded codec coverage includes DIB-encoded 24/32-bit ICO, grayscale PNG/JPEG, JPEG profiles, palette transparency, BMP, odd large dimensions and extreme aspect ratios. Small lossless RGB decoding is checked against reference bytes; codec/resampling numerical differences remain subject to the image and retrieval gates.

Two migration defects were found through these checks and corrected before acceptance: ordinary newline tokens were being dropped by generic passage decoding, and unprivileged USN records with redacted names needed parent-directory reconciliation.

## Native desktop evidence

The original release 1.1.0 ARM64 package passed private-desktop UI Automation with Python absent from `PATH`: maximized startup, attribution links, all four tabs, light/dark switching, native folder selection, real document/code/image retrieval, gallery thumbnails, live metadata, clearing completed/in-flight searches, logging changes, XML persistence, scope removal, its then-current close-to-taskbar behavior, single-instance restoration and Quit. A complete restart retained theme, logging and the changed similarity value.

Current regression coverage adds offline restart deltas (including deleted subfolders, timestamp-only notifications with unchanged content, and partially committed work), search while monitoring setup is blocked, and a no-change second restart with no new embeddings. The real-engine integration test changes/adds/deletes files while stopped and asserts that only the two changed/new files are embedded. The UI suite now checks minimize/close-to-tray behavior, ongoing indexing while hidden, absence of the taskbar button, tray click/menu restoration, single-instance activation and Quit from the tray while hidden. On the private desktop, shell callbacks are delivered to the real .NET notification-icon window because that desktop has no Explorer taskbar.

Private-desktop compositor captures were not usable as visual evidence. Separate offscreen renders of the actual XAML/resources, with synthetic data and no application window or engine, were inspected in both themes. Those renders are the documentation previews; they are not screenshots of personal search results.

## Durability and functional evidence

Core regression cases cover real native graph insertion/search/deletion, uint32 tags, capacity growth, Unicode paths, packed save/load, missing/corrupted snapshots and interrupted checkpoint publication. SQL tests cover file deduplication, stale revisions, overlapping ownership, fair claims, transient/permanent errors, reset modes and active-engine lease refusal.

Process tests verify startup deadlines that cannot be extended by progress messages, first-model readiness, optional-warmup cancellation/failure, worker replacement, malformed IPC cleanup, query priority and inherited CPU masks. Batching tests exercise bounded producers, request/result ownership, incompatible-length separation, incomplete responses, failure isolation and shutdown.

A six-reader test retains **six simultaneous file pipelines with one model**. Separate abrupt-termination fixtures preserve a committed member of a combined batch while recovering its uncommitted neighbor. User-exit tests retain saved passages without recording cancellation as a retry failure.

Live USN-only tests disable directory notifications and exercise creation, preserved-timestamp edits, rename, deletion and cursor ordering. A cursor cannot advance while delivery is blocked before durable queue publication.

Both extracted native packages indexed **nine successful files** (six documents, one source file and two images), classified two permanent errors, and searched all three result kinds. Real installed `.pdf`, `.rtf`, `.docx`, `.pptx` and `.xlsx` filters were exercised without modifying the input files. Their captured process/module trees used the package's own ONNX Runtime, DiskANN and C++ runtimes, with a shared affinity mask and no interpreter.

The real crash test interrupted a 47-passage file after eight passages committed. Restart retained those eight and computed **only the remaining 39**. Unrelated completed vector IDs remained unchanged.

A separate numeric workbook (512 rows by 12 columns) produced **36,445 extracted characters and 80 distinct passages**. All 80 were indexed, searchable and retained after checkpoint/reload; the original fixture hash was unchanged. This verifies full workbook processing rather than a sampled or truncated proxy. The previously mentioned `3d_model.xls` was not present in the retained catalog or searched local locations, so this run does not claim to have retested that specific historical file.

## Existing-data compatibility

A read-only SQLite backup and copied native snapshot of the existing application catalog were opened in the native implementation:

| Item | Result |
|---|---|
| Existing file records | 158 retained |
| Existing vectors | 1,956 retained |
| Existing graph | Loaded directly without a recovery rebuild |
| Folder identities / vector blobs / staged passages | Unchanged digests |
| XML preferences | Unchanged digest |

The source catalog, snapshots and configuration were not reset or migrated in place. Synthetic tests separately cover an active durable queue, since this copied real catalog had no pending jobs.

## Performance interpretation

On the acceptance machine (Windows 11 ARM64, 12 physical cores, shared six-core affinity), final extracted-package runs reported:

| Measurement | ARM64 package | x64 package under Windows compatibility |
|---|---:|---:|
| First-model service readiness | 15.03 s | 24.88 s |
| Nine-file mixed-format corpus | 44.20 s | 80.42 s |
| Maximum active files | 6 | 6 |
| Representative document query | 380.6 ms | 515.0 ms |
| Coordinator peak working set | 108.2 MiB | 133.7 MiB |
| Largest inference-process peak working set | 2,193.4 MiB | 2,288.0 MiB |

These are workload-specific measurements, not guarantees or a controlled speedup claim against another release. Images, document passage counts, memory pressure and warm caches strongly affect results. A native model process is not a file pipeline; CPU eligibility is a ceiling rather than a utilization target.

### Controlled warm text workload

A separate sequential comparison used the same 18 source files, one warmed model, six eligible cores and six model compute threads on the same host. The earlier Python-backed implementation at commit `db527d1` was run read-only with temporary catalogs as an engineering reference, not as a native-product dependency.

| Implementation | One file pipeline | Six file pipelines |
|---|---:|---:|
| Python-backed baseline (`db527d1`) | 12.437 s | 7.875 s |
| Native, cooperative ONNX workers | 4.556 s | 3.571 s |

The native six-pipeline run processed three batches of six, reached six active files and was approximately **2.2x faster** than the earlier six-pipeline baseline on this workload. Before disabling idle ONNX spin waits, the native result was slower; the accepted setting removes that shared-core contention. This does not imply the same speedup for image or workbook workloads.

The native benchmark captured its process/module tree: affinity mask `0x3f` throughout, no Python modules/processes, and approximately **1,595 MiB peak working set** for the inference process. The 3 GiB-per-model planning estimate therefore remains conservative for this measured text workload; larger batches and images can use more.

The benchmark is reproducible with the native test executable's `benchmark <application-directory> <model-directory> <output-directory>` command. `verify-files <application-directory> <model-directory> <output-directory> <file>...` performs explicit full-file extraction, passage-count, search, checkpoint/reload and source-integrity checks using copies. It never alters the supplied originals.

Machine-readable run output is written under ignored `.test-data` directories. Personal corpus paths, indexes, model payloads and runtime logs are not committed as evidence.

## Distribution and clean-runtime checks

Native releases retain the x64/ARM64 process boundary, include app-local C++ runtimes, and contain no interpreter executables, Python DLLs/packages, model payloads or user data. The ZIP's relative configuration and every inventoried file hash are checked before publication.

Relocated probes execute the native DiskANN/SQLite utility and ONNX Runtime CPU-provider host with `PATH` restricted to Windows system binaries. Missing-model verification is read-only. These checks do not uninstall software or change the shared host.

Package validation caught an x64-only companion DLL in Microsoft's ARM64 CRT redistribution directory; staging now filters CRT files by their actual PE architecture. The publication regression uses a real directory-read sharing lock, confirms that a failed move leaves the original output intact, then verifies a complete replacement after the lock is released.

The workflow is configured for architecture-matched Windows runners and was checked with actionlint. Local x64 execution was under Windows ARM64 compatibility, not a separate Intel/AMD machine; a remote CI run is not claimed by these local results. An approved `NATIVE_MODEL_BASE_URI` enables numerical acceptance in CI and is required for native tagged releases. Public model hosting is a separately approved release operation; package builds never upload model files automatically.
