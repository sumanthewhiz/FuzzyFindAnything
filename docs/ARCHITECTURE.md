# Architecture and operations

[Overview](../README.md) · [Quickstart](../QUICKSTART.md) · [Migration](MIGRATION.md) · [Validation](VALIDATION.md)

## Design boundaries

Fuzzy Find Anything is a per-user desktop search application, not a Windows service, system-index replacement, hosted search service or file-management tool. The configured folder list is its scope authority. Source content is read-only; removing a root removes search ownership, not the underlying files.

The native implementation uses .NET for orchestration and native libraries for inference, graph search, SQLite, imaging and document filters. **Python is absent from normal setup, acquisition, builds, tests and runtime.** Optional model export belongs to a separate engineering/release environment, not an end-user installation.

The active DiskANN graph resides in RAM. Persistence and recovery do not make it an out-of-core graph. Process isolation contains crashes and hangs but is not a security sandbox.

## Components and architecture boundaries

| Component | Source | Responsibility |
|---|---|---|
| WPF desktop | `src\FuzzyFindAnything.App` | Window lifecycle, themes, three result sections, thumbnails, settings and supervision |
| Coordinator executable | `src\FuzzyFindAnything.Engine` | Exclusive data-directory lease, local JSON IPC, startup/status and shutdown |
| Engine library | `src\FuzzyFindAnything.Core` | Catalog, durable queue, discovery, monitoring, concurrency, extraction client and logs |
| Inference host | `src\FuzzyFindAnything.Inference` | Exact tokenization/decoding, image preprocessing and reusable ONNX sessions |
| IFilter helper | `src\FuzzyFindAnything.Extractor` | Isolated COM activation, content detection, bounded extraction and classified errors |
| Native DiskANN bridge | `native` | Pinned C++ implementation, versioned C ABI, caller-owned buffers and packed snapshots |
| Utilities | `src\FuzzyFindAnything.Tools` | Native model acquisition/verification, offline maintenance and package verification |

| Package | UI / inference | Coordinator / DiskANN / extractor / utilities |
|---|---|---|
| `win-x64` | x64 | x64 |
| `win-arm64` | ARM64 | x64 |

The current DiskANN C++/MKL backend requires x64. On ARM64, inference remains native while the smaller coordination/index/helper processes use Windows x64 compatibility. Each process loads only architecture-compatible native dependencies. App-local C++ runtimes and self-contained .NET runtimes are packaged with their notices.

The UI communicates through redirected process pipes, not an HTTP listener. A Windows job object supplies shared affinity and lifetime containment. A per-index byte-range lease prevents concurrent writers, including the older coordinator. A data-directory-specific mutex and activation pipe restore an existing UI instance maximized.

## Model contract

| Property | Value |
|---|---|
| Source model | `google/embeddinggemma-2` |
| Source revision | `914f7f89142e33e77833254d9c9b90c3cef7303b` |
| Native asset identifier | `embeddinggemma-2-onnx-v1` |
| Manifest | `model-assets\embeddinggemma-2.json`, embedded in the utilities/runtime |
| Backend | ONNX Runtime 1.31.0, CPU provider, float32 |
| Tokenizer | Microsoft.ML.Tokenizers 2.0.0 plus the pinned model's explicit decoder |
| Representation | 768-dimensional, unit-normalized float32 |
| Query format | `task: search result \| query: <text>` |
| Document/code format | `title: <filename> \| text: <passage>` |
| Passages | 512 tokens, stride 448; whitespace-canonical duplicate suppression |
| Title / preview limits | 160 / 400 Unicode scalars |

The managed SentencePiece encoder matches the reference token IDs. Passage decoding explicitly follows `tokenizer.json`'s replacement, byte-fallback and concatenation operations; the generic decoder would drop the model's ordinary newline token. Code previews preserve line breaks and indentation. Document previews collapse whitespace.

The language graph supports dynamic batches and sequence lengths and outputs projected 768-dimensional token states. Pooling is attention-mask-aware and includes prompt tokens. Non-finite, zero-length and dimensionally invalid embeddings are rejected. The export explicitly expands position IDs per batch to preserve padded-batch RoPE behavior.

The vision graph has a **single-image** input shape: pixels `[1,2520,768]` and positions `[1,2520,2]`. A two-image request makes two valid vision calls, aligns their soft-token features, then batches language processing. This is not a one-image-at-a-time application restriction.

Image preprocessing handles RGB conversion, EXIF orientation, first-frame GIF, largest supported ICO frame, a 2,048-pixel preparation limit, bicubic resizing, 16-pixel patches, pooling factor 3 and up to 280 soft tokens. Decoded dimensions and allocations are bounded. Filename vectors are never substituted for failed image decoding.

### Acquisition

`Get-Model.ps1` invokes a self-contained .NET utility. It accepts an approved source directory or HTTPS base directory; no public native-model URL is hard-coded. Files are pinned by name, size and SHA-256. `.partial` files support resume, range responses are checked, and downloads cannot exceed the manifest's declared sizes.

Verified assets are atomically promoted. Inference holds read leases that prevent replacement of active model files. `-VerifyOnly` checks every asset without creating files, modifying contents or contacting a host. Model acquisition is separate from application packages and Git.

A custom `-Destination` does not automatically change `runtime.json`; update its `model` path while the application is stopped. Export changes, precision changes and tokenizer/preprocessing changes require new parity evidence and an explicit embedding-space compatibility decision.

## Startup and resource budgeting

Readiness follows nine checkpoints: catalog, monitoring, six primary-model phases and service readiness. Startup events carry monotonic sequence numbers, completed/total steps, percentage, phase, state and error. The UI cannot regress from Ready to a stale Loading snapshot. Percentages describe checkpoints, not elapsed time or an ETA.

The first complete text/vision model enables search. A second model, when resources permit, warms asynchronously and cannot block primary readiness. Its failure leaves the primary usable with a visible warning. Initialization has a five-minute deadline; progress messages do not extend it. Native operations have a three-minute deadline. Shutdown cancels and reaps partially initialized workers.

The shared CPU set contains at most `floor(physical cores / 2)`, selecting one logical processor per chosen physical core and respecting inherited restrictions. Multi-group systems conservatively use the primary group. WPF, coordinator, model hosts, IFilters and native graph work inherit the same set.

| Independent bound | Current value |
|---|---|
| File pipelines | `min(8, eligible processors)` |
| Model processes | Up to 2 when at least 4 eligible processors and memory permit |
| Planning memory | 3 GiB per model plus 2 GiB reserve; not a hard quota |
| Text inference batch | Up to 8 passages |
| Image request | Up to 2 images |
| Pending inference requests | 64, with producer backpressure |
| Coalescing window | 20 ms |
| Durable constituent request | Up to 4 passages |
| File scheduling slice | Up to 16 passages before yielding |

Text requests use Unicode-scalar length buckets to avoid excessive padding. Results retain per-request ownership and order after aggregation. Failed combined requests are isolated so one bad file does not fail healthy neighbors. Queries take priority at available model slots. Cancelling pending producers or shutdown releases waits explicitly; it does not generate success-shaped placeholder vectors.

ONNX worker pools yield rather than spin between operations. With a hard shared affinity set, idle spinning can starve the coordinator and extraction/preparation work; the cooperative configuration was selected using measured end-to-end throughput, not thread count alone.

Model copies and file pipelines are separate. One available model does not reduce six eligible file pipelines to one. The status payload reports each count independently. I/O, memory bandwidth and batch sizes can leave eligible cores idle.

## Discovery, indexing and retrieval

1. Persist configured roots and discover supported files recursively, initially favoring smaller files.
2. Resolve canonical Windows paths, file identities, modification time, size and content hashes. Exclude name-surrogate reparse points, application-owned paths and owned log files.
3. Reuse unchanged published content and queued revisions. Commit new/changed jobs before inference.
4. Extract text, prepare distinct passages and persist preparation once per source revision.
5. Generate embeddings and commit each constituent batch as it returns. Yield large jobs fairly.
6. Recheck scope, file identity, timestamps, size and content hash. A changed file invalidates only its own staged revision.
7. Atomically publish complete file metadata/vectors and dequeue its job. Update the native graph.
8. Prune deleted paths only after successful enumeration. Permission/enumeration failures are not evidence of deletion.

`folders`, `folder_files` and `job_folders` preserve overlapping-root ownership. Removing a parent retains files and pending work still owned by a configured child. Completed files and saved passages do not re-enter work just because startup or a parent notification triggers enumeration.

USN v2/v3 replay tracks file and parent identities. **Unprivileged records may omit names**; those events reconcile the known parent directory without resetting unchanged work. Rename handling reconciles both old and new paths when available. Identity maps are prepared before replay so startup cannot discard relevant records prematurely.

Directory notifications provide a complementary low-latency path. Overflow, journal identity/cursor gaps and unknown record versions trigger conservative reconciliation. Metadata reconciliation also runs periodically. Durable journal cursors advance only after detected changes have been represented by queued work or completed deletions, not before callback delivery.

Search validates the query, produces a query embedding, retrieves DiskANN candidates, resolves their current ownership, filters missing files, reranks by cosine similarity and deduplicates by file. The default limit is 50 files. A selected similarity is a score, not a probability.

## Persistence and recovery

SQLite uses **WAL + FULL synchronization** and retains the schema-4 catalog contract:

| Table | Durable responsibility |
|---|---|
| `settings` | Model/schema identity, graph generation, refresh timestamp and monitoring/discovery state |
| `folders` | Configured roots |
| `files`, `folder_files` | Completed metadata, failures and ownership |
| `vectors` | Stable uint32-compatible IDs, ordered snippets and embedding blobs |
| `index_jobs`, `job_folders` | Fingerprints, hashes, revisions, claims, retries, elapsed time and ownership |
| `job_passages` | Prepared content and individually committed embedding batches |

Interrupted active jobs return to pending without losing prepared content or committed embeddings. Publication and dequeue share a transaction. A crash after SQL commit but before graph mutation is recovered from stored vectors; a crash before publication leaves a resumable job.

The native graph uses Microsoft's C++ DiskANN **0.7.0**, commit `df225d32da81db87596a3ce7c040ad0f78709a7c`, with L2 distance on normalized vectors. `native\dependencies.json` pins its build inputs. The ABI catches native exceptions, uses explicit handles/dimensions/uint32 tags and caller-owned result buffers; catalog access serializes mutations/search.

Packed tagged bulk-build snapshots avoid sparse-slot corruption in the dynamic-save path. Snapshot files have SHA-256 digests, model identity and catalog generation in an atomically replaced manifest. Missing, stale or corrupted snapshots rebuild from SQLite, not a full-scan substitute for DiskANN. Replacement graphs are installed only after successful construction/loading. Graph/storage failures are visible; committed state is not silently discarded.

Exact resume means **completed files and committed passage batches do not repeat**. Computation interrupted before its durable commit may repeat; the middle of a matrix operation is not checkpointed.

## Supported content

| Kind | Formats |
|---|---|
| Plain text | `.txt` |
| IFilter documents | `.rtf`, `.pdf`, `.doc`, `.docx`, `.ppt`, `.pptx`, `.xls`, `.xlsx` |
| Images | `.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, `.ico` |

### Source code formats

| Language | Extensions / filenames |
|---|---|
| Python | `.py`, `.pyw`, `.pyi` |
| JavaScript | `.js`, `.jsx`, `.mjs`, `.cjs` |
| TypeScript | `.ts`, `.tsx`, `.mts`, `.cts` |
| Java | `.java` |
| C / C++ | `.c`, `.h`, `.cc`, `.cpp`, `.cxx`, `.c++`, `.hh`, `.hpp`, `.hxx`, `.h++` |
| Go / Rust / SQL | `.go`, `.rs`, `.sql` |
| Bash | `.sh`, `.bash`, `.bashrc`, `.bash_profile`, `.bash_login`, `.bash_logout` |
| C# | `.cs`, `.csx` |
| PHP | `.php`, `.phtml`, `.php3`, `.php4`, `.php5`, `.php7`, `.php8`, `.phps` |
| Ruby | `.rb`, `.rake`, `.gemspec`, `.rbs`, `Gemfile`, `Rakefile` |
| Swift / Kotlin | `.swift`, `.kt`, `.kts` |
| R | `.r`, `.Rprofile` |

Matching is case-insensitive. Text/code support UTF-8, BOM-marked UTF-16/32 and explicit Windows-code-page fallback. Binary code without an appropriate Unicode BOM is rejected. Valid source mentioning document signatures is not routed through IFilter. Compiled artifacts are not source formats.

The isolated extractor detects actual document formats, supports registered and Office Click-to-Run filters, and handles stream/file initialization. It does not rename source files or change registrations. Existing PDF activation and Office-content diagnostics are retained.

| Condition | Policy |
|---|---|
| Empty, invalid, unsupported or encrypted content | Permanent classified failure; retry after content changes |
| Temporary access/filter/dependency failure | Retain resumable work; retry from 5 minutes up to 1 hour |
| Hung IFilter | 55-second internal watchdog and 60-second external limit |
| Oversized extraction | Reject over 8 million characters; direct text input limited to 32 MB |
| Unavailable embedded content | Retain main text and report a warning |
| Online-only file | Report availability error; do not silently hydrate it |

Scanned PDFs need an OCR-capable filter. Some Excel IFilters omit inline-string cells. GIF uses its first frame; ICO chooses a supported largest frame. Large numeric workbooks are processed fully rather than sampled, within explicit safety limits.

## Settings, logs and data locations

Default data: `%LOCALAPPDATA%\FuzzyFindAnything`. Override it with `--data-dir <absolute-directory>`.

```xml
<FuzzyFindAnythingConfiguration version="1">
  <Appearance theme="Light" />
  <Search minimumSimilarity="0.6" />
  <Logging enabled="true" directory="C:\Users\USER\AppData\Local\FuzzyFindAnything\Logs" />
</FuzzyFindAnythingConfiguration>
```

XML is bounded, DTDs/external entities are prohibited, values are validated and writes are atomic. Similarity defaults to 0.60, accepts finite values from 0.00 to 0.90 and uses culture-independent serialization. Clearing a search or changing theme/logging does not reset it. Failed preference saves restore the saved UI value.

Logging starts enabled. Changing its directory affects new records; old logs are not moved. Disabled logging stops normal file writes while indexing and pipe draining continue. Owned log filenames are excluded without excluding unrelated documents in the same directory.

`indexer.jsonl` records timestamp, full path, kind, outcome, elapsed processing time, attempt/revision, passage count, warnings and classified exception details. Normal cancellation preserves work instead of recording a failed file. Outcome logs rotate at 8 MB; diagnostics at 4 MB; three backups are retained. Storage failures remain visible and do not turn successful indexing into a file failure.

Index metadata includes SQLite/WAL/SHM and native snapshot sizes, excluding models, logs and thumbnail caches. Appearance/search/logging live in XML; folder scope remains in SQLite.

## Build, packaging and maintenance

Source builds require PowerShell 7, .NET 10 SDK, Git, MSVC desktop tools, CMake and a Windows SDK. `Setup.ps1 -SkipModel` builds the pinned native bridge and utilities. `Build.ps1` publishes native-architecture UI/inference and x64 helpers into a fresh staging directory.

Output replacement uses atomic same-volume directory moves and rollback, never a file-by-file deployment swap. It does not terminate a running application. A locked destination is retained; choose another output directory.

`Build-Release.ps1` creates relocatable ZIPs, retained notices, exact component/file manifests and SHA-256 sidecars. It validates architecture, relative paths, every inventoried checksum and absence of interpreter/model/user data, then exercises native dependencies after relocation. `tools\Build-ModelBundle.ps1` separately verifies and packages model assets; it does not publish them.

Offline maintenance requires a stopped application and acquires the same engine lease:

```powershell
# Destructive to generated search data; retains configured roots and exports a scope backup.
.\Reset-Index.ps1 -DataDirectory "$env:LOCALAPPDATA\FuzzyFindAnything"

# Also clears configured roots; source files, models, XML preferences and logs are retained.
.\Reset-Index.ps1 -DataDirectory "$env:LOCALAPPDATA\FuzzyFindAnything" -ClearFolders
```

Neither command starts the application. Never delete an active catalog or WAL. Folder backups are archival and are not imported automatically.

For troubleshooting, inspect the per-file error before assuming a filter is missing; inspect passage progress before attributing a long workbook to extraction. `FFA_IFILTER_TRACE=1` enables additional helper diagnostics. No interpreter thread-dump setting is required in the native engine.

See [validation](VALIDATION.md) for test commands and [migration](MIGRATION.md) for safe cutover. Binaries, model payloads, package caches, personal corpora, indexes, logs and credentials are excluded from Git. The application icon and small synthetic test fixtures are intentional source assets.
