# FuzzyFindAnything 2.0: verification and release process

[Overview](../README.md) · [Architecture](ARCHITECTURE.md) · [Migration](MIGRATION.md)

## 2.0 release gates

Release verification covers inference, chunking, storage, current UI/tray branding, author metadata, MIT application licensing, required legal/contribution documents, hash-verified upstream notices and binary-only onboarding.

Run the native/headless suites and solution build/format checks, build both architecture packages, and verify package inventories, original license texts and relocation. UI branding must be checked through current XAML renders and the isolated UI workflow. Execute the documented public-model setup against a fresh data directory without relying on a default model bundle or build tools.

Branch parity is an explicit release check:

```powershell
git fetch origin main dev
git diff --exit-code origin/main origin/dev
git rev-parse origin/main origin/dev
```

The branch heads should be the same verified commit. Branch artifacts are available without publishing model weights. A version tag additionally requires the maintainer's approved `NATIVE_MODEL_BASE_URI` parity source; do not weaken that gate or upload private model payloads just to publish a tag.

## Validation principles

Use isolated data directories and synthetic corpora. Never clear the user's index, alter source documents, weaken memory reserves or substitute placeholder vectors to make a check pass. Record the package/source revision, architecture, model/artifact identity, actual execution provider and result for each run.

Unit and orchestration tests do not replace real native execution checks. Package presence is not device qualification, and an ARM64 CPU result does not certify an NPU or an unrelated x64 GPU. Report skipped hardware checks explicitly.

Documentation images are renders of the current XAML with synthetic data. They show UI layout, not personal search results or evidence that a model executed on particular hardware.

## Acceptance layers

### Model-memory lifecycle

The native `scheduling` mode uses real supervised fixture processes with an injected available-memory budget; it does not allocate gigabytes or pressure other applications. Cases cover multiple selected models at startup, idle-worker eviction and cold reactivation, waiting for active inference, rejected oversized models, cancellation during admission, shutdown of a cold waiting query, and recovery of the previous model. An external-provider fixture exercises the real memory-request/acknowledgement protocol through successful preparation, failed qualification and cancellation, checking process and lease cleanup.

Informational native-runtime capability messages must reach diagnostic logs without setting the pool's warning. Actual startup and worker failures remain visible. The reserve and model estimates are not reduced to make these tests pass.

### Internal provider acceptance

`providers` (also in the ordinary core suite) checks typed references, mixed-source XML version 4, rejection of provider bindings in older formats, exact source/version validation, independent execution identities, zero-padding invariants, real 104-dimensional DiskANN retrieval, schema-10 restart and preservation of unrelated sources. Headless WPF tests cover mixed source controls, selection/device retention, unchanged HF defaults, unavailable-model guards and saving unrelated settings without losing provider bindings.

Companion bootstrap tests reject public or owner-only private channels, incorrect repository identities, unrelated release tags, drafts and replacement archives, even when an unapproved archive agrees with its own checksum sidecar. End-to-end acceptance includes authenticated installation/relocation, live registry discovery and anonymous denial through Microsoft enterprise SSO. Disconnected checks must expose no selectable Microsoft models or versions and must not change the parent environment.

`provider-integration <application-directory> <default-model-directory> <HF-cache> <approved-configurations.json> <output-directory>` takes a caller-supplied array of approved `ModelSelection` configurations; no private model identities or payloads are embedded in the public fixtures. It exercises real mixed-provider document/code/image retrieval, native structural chunk provenance, unchanged-role vector retention, no-op Apply, strict missing-device rejection, restart without re-embedding and restoration of HF models.

`RunUiSmoke.ps1 -ProviderConfiguration <approved-configurations.json> -ModelCache <HF-cache> -ApplicationDirectory <build>` exercises actual WPF source pickers, authenticated live version refresh, version-specific fixed model choices, conditional controls, Apply, image retrieval, XML persistence, appearance changes and tray lifecycle. The internal companion separately qualifies supported SDK/model combinations on real CPU/NPU hardware; package presence alone is not counted as execution evidence.

All acceptance uses isolated catalogs/corpora. Private SDK/model files remain outside the public source and release payloads. Release tests reject provider registrations/settings, companion/native bridge binaries, NuGet archives and encrypted model files.

### Structured chunking and per-type search

The `chunking` native mode validates packaged syntax grammars, function/document boundaries, leading documentation, source/Unicode coverage, token-bounded oversized units, explicit fallback and selective profile migration. `chunking-runtime <application-directory> <default-model-directory> <HF-cache> <output>` checks real Gemma and Qwen token counts, source provenance and encoding of the prepared chunks. The model tokenizer measures the inputs; it does not choose file boundaries.

`search-options` verifies that excluded types do not request query embeddings, shared indexes apply independent thresholds and per-type limits, and empty/non-finite preferences are rejected. Headless WPF checks cover XML version-3 persistence, read-only migration of the legacy threshold, last-selection protection and full matched-chunk view models.

The enhanced isolated UI workflow checks all-three-selected defaults, each slider, image-only and mixed-type retrieval, the final-selected-type guardrail, matched-chunk hover content, and persistence across appearance/logging updates and a complete restart. It retains the existing thumbnail, model configuration, scope, tray and cancellation checks.

### Image-understanding acceptance

`regions` (also included in the ordinary core suite) verifies typed region parsing, finite/valid bounds, repeated labels with multiple boxes, incomplete output, region-only XML, durable geometry, image-level result deduplication, restart and last-owner cleanup. Headless checks exercise region selection retention, aspect-correct thumbnails and rejection of stale source metadata/dimensions. Offscreen renders of the actual XAML show the region overlay in both themes; private-desktop compositor captures are not treated as visual evidence.

`region-models <application-directory> <image-cache> <reference-photo> <output-directory>` runs object labels and dense region descriptions through all four reviewed Florence checkpoints. The cached public vehicle fixture must produce complete labeled vehicle geometry, with no raw location markers in retrieval text. It records each native worker's peak working set and checks the profile's memory allowance. Native ARM64 CPU runs measured approximately 2.25-2.32 GiB for base variants and 5.32-5.39 GiB for large variants on this fixture; these are workload-specific observations, not universal memory limits or accelerator qualification.

`region-integration <application-directory> <default-model-directory> <embedding-cache> <image-cache> <reference-photo> <output-directory>` indexes regions alone, retrieves the original image and matching box, changes the final text embedder without regenerating regions, and verifies geometry/lazy generation across restart. `RunUiSmoke.ps1 -RegionImage <reference-photo> -ModelCache <embedding-cache> -ImageModelCache <image-cache> -ApplicationDirectory <build>` adds real region selection, region-only XML and gallery evidence to the isolated UI workflow.

`image-pipeline` on the native test executable covers configuration/dependency normalization, stable source IDs, durable generation artifacts, selective image re-embedding, provenance, folder ownership, finite retries, empty OCR outcomes, per-operation completion and restart that reuses completed caption work. A deterministic implementation of `IImageUnderstanding` exercises orchestration independently of the native generator adapter.

`image-models <application-directory> <default-model-directory> <embedding-cache> <image-cache> <output-directory>` exercises all four reviewed Florence checkpoints through the same native task ABI. A synthetic image contains a red circle and `HELLO SEARCH 123`; tokenizer IDs are checked against the pinned upstream definition, the caption must describe the reference content and OCR must recover its text. The suite then verifies native image search/evidence, caption/OCR cache reuse on embedder changes, linked versus custom Text dependencies, invalid-task preservation, lazy generator restart, and restoration of direct CLIP embeddings. `image-runtime <application-directory> <image-cache> <output-directory>` runs the native tokenizer/caption/OCR portion alone. This is compatibility/functional evidence, not a broad caption/OCR accuracy benchmark.

`RunUiSmoke.ps1 -ModelCache <embedding-cache> -ImageModelCache <image-cache> -ApplicationDirectory <build>` selects an isolated lightweight baseline, tests model-relevant task controls and their XML persistence, OCR-backed gallery results, hidden controls after returning to a direct model, themes and tray lifecycle. It does not replace or stop the user's running deployment. The native memory reserve is not lowered to force tests to run alongside other applications.

### Extensible embedding acceptance

`embedding-recipes` on the native test executable covers reviewed registry identities/aliases, query-versus-document fingerprints, padding-aware mean/CLS/last-token/pre-pooled contracts, malformed ONNX metadata/external paths, shared-space source-kind filtering and query-only XML/catalog updates. It also checks the version-2 logging reader and retention of unfinished reindex status. These checks run as part of the ordinary native core suite without downloading models.

`extensible-models <application-directory> <default-model-directory> <recipe-cache> <output-directory>` runs the real native Qwen3-Embedding-0.6B, E5-base-v2 and E5-small-v2 recipes. It requires those reviewed assets to have been acquired into the supplied isolated cache with the packaged `prepare-hf-model` tool. Coverage includes exact Qwen tokenizer fixtures, the upstream score matrix (maximum error `0.0001`), mixed-length batch/single agreement (cosine above `0.99999`), chunk/prefix token budgets and source coverage, code retrieval, maximum-profile workload probes, cancellation, incompatible image rejection, shared text/code spaces with distinct query roles, query-only changes, unavailable strict-device rejection, restart without re-embedding and restoration of the defaults.

The reviewed Qwen artifact is stateless fp32, 1,024 dimensions and a 2,048-token input ceiling; the 512-token chunk and total batch-token budgets are distinct. The two E5 checkpoints use the same prefix/mean-pooling recipe without adding model-specific UI or indexing logic. This acceptance does not claim support for Qwen generation models, Florence, every checkpoint or every GPU/NPU.

The private-desktop runner accepts `-EmbeddingModelCache <recipe-cache>` in addition to the existing `-ModelCache`. It checks automatic requirements, optional query-purpose disclosure, search availability during validation, real Qwen Apply, query-only persistence, and returning to the simple Gemma form without leaked hidden settings. Offscreen WPF renders cover the collapsed/expanded recipe details in both themes.

All model and UI runs use isolated corpora/catalogs. Existing configured folders, user selections, database files and native snapshots are not reset to exercise the new recipes. Runtime device checks execute again on worker startup; an old persisted compatibility certificate is not used to skip them.

### Configurable-model acceptance

Model-routing tests use `all-MiniLM-L6-v2`, `paraphrase-MiniLM-L3-v2`, `Xenova/clip-vit-base-patch32` and the default EmbeddingGemma2 bundle. Configurations exercise three distinct models, shared text/code, one multimodal model for all types, shared text/images, shared code/images, default text/images with alternate code, and restoration of all defaults.

Each case checks index count and file ownership, matching semantic queries, retained vector IDs for unchanged assignments, no-op Apply, rejection of a text-only image selection, and XML persistence. Additional checks cover non-default restart without re-embedding, XML write rollback and recovery when saved XML intent precedes the catalog commit.

Real-device probes must confirm accelerator kernel execution rather than an all-CPU fallback and reject nonexistent requested providers/devices. GPU/NPU eligibility is conditional on hardware, usable drivers/providers and graph compatibility; qualification of one profile does not certify all models on all devices.

The model-tab UI check verifies the three default URLs, unchanged Apply, invalid-host preservation, an actual code-model change and restoration of EmbeddingGemma2. Its corpus/catalog are isolated from the user's configuration and indexes. The existing default production-data hashes are checked separately for preservation.

Validation uses temporary catalogs and synthetic fixtures. It does not launch the production deployment, clear configured folders or reset the live index.

| Layer | Command / entry point | Coverage |
|---|---|---|
| Native core | `.\tests\RunChecks.ps1` | Real DiskANN, SQL transactions, ownership, revisions, recovery, batching, CPU plans, process lifecycle, USN, extraction supervision, acquisition and logging |
| Managed UI contracts | Included in `RunChecks.ps1` | XML defaults/persistence, invalid values, document/code/image grouping, search clearing, startup sequencing and footer/layout |
| Binary-only onboarding | `binary-quickstart <application-directory> <prepared-data-directory>` | Public models prepared by the documented packaged commands; all search types and restart without a default model bundle |
| Model acceptance | `.\tests\RunChecks.ps1 -WithModel` | Exact tokenization/chunking, text/image embeddings, mixed batches, norm/similarity gates and retrieval |
| Packaged integration | `.\tests\RunChecks.ps1 -Integration -ApplicationDirectory <published-folder>` | Real model, real DiskANN, installed IFilters, live deltas, queries and abrupt-exit resume |
| Private-desktop UI | `.\tests\RunUiSmoke.ps1 -Enhanced -ApplicationDirectory <published-folder>` | Native WPF controls, themes, three result sections, thumbnails, metadata, logging, settings and window lifecycle |
| Release acceptance | `Build-Release.ps1` | Exact inventory/hashes, PE architecture, relative paths, payload exclusions and relocated runtime smoke |
| Publication safety | `.\tests\PublishSafety.ps1` | Locked directory refusal without partial replacement, followed by successful unlocked publication |
| Formatting/build | `dotnet build` and `dotnet format --verify-no-changes` | Nullable/warnings-as-errors compilation and repository formatting |

Build dependencies first with `Setup.ps1 -SkipModel`. Model acceptance requires the approved native asset bundle. Real document integration requires installed PDF/Office IFilters; portable core tests use isolated helper fixtures and do not install filters.

The native core executable accepts `core --require-usn` to require an active local journal. Without that option it explicitly reports a skipped live USN test on unsupported/inactive volumes; parser and directory-notification regressions still run.

## Numerical acceptance

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

Expanded codec coverage includes DIB-encoded 24/32-bit ICO, grayscale PNG/JPEG, JPEG profiles, palette transparency, BMP, odd large dimensions and extreme aspect ratios. Small lossless RGB decoding is checked against reference bytes; codec/resampling numerical differences remain subject to the image and retrieval gates.

## Desktop acceptance

The isolated UI suite covers maximized startup, current app/author branding, all five tabs, light/dark appearance, native folder selection, document/code/image retrieval, gallery thumbnails, live metadata, search clearing, model configuration, logging and saved preferences. Tray tests verify hide/restore, single-instance activation and graceful Quit without losing committed indexing progress.

Current regression coverage adds offline restart deltas (including deleted subfolders, timestamp-only notifications with unchanged content, and partially committed work), search while monitoring setup is blocked, and a no-change second restart with no new embeddings. The real-engine integration test changes/adds/deletes files while stopped and asserts that only the two changed/new files are embedded. The UI suite now checks minimize/close-to-tray behavior, ongoing indexing while hidden, absence of the taskbar button, tray click/menu restoration, single-instance activation and Quit from the tray while hidden. On the private desktop, shell callbacks are delivered to the real .NET notification-icon window because that desktop has no Explorer taskbar.

Private-desktop compositor captures were not usable as visual evidence. Separate offscreen renders of the actual XAML/resources, with synthetic data and no application window or engine, were inspected in both themes. Those renders are the documentation previews; they are not screenshots of personal search results.

## Durability and functional acceptance

Core regression cases cover real native graph insertion/search/deletion, uint32 tags, capacity growth, Unicode paths, packed save/load, missing/corrupted snapshots and interrupted checkpoint publication. SQL tests cover file deduplication, stale revisions, overlapping ownership, fair claims, transient/permanent errors, reset modes and active-engine lease refusal.

Process tests verify startup deadlines that cannot be extended by progress messages, first-model readiness, optional-warmup cancellation/failure, worker replacement, malformed IPC cleanup, query priority and inherited CPU masks. Batching tests exercise bounded producers, request/result ownership, incompatible-length separation, incomplete responses, failure isolation and shutdown.

A six-reader test retains **six simultaneous file pipelines with one model**. Separate abrupt-termination fixtures preserve a committed member of a combined batch while recovering its uncommitted neighbor. User-exit tests retain saved passages without recording cancellation as a retry failure.

Live USN-only tests disable directory notifications and exercise creation, preserved-timestamp edits, rename, deletion and cursor ordering. A cursor cannot advance while delivery is blocked before durable queue publication.

Packaged integration uses mixed-format synthetic files and classified-error cases, including installed PDF/Office filters where available. Source hashes must remain unchanged. Abrupt-exit tests preserve committed passages and compute only missing work after restart; unrelated vector IDs must remain stable. Numeric-workbook checks require complete supported extraction and retrieval, not sampling.

## Existing-data compatibility

Use a read-only SQLite backup and copied native snapshots, never the live writer's files. Compare folder identities, file/vector ownership, vector bytes, staged passage progress and XML settings before and after opening the copy. Exercise pending as well as completed jobs. Verify that an unchanged configuration does not trigger new inference and that a changed profile invalidates only its dependent representations.

## Performance interpretation

Measure service readiness, indexing throughput, query latency, batch sizes, worker residency and peak working set on a declared corpus and hardware profile. Record cold/warm cache conditions and available memory. Images, document passage counts and worker eviction strongly affect results; a model process is not a file pipeline, and CPU eligibility is a ceiling rather than a utilization target. Do not generalize a single workload into a universal speedup or memory guarantee.

The benchmark is reproducible with the native test executable's `benchmark <application-directory> <model-directory> <output-directory>` command. `verify-files <application-directory> <model-directory> <output-directory> <file>...` performs explicit full-file extraction, passage-count, search, checkpoint/reload and source-integrity checks using copies. It never alters the supplied originals.

Machine-readable run output is written under ignored `.test-data` directories. Personal corpus paths, indexes, model payloads and runtime logs are not committed as evidence.

## Distribution and clean-runtime checks

Native releases retain the x64/ARM64 process boundary, include app-local C++ runtimes, and contain no interpreter executables, Python DLLs/packages, model payloads or user data. The ZIP's relative configuration and every inventoried file hash are checked before publication.

Relocated probes execute the native DiskANN/SQLite utility and ONNX Runtime CPU-provider host with `PATH` restricted to Windows system binaries. Missing-model verification is read-only. These checks do not uninstall software or change the shared host.

Staging filters CRT files by their actual PE architecture. The publication regression uses a real directory-read sharing lock, confirms that a failed move leaves the original output intact, then verifies a complete replacement after the lock is released.

The workflow uses architecture-matched Windows runners. Distinguish native x64 execution from x64 compatibility on ARM64 when recording results. An approved `NATIVE_MODEL_BASE_URI` enables numerical acceptance in CI and is required for tagged releases. Package builds never upload model weights automatically.
