# Contributing to FuzzyFindAnything

FuzzyFindAnything 2.0 is authored and maintained by **Suman Ghosh**. Contributions to the application and its documentation are welcome under the [MIT license](LICENSE).

## Before changing code

Use an issue to describe a substantial feature or architectural change before implementing it. Include the user scenario, expected behavior and relevant platform/model constraints. For bugs, include the app version, Windows/CPU architecture, reproduction steps and a minimal **synthetic** example.

Never attach private documents, catalogs, model weights, credentials or unredacted logs. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

Keep changes focused. Preserve existing folder ownership, settings, vector identities and durable progress unless a deliberate migration is part of the change. The repository remains named `FuzzyFindAnything`; the 2.0 suffix is app branding, not a namespace or data-directory migration.

## Development environment

Source builds require Windows 11, PowerShell 7, Git, the .NET 10 SDK and Visual Studio 2022 or later with Desktop development with C++, CMake and a Windows SDK. ARM64 packages additionally require the matching ARM64 C++ redistributable files.

```powershell
git clone https://github.com/sumanthewhiz/FuzzyFindAnything.git
Set-Location .\FuzzyFindAnything
git switch dev
git switch -c feature-your-change
.\Setup.ps1 -SkipModel
.\Build.ps1 -SkipSetup
```

Models are provisioned separately. Follow the [binary quickstart](QUICKSTART.md#2-prepare-models) for public model preparation or an approved default-model bundle.

Normal setup, model acquisition, builds, tests and runtime are Python-free. Do not introduce a global interpreter dependency. Build different runtime identifiers sequentially: shared project asset files are restored for the active RID.

## Code and model changes

- Follow the existing C#/XAML/PowerShell formatting and nullable/warnings-as-errors policy.
- Keep UI, coordinator, extraction and inference process boundaries intact. Native libraries must match their host architecture.
- Use bounded requests, explicit errors, cancellation and owned-process cleanup. Do not suppress failures or produce placeholder embeddings.
- Retain the shared half-physical-core CPU ceiling and memory reserve. File-pipeline concurrency is independent of model-worker residency.
- Keep file input read-only. Never reset the user's data to make a test pass.
- Add focused regression tests for changed behavior, using isolated data directories and synthetic fixtures.

Adding a model requires more than listing a model-card URL. Validate its upstream terms, pinned assets, tokenizer, input framing, pooling, output dimensions, modality, device behavior and worst supported input size. Reuse a reviewed recipe where appropriate; introduce a new adapter only for a new execution contract.

Vector, tokenizer, chunking or preprocessing changes need an explicit identity/migration decision. Matching dimensions alone do not prove compatibility. CPU evidence does not certify GPU/NPU execution. See [architecture](docs/ARCHITECTURE.md), [chunking](docs/CHUNKING.md) and [validation](docs/VALIDATION.md).

## Checks before a pull request

Run the smallest relevant tests while developing, then the repository checks for code changes:

```powershell
.\tests\RunChecks.ps1
dotnet build .\FuzzyFindAnything.sln -c Release --nologo
dotnet format .\FuzzyFindAnything.sln --no-restore --verify-no-changes
```

Model/inference changes also require the relevant numerical and real-device tests. Packaging changes require both architecture packages and relocation checks:

```powershell
.\Build-Release.ps1 -SkipSetup -RuntimeIdentifier win-x64
.\Build-Release.ps1 -SkipSetup -RuntimeIdentifier win-arm64
```

UI changes should pass the headless contracts and the applicable isolated UI workflow. Render screenshots from current XAML using synthetic data, never personal search results. Documentation changes should include link/command checks where relevant.

## Pull requests and releases

Target `dev` for ordinary work. Describe the problem, the implementation, compatibility implications and checks actually performed. Do not claim unrun tests or unverified hardware support. The maintainer promotes verified work to `main`; release promotions keep both branches aligned.

Release ZIPs must contain their MIT application license, third-party notices, exact inventory and checksums. Model payloads, private provider components, user data and credentials must remain outside public packages. Keep the pinned upstream notice bytes in `licenses\upstream` unchanged unless deliberately updating the source/hash inventory.

The Microsoft companion has a separate authorized source/distribution channel. Public contributions must not disclose private model identifiers, SDK payloads, feed credentials or internal user data.

## Contribution terms and conduct

By submitting a contribution, you confirm you may contribute it under this project's MIT license. Retain applicable copyright and permission notices. Third-party code, SDKs, models and assets keep their own licenses; do not relabel them as MIT.

Be respectful, constructive and specific in issues and reviews. Discuss technical decisions rather than personal characteristics. The maintainer may moderate abusive, discriminatory or privacy-violating content.
