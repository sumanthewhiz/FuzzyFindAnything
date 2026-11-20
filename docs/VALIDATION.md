# Validation and releases

[Overview](../README.md) · [Contributing](../CONTRIBUTING.md) · [Architecture](ARCHITECTURE.md) · [Migration](MIGRATION.md)

This reference defines the checks used for source changes, model qualification and downloadable packages. Test coverage describes what a command exercises; a passing result must identify the actual source revision, package, architecture, model and execution provider.

## Validation principles

- Use isolated data directories and synthetic corpora. Never reset a user's index, alter source documents or commit private content as a fixture.
- Preserve numerical gates, CPU ceilings, memory reserves and production inference timeouts. Do not substitute placeholder vectors or disable a check to obtain a pass.
- Run architecture-dependent checks on executable hardware. An x64 cross-build is not ARM64 inference evidence, and CPU execution does not certify GPU/NPU support.
- Record genuine skips, external dependencies and unavailable hardware explicitly.
- Keep independent reference fixtures unchanged unless deliberately updating and documenting their source.

## Source checks

Prepare native dependencies with `Setup.ps1 -SkipModel`, then run:

```powershell
.\tests\RunChecks.ps1
dotnet build .\FuzzyFindAnything.sln -c Release --nologo
dotnet format .\FuzzyFindAnything.sln --no-restore --verify-no-changes
```

`RunChecks.ps1` includes native core, headless UI/settings and release-input regressions. It does not need production data or downloaded model weights. Documentation-only changes require relevant link, command, catalogue and notice checks rather than an unrelated inference run.

| Area | Core coverage |
|---|---|
| Native storage | DiskANN tags, growth, snapshots, corruption recovery, SQLite publication, ownership and schema rejection |
| Durable indexing | Source revisions, fair scheduling, retry policy, committed passage resume and cancellation |
| Model configuration | Recipe identities, selective invalidation, no-op Apply, query-only changes, original public descriptors and legacy boundaries |
| Acquisition | Hash/size/range checks, resumable transfers, completed-partial promotion, cache publication and saved-revision repair |
| Resources and processes | Half-core affinity, coordinated memory admission, idle eviction, query priority, bounded IPC and owned-process cleanup |
| Monitoring and extraction | Journal parsing/replay, directory notifications, restart deltas, source formats and bounded IFilter supervision |
| Content and images | Native grammar boundaries, source coverage, chunk provenance, image-task states, region geometry and cache reuse |
| UI/settings | XML validation, saved preferences, three result groups, stale-response handling, startup recovery and layout contracts |
| Distribution | Required files/notices, matching application-license copies, nested-archive rejection, payload exclusions and PE boundaries |

Use the native executable's `core --require-usn` mode when live journal availability is mandatory. Without that option, an unsupported/inactive volume is reported as a live-USN skip; parser and directory-notification checks still run.

## Public-model release acceptance

Every x64 and ARM64 package must pass the extracted first-launch gate before publication. It verifies the public default without an approved legacy model bundle, a development SDK on the application's runtime PATH, or manual model configuration.

After a solution build, explicitly rebuild the acceptance harness as self-contained x64. This avoids a framework-dependent runtime configuration loading the host architecture's shared runtime:

```powershell
dotnet run --project .\tests\FuzzyFindAnything.NativeTests\FuzzyFindAnything.NativeTests.csproj -c Release `
  -r win-x64 -p:PlatformTarget=x64 -p:SelfContained=true -- `
  first-run-acquisition .\artifacts\releases\FuzzyFindAnything-v2.0.0-win-arm64.zip .\tests\Fixtures
```

Use the matching package path; ARM64 execution requires an ARM64 Windows host. The gate covers:

1. Extracted startup with an empty model/data cache and automatic pinned public-model acquisition.
2. Synthetic document, code and image indexing and retrieval.
3. Offline restart without another download or repeated committed indexing.
4. Same-selection recovery after failed download or settings publication.
5. Narrow unfinished-legacy onboarding recovery, preserving appearance, thresholds, logging and folder IDs.
6. Original public recipe/cache startup, native retrieval, offline tokenizer repair and restart without rewriting routes or reindexing.
7. Independent tokenizer, embedding, padded-batch, image-ownership and retrieval-reference checks.

The public profile qualifies 2,048 input tokens, 512-token formatted chunks and a 4,096 padded-token batch budget. It does not establish full upstream context, quantized, audio/video or accelerator support.

## Numerical acceptance

`tests\Fixtures\goldens.json` and its small synthetic images are frozen data captured independently of the candidate implementation. Public acceptance uses 250 tokenizer inputs, 40 text/image embedding cases, mixed text batches of 1/2/4/6/8 and eight independent image pairs.

| Gate | Requirement |
|---|---|
| Token IDs | Exact reference match |
| Text embeddings | Cosine >= 0.99999; maximum absolute difference <= 0.0002 |
| Image embeddings | Cosine >= 0.999 |
| Unit-norm error | <= 0.00001 |
| Pairwise similarity drift | <= 0.005 |
| Clearly separated retrieval winner | Same top result; near ties distinguished from regressions |

Image fixtures include transparency, GIF, ICO/DIB, grayscale/profiled JPEG, palette PNG, BMP, orientation, odd dimensions and extreme aspect ratios. Lossless decode checks and embedding/ranking gates address different stages.

The legacy custom-export `product-parity` mode additionally covers 16 exact chunk/decode fixtures. That optional legacy check is separate from the mandatory public-model gate.

## Distribution verification

Build runtime identifiers sequentially in one checkout:

```powershell
.\Build-Release.ps1 -SkipSetup -RuntimeIdentifier win-x64
.\Build-Release.ps1 -SkipSetup -RuntimeIdentifier win-arm64
```

Choose a new `-Destination` when the versioned archive already exists; packaging does not overwrite published bytes. Each build produces:

```text
FuzzyFindAnything-v2.0.0-win-<architecture>.zip
FuzzyFindAnything-v2.0.0-win-<architecture>.zip.sha256
```

The ZIP contains application files directly, including the executable, runtime directories, documentation, MIT license, original third-party notices and manifests. It must not contain a second application ZIP, model weights, user settings/indexes, private provider payloads or interpreter installations.

Packaging validates every inventoried hash, relative runtime path, PE architecture boundary, required document and application-license copy. The packaged public-model manifest must match the embedded contract. Relocated native probes run with PATH restricted to Windows system binaries; missing-model verification remains read-only.

`tests\PublishSafety.ps1` checks locked-output refusal without partial replacement, followed by complete publication after the lock is released. `tests\ReleaseInputs.ps1` exercises absent/empty/whitespace optional model sources and valid/unsafe URLs through the actual release entry point, while proving an existing archive is preserved.

The native test executable's `release` mode runs the package-contract cases independently; the same cases are included in the full core suite.

### GitHub Actions downloads

The pinned upload action publishes the existing application ZIP **as a single file**, with `archive: false`. Its filename is the artifact name. The checksum is uploaded separately in the same mode, so neither download receives another ZIP layer.

CI downloads both files using the pinned downloader with `skip-decompress: true` and digest mismatch configured as an error. It checks the exact filenames, file count, original/uploaded/downloaded digests, checksum sidecar and native release inventory. This round trip verifies the bytes users download, not only the staging directory.

Tagged-release downloads use the same unextracted files. The release job requires exactly two application ZIPs and two checksum files, verifies their SHA-256 values, creates a draft release and publishes only after successful upload.

### Workflow and release policy

The workflow runs on pushes to `main`/`dev`, pull requests, manual dispatch and `v*` tags. It uses separate Windows x64 and ARM64 hosted runners. Record x64-on-ARM compatibility execution separately from native x64 hardware results.

A version tag must match `Directory.Build.props`. The maintainer promotes verified work to `main` before publishing a release; compare intended source changes and the release manifest's source revision rather than relying on a filename alone.

`NATIVE_MODEL_BASE_URI` is an **optional legacy parity source**, not a requirement for public-model acceptance or tagged releases. A real packaged source must be HTTPS without credentials, query tokens or fragments; blank CI values are normalized to unconfigured. Model weights are never uploaded automatically.

## Optional model qualification

The native test executable accepts the following modes. Paths in angle brackets are caller-supplied; use isolated caches, data directories and outputs.

| Mode | Purpose |
|---|---|
| `public-parity <application> <verified-cache-directory> <fixtures>` | Public compatible-host tokenizer, embeddings, padded batches, independent image ownership and ranking |
| `product-parity <legacy-model-directory> <fixtures> <output>` | Original custom-export numerical and exact chunk/decode parity |
| `binary-quickstart <application> <prepared-data-directory>` | Explicit public-model configuration with no default legacy bundle |
| `model-permutations <application> <legacy-model-directory> <HF-cache> <output>` | Shared/distinct role assignments, selective reindexing, no-op Apply and XML/catalog recovery |
| `extensible-models <application> <legacy-model-directory> <recipe-cache> <output>` | Reviewed Qwen/E5 tokenization, formatting, retrieval, workload limits and configuration changes |
| `embedding-runtime <application> <recipe-cache> <output>` | Native Qwen/E5 runtime checks without the configuration integration phase |
| `embedding-outputs <application> <recipe-cache> <output>` | Generalized CLS/pre-pooled BGE-small and Mixedbread outputs |
| `chunking-runtime <application> <legacy-model-directory> <HF-cache> <output>` | Actual Gemma/Qwen token budgets, source provenance and embedding of prepared chunks |
| `image-models <application> <legacy-model-directory> <embedding-cache> <image-cache> <output>` | Florence tokenizer/caption/OCR behavior, evidence retrieval, cache reuse and linked/custom embedder changes |
| `image-runtime <application> <image-cache> <output>` | Native image-generation checks without configuration integration |
| `region-models <application> <image-cache> <reference-photo> <output>` | Object/region descriptions, valid geometry and resource allowance for the four Florence recipes |
| `region-integration <application> <legacy-model-directory> <embedding-cache> <image-cache> <reference-photo> <output>` | Region-only indexing, retrieval, cached generation reuse and restart |
| `provider-integration <application> <legacy-model-directory> <HF-cache> <approved-configurations.json> <output>` | Authorized mixed-provider selections and execution identities; no private model names are embedded in public fixtures |

The `-WithModel` and `-Integration` switches on `RunChecks.ps1` require the legacy bundle used by those optional modes. Real PDF/Office integration additionally requires matching installed IFilters; portable core tests use synthetic helpers and do not install filters.

Model preparation must honor the shared memory allowance and CPU budget. Accelerator qualification requires evidence that the selected provider executed kernels, not an all-CPU fallback. A passing model/profile does not certify an entire family or every GPU/NPU.

### Image and region criteria

Image-generation checks distinguish complete, partial, skipped and failed outcomes. Valid caption/OCR artifacts must survive text-embedder changes; invalidating one task must not discard a successful sibling. Empty OCR is not a fabricated success vector.

Region checks require finite oriented-image geometry, correct text/box association, bounded output and image-level result deduplication. Retrieval must use the final text embedder and preserve cached regions when only that embedder changes. These are functional compatibility checks, not a general caption/OCR accuracy benchmark.

### Optional provider criteria

Core tests cover typed references, supported configuration versions, device-specific identities, padding invariants, registration validation and preservation of existing vectors. Bootstrap checks reject wrong repositories/tags, unapproved replacement archives and architecture mismatches.

Authenticated installation, registry access and actual private SDK/device execution require separately authorized resources. Report those runs separately. Private payloads, configurations, credentials and model identities must remain outside public fixtures and release packages.

## Desktop acceptance

```powershell
.\tests\RunUiSmoke.ps1 -Enhanced -ApplicationDirectory .\artifacts\FuzzyFindAnything
```

The isolated workflow covers maximized startup, all five tabs, themes, attribution, folder selection, results, thumbnails, metadata, logs, settings and tray lifecycle. It also checks search-type/threshold persistence, matched-chunk previews, unchanged-data restart and no loss of committed indexing progress.

Optional `-ModelCache`, `-EmbeddingModelCache`, `-ImageModelCache`, `-RegionImage` and `-ProviderConfiguration` inputs enable the corresponding real model workflows. Supply only approved synthetic fixtures and isolated caches.

The private desktop has no Explorer taskbar; tray tests deliver shell callbacks to the real notification-icon window. Visual layout evidence uses offscreen renders of current XAML/resources with synthetic data in both themes, not private-desktop compositor captures or personal search results.

## Existing-data and durability checks

Use a read-only SQLite backup and copied snapshots, never a live writer's files. Compare folder IDs, file/vector ownership, vector bytes, staged passages, model identities and XML before and after opening the copy. Include pending and completed jobs.

Abrupt-exit checks must retain committed work and recompute only missing operations. Confirm that stale source revisions cannot publish, overlapping folder ownership is preserved, and missing/corrupt graph snapshots rebuild from SQLite without discarding durable vectors.

Live monitoring checks cover creations, preserved-timestamp edits, renames, deletions and cursor ordering. Journal cursors must not advance before durable delivery. An unchanged restart must not re-embed files.

## Performance and evidence

Measure readiness, indexing throughput, query latency, batch sizes, model residency and peak memory on a declared corpus/hardware profile. Record cold/warm caches, available memory, actual execution provider and source/package/model revisions. A model worker is not a file pipeline, and a CPU ceiling is not a promise of full utilization.

`benchmark <application> <model-directory> <output>` records workload results. `verify-files <application> <model-directory> <output> <file>...` checks copied-file extraction, passage counts, retrieval, checkpoint/reload and source integrity.

Store machine-readable results in ignored `.test-data` locations. Do not commit personal corpus paths, indexes, model payloads or runtime logs. Preserve primary failures if cleanup also fails, and bound cleanup retries for Windows runtime-file locks.
