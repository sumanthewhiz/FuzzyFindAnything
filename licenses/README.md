# Upstream redistribution notices

`upstream` contains verbatim license/notice bytes imported from the public sources pinned in [`native/license-sources.json`](../native/license-sources.json). Each file is checked against its SHA-256 during packaging.

These files retain their original upstream terms; they are **not** relicensed under the application's MIT license. Git line-ending conversion is disabled for this directory so a checkout preserves the verified bytes.

Release packaging combines these notices with the license/notice files from the exact restored NuGet and native packages. See [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) for the component/model boundary and each release's `third-party` directory and `release-manifest.json` for its complete inventory.

When updating a dependency, obtain the notice from its reviewed source, retain it unchanged and update the corresponding source URL/hash together. Packaging must fail if a required notice is missing or altered; it must not silently omit attribution.
