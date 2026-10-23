# Licensing, components and model terms

## Application license

**FuzzyFindAnything 2.0 is authored by Suman Ghosh and its original application source and documentation are licensed under the [MIT license](LICENSE).**

The MIT grant does **not** replace the licenses or terms for dependencies, native runtimes, SDKs, models, tokenizers, exported artifacts or other third-party material. A binary package is a collection of separately licensed components, not a relicensing of all its contents under MIT.

Binary ZIPs include `APPLICATION-LICENSE`, `THIRD-PARTY-NOTICES.txt`, original texts in `third-party`, and a checksummed `release-manifest.json` identifying the actual versions and files. These packaged notices are the authoritative inventory for that particular build.

## Principal components

| Component | Used for | License / terms |
|---|---|---|
| [.NET / WPF](https://github.com/dotnet/wpf) | Desktop UI and managed runtime | MIT and bundled third-party notices |
| [Microsoft DiskANN 0.7.0](https://github.com/microsoft/DiskANN/tree/df225d32da81db87596a3ce7c040ad0f78709a7c) | Native ANN graph | MIT; retain its NOTICE and vendored notices |
| [ONNX Runtime 1.31.0](https://github.com/microsoft/onnxruntime) | Default native model inference | MIT and upstream third-party notices |
| [Microsoft.ML.Tokenizers 2.0.0](https://www.nuget.org/packages/Microsoft.ML.Tokenizers/2.0.0) | Native tokenization | MIT |
| [Microsoft Windows ML 2.4.89](https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning/2.4.89) | Compatible-model CPU/GPU/NPU execution | **Microsoft Software License Terms**, with separate included-component terms; not covered by the app's MIT grant |
| [Microsoft.Data.Sqlite.Core 9.0.4](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/9.0.4) | SQLite access | MIT |
| [SQLitePCLRaw 3.0.3](https://github.com/ericsink/SQLitePCL.raw/tree/v3.0.3) | SQLite native bindings | Apache-2.0 |
| [SQLite](https://www.sqlite.org/copyright.html) | Local catalog, queue and vectors | Public domain; wrapper licenses remain separate |
| [TreeSitter.DotNet 1.3.0](https://www.nuget.org/packages/TreeSitter.DotNet/1.3.0), Tree-sitter and packaged grammars | Code structure boundaries | MIT; original runtime/binding/grammar notices are retained individually |
| [Boost 1.78.0](https://www.boost.org/users/license.html) | Native graph dependencies | Boost Software License 1.0 |
| Intel MKL / OpenMP 2022 packages | Native numeric/parallel runtime | Intel Simplified Software License and included notices |
| [Protocol Buffers](https://github.com/protocolbuffers/protobuf) | Native/model-related dependency | BSD-3-Clause and included notices |
| Microsoft Visual C++ redistributables | App-local C++ runtime | [Microsoft redistribution terms](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files) |
| Windows SDK / Windows components | Windows integration and metadata | Applicable Microsoft SDK/platform terms |

Version-specific dependency details come from the project manifests and packaged inventory, not this summary alone. The pinned native source/package versions are in [`native/dependencies.json`](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/main/native/dependencies.json). Verbatim redistribution notices are preserved in [`licenses/upstream`](https://github.com/sumanthewhiz/FuzzyFindAnything/tree/main/licenses/upstream), with their source URLs and SHA-256 values in [`native/license-sources.json`](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/main/native/license-sources.json).

Windows ML and other Microsoft redistributables retain their use, distribution, platform and privacy conditions. Read their included terms before using or redistributing a binary bundle. The application license does not grant rights that an upstream component's license withholds.

## Hugging Face and other models

**Models downloaded via Hugging Face are subject to their respective creators' upstream licenses and terms of use.** Review both the original model card and the selected ONNX/export repository, including any access, attribution, use or redistribution requirements.

Weights are not bundled in public application ZIPs. A supported model-card URL means that a native execution contract is available; it does not mean that the model is MIT-licensed, suitable for every purpose or legally redistributable by every recipient.

Examples:

| Model / artifact | Upstream reference and guidance |
|---|---|
| `google/embeddinggemma-2` | The [pinned source card](https://huggingface.co/google/embeddinggemma-2/tree/914f7f89142e33e77833254d9c9b90c3cef7303b) declares Apache-2.0. The separately prepared native export must retain applicable attribution, notices and modification provenance. |
| `sentence-transformers/paraphrase-MiniLM-L3-v2` | The [model card](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2) declares Apache-2.0. |
| `Xenova/clip-vit-base-patch32` | The [ONNX card](https://huggingface.co/Xenova/clip-vit-base-patch32) identifies the [OpenAI CLIP model](https://huggingface.co/openai/clip-vit-base-patch32). OpenAI's [CLIP repository license](https://github.com/openai/CLIP/blob/main/LICENSE) is MIT; the export card does not declare a separate license identifier. Review both sources rather than inferring an app-granted license. |
| Other embedding, caption, OCR or region models | Consult the exact source and artifact revisions selected by their reviewed recipe. Licensing can differ by model and export. |

The app does not grant rights to indexed documents or images. Generated descriptions can be inaccurate; do not treat them as verified facts or an ownership/license determination.

## Internal Microsoft models and system filters

The optional Microsoft/PSAPI companion, SDK packages and model artifacts are distributed separately through authorized internal channels and remain subject to their applicable Microsoft/internal terms. GitHub enterprise access and model-registry access are separate. The public app's MIT license does not grant access or redistribution rights to these components.

Windows, Office/PDF IFilters, installed GPU/NPU drivers and registered file viewers are not relicensed by this project. Filters are installed separately and are not included in the application ZIP.

## Changes and redistribution

Keep the application MIT notice with copies or substantial portions of the app. Retain all applicable upstream copyright, attribution, license and NOTICE material when redistributing dependencies or permitted model artifacts. Do not replace an upstream license with this project's license or remove existing notices.

For a dependency/model update, re-check its terms, record the exact source/version and update the packaged notice inventory. Do not upload gated models, confidential SDKs, user indexes or credentials to a public release.
