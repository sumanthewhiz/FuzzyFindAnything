# Native upgrade and rollback

[Overview](../README.md) · [Quickstart](../QUICKSTART.md) · [Architecture](ARCHITECTURE.md)

## Compatibility policy

The application retains the established model fingerprint, schema-4 catalog, folder ownership, file/vector identifiers, prepared passages and committed embedding batches. Tokenization, chunk decoding, embeddings and retrieval have explicit parity gates; matching dimensions alone is not treated as compatibility proof.

Native graph loading is attempted only after checksums, model identity and generation agree. A missing, incompatible or damaged graph is reconstructed from SQLite vectors without re-embedding completed content. The native graph format is not a reason to clear the catalog or folder list.

The native model **installation layout is different**. It requires `language.onnx`, `vision.onnx`, their external data, tokenizer assets and configuration from the approved native bundle. Keep the old safetensors model and deployment for rollback; do not overwrite either in place.

## Trial without touching the original installation

1. Build or extract native binaries into a new directory. Provision and verify the native model separately.
2. Use **Quit** in the old application before copying its database. Closing its window only minimizes it.
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
    $settings.FuzzyFindAnythingConfiguration.Logging.directory = Join-Path $trial 'Logs'
    $settings.Save($preferences)
}

& 'C:\Apps\FuzzyFindAnything-Native\FuzzyFindAnything.exe' --data-dir $trial
```

Configured roots still refer to the same original documents, which the application reads without modifying. Confirm folder IDs/counts, saved similarity/theme, document/code/image search, queued passage progress, logging and live updates.

Do not run both deployments against the same data directory. The engine lease rejects a second writer. Do not defeat that lock by deleting `engine.lock`.

## Approved cutover

Cutover is a separate operator decision; builds and tests do not stop or replace an existing installation automatically.

1. Keep a complete, recoverable pre-cutover data backup and the old application/model folders.
2. Stop the old application using **Quit**.
3. Choose either the validated native trial directory or the original data directory under the compatibility policy above.
4. Verify native model hashes and package checksums, then launch the selected native deployment.
5. Confirm first-model readiness, folder scope, index counts, saved preferences and representative searches.

No reset is required merely to remove Python. `Reset-Index.ps1` is a separate, destructive maintenance operation; use it only when a full rebuild is explicitly intended. `-ClearFolders` is a further, explicit choice.

## Rollback

Stop the native application first. For a separate-directory trial, relaunch the untouched old deployment against its untouched original data. If native production use modified the original data directory, restore the **complete pre-cutover backup** before returning to the older deployment. Do not mix database/WAL files or selectively transplant graph snapshots.

Native writes should not be assumed backward-compatible with every older binary, even when current schema/model identities match. Retaining the original deployment and data is the rollback guarantee.

## Distribution boundary

Application ZIPs contain the native runtimes but no weights or personal data. The approved native model bundle is separately checksummed. Public hosting/publishing of that bundle is not automatic and requires the owner's release-channel approval.

Use versioned package checksums and complete data backups to identify a rollback point. Branch names alone do not establish binary or catalog compatibility.
