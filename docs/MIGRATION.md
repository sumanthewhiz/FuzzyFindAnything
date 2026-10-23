# Upgrading FuzzyFindAnything

[Overview](../README.md) · [Quickstart](../QUICKSTART.md) · [Architecture](ARCHITECTURE.md)

## Compatibility policy

An application update should preserve configured folders, model choices, search preferences and completed indexing work. FuzzyFindAnything keeps the same executable and `%LOCALAPPDATA%\FuzzyFindAnything` data location. The 2.0 branding is not a reason to reset the database or rename existing folders.

Back up the complete data directory before changing deployments. Storage formats are feature-dependent and are not downgraded when a feature is deselected. An older application may be unable to open data written by a newer one; rollback requires the complete pre-upgrade backup, not a manually edited schema number.

### What can trigger reindexing?

| Change | Expected effect |
|---|---|
| Theme, logging, selected search types or similarity thresholds | No file reindexing |
| Query-purpose setting with an unchanged document-vector contract | Existing document embeddings retained |
| Document/code model or chunking-profile change | Only affected file types are rebuilt |
| Provider model, SDK drop or device-specific vector identity change | Affected representations are rebuilt after validation |
| Image text embedder change | Valid captions/OCR/regions are reused; their embeddings are rebuilt |
| Image producer, task recipe or source-image revision change | Affected generation artifacts and vectors are refreshed |
| Native graph snapshot missing or damaged | Graph rebuilt from durable SQLite vectors, without repeating completed inference |

Model dimensions alone do not establish compatibility. The app records vector, tokenizer, preprocessing, chunking and execution identities and validates a new selection before committing it. Failed or cancelled preparation retains the applied settings and index. Idle model-worker eviction is memory management, not reindexing.

### Data-format compatibility

The current app reads configuration versions 1-4. Per-type search and regional-task settings use XML version 3; provider-aware bindings use version 4. Older global similarity settings are read as all three per-type thresholds without rewriting the file simply to read it.

Catalog formats distinguish model routing, generated image artifacts, structured chunks, regions and provider identities. Current structured chunks use schema 8, regions use 9 and provider-aware data uses 10. Existing lower-format data is upgraded as its features are used. A changed chunking profile schedules affected document/code representations while retaining image artifacts and unrelated data.

Saving unrelated preferences preserves model/source/version bindings. Returning to Hugging Face or a direct image model does not downgrade the catalog or configuration. For the detailed contracts, see [architecture](ARCHITECTURE.md) and [chunking](CHUNKING.md).

### Models and optional providers

Keep verified model caches and the separately installed Microsoft companion available. If an applied provider or required device disappears, startup reports the dependency rather than querying an index with a substitute encoder. Use the [employee setup](../QUICKSTART.md#microsoft-employees-psapi-models) to obtain the approved companion; existing registrations are not silently overwritten.

Model source packages and execution formats are not interchangeable. The default native bundle requires its ONNX language/vision graphs, external data and tokenizer configuration; a source `model.safetensors` file is not a replacement. Preserve the previous model installation for rollback, or explicitly select another supported model and allow selective reindexing.

Native graph loading is attempted only after checksums, model identity and generation agree. A missing, incompatible or damaged graph is reconstructed from SQLite vectors without re-embedding completed content. The native graph format is not a reason to clear the catalog or folder list.

## Trial without touching the original installation

1. Build or extract native binaries into a new directory. Provision and verify the native model separately.
2. Use **Quit** in the old application before copying its database. Closing its window only hides it in the tray.
3. Copy the complete data directory into a new trial directory. Never copy only the main SQLite file while a writer is active; the WAL belongs to the same transactional state.
4. Redirect the **copied** XML log destination before launching, so the trial does not append to the original logs.
5. Launch the native binary with `--data-dir` pointing at the trial copy.

Example, after the old application has been explicitly stopped:

```powershell
$original = Join-Path $env:LOCALAPPDATA 'FuzzyFindAnything'
$trial = Join-Path $env:LOCALAPPDATA 'FuzzyFindAnything-NativeTrial'
if (Test-Path -LiteralPath $trial) { throw 'Choose a new, empty trial location.' }
Copy-Item -LiteralPath $original -Destination $trial -Recurse

$preferences = Join-Path $trial 'settings.xml'
if (Test-Path -LiteralPath $preferences) {
    [xml]$settings = Get-Content -LiteralPath $preferences -Raw
    $logging = $settings.DocumentElement.SelectSingleNode('Logging')
    if (-not $logging) {
        $logging = $settings.CreateElement('Logging')
        $logging.SetAttribute('enabled', 'true')
        $null = $settings.DocumentElement.AppendChild($logging)
    }
    $logging.SetAttribute('directory', (Join-Path $trial 'Logs'))
    $settings.Save($preferences)
}

& 'C:\Apps\FuzzyFindAnything-Native\FuzzyFindAnything.exe' --data-dir $trial
```

Configured roots still refer to the same original documents, which the application reads without modifying. Confirm folder IDs/counts, saved similarity/theme, document/code/image search, queued passage progress, logging and live updates.

Do not run both deployments against the same data directory. The engine lease rejects a second writer. Do not defeat that lock by deleting `engine.lock`.

## Apply the upgrade

Cutover is a separate operator decision; builds and tests do not stop or replace an existing installation automatically.

1. Keep a complete, recoverable pre-cutover data backup and the old application/model folders.
2. Stop the old application using **Quit**.
3. Choose either the validated native trial directory or the original data directory under the compatibility policy above.
4. Verify native model hashes and package checksums, then launch the selected native deployment.
5. Confirm first-model readiness, folder scope, index counts, saved preferences and representative searches.

An ordinary application update does not require a reset. `Reset-Index.ps1` is a separate, destructive maintenance operation; use it only when a full rebuild is explicitly intended. `-ClearFolders` is a further, explicit choice.

## Rollback

Stop the native application first. For a separate-directory trial, relaunch the untouched old deployment against its untouched original data. If native production use modified the original data directory, restore the **complete pre-cutover backup** before returning to the older deployment. Do not mix database/WAL files or selectively transplant graph snapshots.

Native writes should not be assumed backward-compatible with every older binary, even when current schema/model identities match. Retaining the original deployment and data is the rollback guarantee.

## Distribution boundary

Application ZIPs contain the native runtimes but no weights or personal data. The approved native model bundle is separately checksummed. Public hosting/publishing of that bundle is not automatic and requires the owner's release-channel approval.

Use versioned package checksums and complete data backups to identify a rollback point. Branch names alone do not establish binary or catalog compatibility.
