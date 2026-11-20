# Upstream redistribution notices

[Project licensing](../THIRD-PARTY-NOTICES.md) · [Contribution process](../CONTRIBUTING.md)

`upstream` contains verbatim license/notice bytes imported from the public sources pinned in [`native/license-sources.json`](../native/license-sources.json). Each file is checked against its SHA-256 during packaging.

These files retain their original upstream terms; they are **not** relicensed under the application's MIT license. Git line-ending conversion is disabled for this directory so a checkout preserves the verified bytes.

Release packaging combines these notices with the license/notice files from the exact restored NuGet and native packages. See [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) for the component/model boundary and each release's `third-party` directory and `release-manifest.json` for its complete inventory.

When updating a dependency, obtain its notice from the reviewed source and update the source URL/hash together. Preserve copyright, permission and NOTICE material verbatim. Packaging fails when a required pinned notice is missing or altered; attribution must not be silently omitted.
