# Security policy

## Reporting

Use [GitHub private vulnerability reporting](https://github.com/sumanthewhiz/FuzzyFindAnything/security/advisories/new) for suspected vulnerabilities. Do not open a public issue containing an exploit, private file, credential, model payload or catalog.

Include the affected version, Windows/CPU architecture, reproduction steps, expected impact and a minimal synthetic example. Share only the minimum diagnostic material needed. There is no guaranteed response-time SLA.

Security fixes target the current 2.0 codebase. Reproduce on the latest supported build where practical; older binaries may not contain current isolation, validation or recovery fixes.

## Trust boundaries

FuzzyFindAnything performs indexing and inference locally. Model acquisition, provider discovery and package authentication require network access, but application source-file content and search queries are not sent to those registries.

The index stores paths, extracted text/snippets, generated descriptions and embeddings. Logs may contain filenames and error details. Protect the data directory with appropriate Windows permissions and device encryption; do not upload it to public issues.

Process isolation limits faults, hangs and crashes; it is **not a security sandbox**. Installed IFilters, native execution providers and registered file viewers are part of the machine's trust boundary. Avoid indexing untrusted files on a privileged account.

Native model assets are pinned/validated, and supported recipes do not execute arbitrary Hugging Face Python or JavaScript. Optional companion downloads require authenticated access, an approved internal repository/release and pinned archive digests. Registry access is checked separately.

Release packages are currently unsigned. SHA-256 checksums detect altered bytes but are not a substitute for publisher verification or code signing. Follow your organization's software and model-use policies.

## Responsible handling

Do not bypass model access controls, SDK restrictions, encryption or protected-document policies. Do not weaken input bounds, memory reserves, architecture checks or checksum validation to work around a failure. Preserve error evidence without copying sensitive content.

Third-party runtimes may have their own telemetry, update and privacy behavior. Their original terms and notices are included in release packages; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
