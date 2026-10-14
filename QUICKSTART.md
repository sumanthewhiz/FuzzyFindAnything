# Quickstart: run Fuzzy Find Anything on your PC

> This `dev` branch also supports source-code indexing and a saved minimum-similarity preference. These additions are not included in the unchanged `v1.0.0` stable download; use a `dev` build to try them.

Download the application, acquire its model, and index a small folder. You do **not** need to install Python, pip, Git, Visual Studio, or .NET to use a release.

[Project](https://github.com/sumanthewhiz/FuzzyFindAnything) ·
[Latest download](https://github.com/sumanthewhiz/FuzzyFindAnything/releases/latest) ·
[Detailed documentation](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/main/docs/ARCHITECTURE.md)

## Before you start

| Requirement | Details |
|---|---|
| Windows | Windows 11 on x64 (Intel/AMD) or ARM64 |
| Processor | At least two physical cores; the app uses no more than half of them |
| Memory | On `dev`, at least 5 GiB available for one worker or 8 GiB for two; the app adapts to available RAM |
| Storage | Allow at least 5 GB for the extracted application, downloads, and model; your file index needs additional space |
| Network | Needed to download the ZIP and model; normal indexing/search then works offline |
| Documents | Office/PDF formats need compatible installed IFilters; `.txt` and images are a useful first test |

On ARM PCs, Windows x64 application compatibility must be available. The UI is native ARM64; the bundled engine and document helper are x64.

## 1. Download and extract

Open **[Latest release](https://github.com/sumanthewhiz/FuzzyFindAnything/releases/latest)** and expand **Assets**.

| Your PC | Download |
|---|---|
| Intel or AMD | `FuzzyFindAnything-v<VERSION>-win-x64.zip` |
| Windows on ARM, such as Snapdragon | `FuzzyFindAnything-v<VERSION>-win-arm64.zip` |

Check **Settings > System > About > System type** if unsure. Download the application ZIP, **not** GitHub's automatically generated “Source code” ZIP.

Extract **all files** to a writable local directory, for example:

```text
C:\Users\<you>\Apps\FuzzyFindAnything
```

Do not run the executable from inside the ZIP or copy it out by itself. Keep the `.runtime`, `engine`, and `extractor` directories alongside it. Avoid `Program Files` for this first run because model acquisition writes beneath the extracted folder.

**Optional integrity check:** download the matching `.zip.sha256` file and compare its hash with:

```powershell
Get-FileHash .\FuzzyFindAnything-v<VERSION>-win-x64.zip -Algorithm SHA256
```

Use the actual filename/version. Release packages are unsigned; a matching checksum confirms file integrity, not an Authenticode signature. Verify the download came from `github.com/sumanthewhiz/FuzzyFindAnything` and follow your organization's policies.

## 2. Download the model

Open PowerShell in the extracted application directory, or navigate there:

```powershell
Set-Location "$env:USERPROFILE\Apps\FuzzyFindAnything"
```

Run the included model-acquisition script:

```powershell
.\Get-Model.ps1
```

If local script policy blocks this unsigned script, and your device policy permits it, use a **single-process** override:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Get-Model.ps1
```

This does not permanently change your execution policy. Do not override organization-enforced restrictions; contact your administrator instead.

The script uses the **bundled interpreter** to download the pinned `google/embeddinggemma-2` model into:

```text
models\embeddinggemma-2
```

Expect approximately **1.5 GB** for the model checkpoint, plus tokenizer/configuration files. Download time depends on your connection. If interrupted, rerun the same command to resume. Keep the complete model directory; the weight file alone is insufficient.

To check already-downloaded assets without network access:

```powershell
.\Get-Model.ps1 -VerifyOnly
```

This checks required files and configuration; it is not an embedding/inference test. The script prints **“Model assets ready”** when the check succeeds.

## 3. Launch and try a small folder

Double-click **`FuzzyFindAnything.exe`**, or run:

```powershell
& .\FuzzyFindAnything.exe
```

Wait for the engine status to show **Ready**. The first model load can take a minute or more. On `dev`, startup shows a percentage and current phase. The percentage counts completed startup checkpoints, not elapsed time; a slow import can remain at one percentage while it finishes.

Search is enabled when the first complete text/vision model is ready. A second worker, when memory permits, warms in the background without delaying readiness. The CPU-status tooltip reports ready workers. If startup reports insufficient available RAM, close other applications and retry; your index and queued work remain intact.

1. Open **Indexed folders** and select **Add folder**.
2. Choose a small local folder containing a few `.txt` files and photos. Start small rather than selecting an entire drive.
3. Wait for the status to become **Indexed**. The details show succeeded/failed counts; current processing appears in the status bar.
4. Open **Search**, describe something in those files, and select **Search**.
5. Click a document, code result, or image tile to open the original file in its usual application.

For a predictable text test, save a `.txt` file containing:

> The team is preparing next quarter's budget, including travel expenses, equipment purchases, and the annual conference.

Index its folder, then search for **“planning spending for travel and equipment”**. Try a photo query such as **“mountains beside a lake”** using your own matching images.

If results are too narrow, reduce **Minimum similarity**. Similarity is a ranking score, not a probability.

On `dev`, the initial minimum similarity is **0.60**, and changes are retained in the configuration XML across restarts. Code results appear in their own section. Supported source languages include Python, JavaScript/TypeScript, Java, C/C++, Go, Rust, SQL, Bash, C#, PHP, Ruby, Swift, Kotlin and R; source files are read as text, not executed.

## Everyday controls

| Action | Where / behavior |
|---|---|
| Clear a search | **Clear**, or Escape in the search field, removes the phrase and all result sections |
| Remove a folder | Check it in **Indexed folders**, then **Remove selected**; original files are not deleted |
| Inspect the index | **Index information** shows live counts, size, location, and refresh time |
| Change appearance | Use the **Dark mode** toggle |
| Configure logs | **Configuration** enables/disables logging and chooses a log folder; enabled by default |
| Keep indexing in the background | Close the window; it minimizes to the taskbar |
| Stop the application | Select **Quit**; saved file/passage progress resumes next launch |

Adding, modifying, or deleting files in configured folders updates the index automatically. The application does not register automatic startup at sign-in.

## Where data is stored

| Data | Default location |
|---|---|
| Downloaded model | `models\embeddinggemma-2` beside the executable |
| Folder configuration, queued work, and index | `%LOCALAPPDATA%\FuzzyFindAnything` |
| Appearance and logging preferences | `%LOCALAPPDATA%\FuzzyFindAnything\settings.xml` |
| Per-file outcomes and diagnostics | `%LOCALAPPDATA%\FuzzyFindAnything\Logs`, unless changed in Configuration |

Source files are read, not moved or modified. The catalog contains filenames, snippets, and embeddings; logs may contain file paths. Keep both private.

## Common first-run problems

| Problem | What to check |
|---|---|
| The download page has no application ZIP | Use a completed tagged release, not an in-progress Actions run or the source archive |
| “Runtime configuration is missing” or missing DLLs | Extract the complete release again; retain every folder and `runtime.json` |
| Model unavailable | Run `Get-Model.ps1` from the extracted directory, then `-VerifyOnly`; restart after acquisition |
| Native library / Visual C++ runtime error | Install or repair Microsoft's [latest supported Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist). The engine requires x64 libraries; on ARM64 follow Microsoft's ARM64/x64 guidance |
| Office or PDF file fails | Inspect `indexer.jsonl` in the configured log folder. Check that the document opens normally and a compatible x64 IFilter is installed |
| Scanned or protected document has no content | Scanned PDFs need an OCR-capable IFilter; encrypted documents are not decrypted |
| Cloud files do not index | Make them available on this device first |
| Large file seems slow | Check the current passage counter. Large workbooks can require many embedding batches; already-committed batches are retained |
| No search matches | Confirm successful indexing, try a different phrase, or lower minimum similarity |
| Windows warns about an unsigned download | Verify the source and checksum. Do not bypass your organization's application-control policies |

For error details, use **Configuration > Open folder**. Never post private documents, credentials, unredacted logs, or your index database in a public issue.

## Update safely

Select **Quit** before updating. Extract the new release to a separate folder, keep its bundled runtime intact, and either download the model again or copy your complete existing `models\embeddinggemma-2` directory while the app is stopped.

Read the release notes before launching against an existing index. Normal updates use the same per-user data directory; a model or catalog compatibility change may require documented migration steps. Do not delete an active catalog or its WAL files.
