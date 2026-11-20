# Upgrade and rollback

[Overview](../README.md) · [Quickstart](../QUICKSTART.md) · [Model compatibility](MODEL-COMPATIBILITY.md) · [Architecture](ARCHITECTURE.md)

An application update should preserve configured folders, model choices, preferences and committed indexing progress. The executable and default data location remain `FuzzyFindAnything.exe` and `%LOCALAPPDATA%\FuzzyFindAnything`; the 2.0 product name does not require renaming or resetting data.

## Upgrade an installation

1. Use **Quit** in the application or tray menu. Closing the window only hides it.
2. Back up the **complete data directory** and retain the old application and any separately provisioned legacy models. Do not copy only the main SQLite file while a writer is active; its WAL is part of the transactional state.
3. Download the matching architecture's application ZIP and checksum. Verify the SHA-256 and extract the complete ZIP into a **new folder**, not over the running or previous deployment.
4. Launch the new executable with the same data directory. Use the same `--data-dir` argument if the installation has a custom profile.
5. Confirm model readiness, folder scope, saved preferences, index counts and representative document/code/image searches.

An ordinary upgrade does not require `Reset-Index.ps1`. That command deliberately removes generated search data; `-ClearFolders` additionally removes configured roots. Source files are not changed by an application upgrade or index reset.

Storage formats are feature-dependent and are not downgraded when a feature is deselected. An older binary may not read data written by a newer one. Keep the complete pre-upgrade backup until rollback is no longer needed.

## Validate using a copy

To check an upgrade without changing the original index, stop the original application, copy its complete data directory, and launch the new deployment with the copy. Redirect copied logging settings so the new process does not append to the original logs.

```powershell
$original = Join-Path $env:LOCALAPPDATA 'FuzzyFindAnything'
$copy = Join-Path $env:LOCALAPPDATA 'FuzzyFindAnything-UpgradeCheck'
if (Test-Path -LiteralPath $copy) { throw 'Choose a new, empty destination.' }
Copy-Item -LiteralPath $original -Destination $copy -Recurse

$preferences = Join-Path $copy 'settings.xml'
if (Test-Path -LiteralPath $preferences) {
    [xml]$settings = Get-Content -LiteralPath $preferences -Raw
    $logging = $settings.DocumentElement.SelectSingleNode('Logging')
    if (-not $logging) {
        $logging = $settings.CreateElement('Logging')
        $logging.SetAttribute('enabled', 'true')
        $null = $settings.DocumentElement.AppendChild($logging)
    }
    $logging.SetAttribute('directory', (Join-Path $copy 'Logs'))
    $settings.Save($preferences)
}

& 'C:\Apps\FuzzyFindAnything-New\FuzzyFindAnything.exe' --data-dir $copy
```

Replace the executable path with the new extracted application. Configured roots still refer to the original documents, which the app reads without modifying. Verify folder IDs, saved theme/thresholds, searches, queued progress, logging and background changes.

Do not run two deployments against the same data directory. The engine lease rejects a second writer; deleting `engine.lock` is not a supported way to bypass it.

## What triggers reindexing

| Change | Effect |
|---|---|
| Theme, logging, selected search types or similarity thresholds | No file reindexing |
| Query purpose with an unchanged document-vector contract | Existing document vectors retained |
| Document/code model or chunking profile | Only dependent file kinds are rebuilt |
| Provider model, SDK drop or device-specific vector identity | Dependent representations rebuilt after validation |
| Text embedder behind image descriptions | Valid captions/OCR/regions reused; their embeddings rebuilt |
| Image producer, task recipe or source-image revision | Dependent generated artifacts and vectors refreshed |
| Missing or damaged DiskANN snapshot | Graph rebuilt from durable SQLite vectors without repeating committed inference |

Equal dimensions do not make two models compatible. The app records vector, tokenizer, preprocessing, chunking and execution identities and validates a selection before committing it. Failed or cancelled preparation retains the applied settings and index. Unloading an idle model worker is memory management, not reindexing.

## Configuration and model compatibility

Configuration versions 1-4 remain readable. Per-type search and regional-task settings use version 3; provider-aware bindings use version 4. Older global similarity values are read as all three per-type thresholds without rewriting the file merely to load it.

Catalog schemas record model routing, generated image artifacts, structured chunks, regions and provider identities. Structured chunks use schema 8, regions 9 and provider-aware data 10. Features upgrade the necessary storage; saving unrelated preferences or selecting a simpler model does not downgrade it.

Keep required model caches and any authorized companion available. A missing selected provider or explicit device is reported rather than silently replaced. See [model compatibility](MODEL-COMPATIBILITY.md) for exact supported packages and [optional provider setup](../QUICKSTART.md#microsoft-employees-psapi-models) for access requirements.

### Automatic recovery of unfinished onboarding

New installations use `https://huggingface.co/onnx-community/embeddinggemma-2-ONNX` for all three roles and acquire its pinned public assets automatically.

An earlier unsuccessful first launch is recovered automatically only when all of the following hold:

- Every normalized role still has the untouched legacy default selection.
- No model-selection or embedding-route metadata has been committed.
- No file results, published vectors or prepared passage progress exist.
- No complete expected legacy model bundle is installed.

An empty catalog, saved folder list or unprepared queued sources alone do not block recovery. The replacement XML and routes are committed only after verified acquisition and native qualification succeed. Appearance, logging, thresholds, folder IDs and scope are preserved; a disconnected failure leaves the old XML intact.

Customized selections, initialized routes, nonempty indexes, prepared progress and provisioned legacy installations are excluded. When XML is absent but a catalog exists, its saved binding is inspected read-only instead of being replaced with a fresh default.

To migrate an initialized legacy installation deliberately, select the explicit community URL and Apply. This changes the vector contract and rebuilds only its dependent content.

### Earlier public-ONNX recipe layout

Initialized public-model catalogs can contain the earlier `image`, `document_template` and `asset_fingerprint` fields. The compatibility reader recognizes the reviewed original contract and retains its vector fingerprint, cache key and index directory. It does not relabel those vectors or reindex unchanged files.

Supplemental tokenizer repair uses the pinned Google source even when the older manifest has no per-file source fields. Unknown or altered contracts remain rejected. Do not hand-edit descriptors or clear the database to resolve a layout error.

## Legacy model bundles

This section applies only to initialized installations using the custom `embeddinggemma-2-onnx-v1` export. The current public default does not require this bundle or an approved legacy model host.

The legacy package uses the original language/vision graphs and tokenizer files listed in `model-manifest.json`:

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

Neither a `model.safetensors` checkpoint nor renamed community graphs are substitutes. Obtain the expected bundle from its approved source and retain its original model terms and provenance.

From the extracted application folder, copy and verify an approved local bundle:

```powershell
& .\tools\FuzzyFindAnything.Tools.exe acquire-model `
    --destination .\models\embeddinggemma-2-onnx `
    --source-directory 'D:\ApprovedModels\embeddinggemma-2-onnx-v1'
if ($LASTEXITCODE -ne 0) { throw 'Legacy model acquisition failed.' }
& .\tools\FuzzyFindAnything.Tools.exe verify-model --destination .\models\embeddinggemma-2-onnx
if ($LASTEXITCODE -ne 0) { throw 'Legacy model verification failed.' }
```

An approved HTTPS source can be supplied with `--base-uri` instead of `--source-directory`. `Get-Model.ps1` exposes the same workflow for PowerShell 7; `-VerifyOnly` is read-only. Interrupted downloads can be resumed. Assets in use by inference cannot be overwritten.

If a different model destination is required, update the legacy `model` path in `runtime.json` while the application is stopped. This path is independent of the public-model cache under `--data-dir`.

## Rollback

Stop the new application with **Quit**. If it used only a separate validation copy, relaunch the old deployment against its untouched original data. Otherwise, restore the **complete pre-upgrade backup** before opening it with the older binary.

Do not mix database/WAL files, transplant selected graph snapshots or change schema numbers manually. Retaining the old application, required models and complete data backup is the rollback boundary.

## Distribution and identity

Application ZIPs contain runtimes, documentation, notices and pinned model manifests, but no model weights or personal data. Use the package checksum and `release-manifest.json` source revision to identify a deployment; a branch name alone does not establish binary or catalog compatibility.

Model licenses remain separate from the application's MIT license. See [Third-party notices](../THIRD-PARTY-NOTICES.md).
