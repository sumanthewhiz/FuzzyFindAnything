# Model compatibility

**FuzzyFindAnything 2.0 | As of November 2026**

[Overview](../README.md) · [Quickstart](../QUICKSTART.md) · [Migration](MIGRATION.md) · [Model terms](../THIRD-PARTY-NOTICES.md#hugging-face-and-other-models)

This reference describes Hugging Face model selections for document, source-code and image search. It includes the complete integrated model registry and a revision-specific catalogue of additional compatible-format exports, assessed on **19 November 2026**.

FuzzyFindAnything runs supported models locally through native ONNX adapters. Model assets are downloaded separately from the application. Use the exact model-card URLs below: a similarly named checkpoint or export may have a different tokenizer, graph layout or retrieval contract.

## Contents

- [Compatibility categories](#compatibility-categories)
- [Selecting a model](#selecting-a-model)
- [Integrated models](#integrated-models)
- [Configuration and requirements](#configuration-and-requirements)
- [Compatibility catalogue](#compatibility-catalogue)
- [Unsupported selections](#unsupported-selections)
- [Verification scope](#verification-scope)
- [Licensing and attribution](#licensing-and-attribution)

## Compatibility categories

| Category | Meaning |
|---|---|
| **Integrated** | An application-defined recipe specifies the model's pinned artifacts, tokenizer, preprocessing, tensor interfaces and retrieval formatting. These models are listed under [Integrated models](#integrated-models). |
| **Format compatible** | The assessed repository provides files and metadata that match an implemented model family. This establishes format compatibility, not a completed inference or retrieval-quality evaluation. Apply still validates assets and local execution. |
| **Conditional** | The repository clears format checks, but its intended retrieval behavior needs model-specific formatting that is not integrated for that URL. Use an integrated alternative unless that requirement is addressed. |
| **Not supported as listed** | The assessed URL lacks a required artifact or native contract. This finding applies to the listed revision, not every possible export of the model family. |
| **Not verified** | Required metadata could not be accessed. These entries are not classified as compatible or incompatible. |

Compatibility is specific to the model revision, application adapter and execution device. It is not a comparative accuracy rating or a guarantee of CPU, GPU and NPU support on every computer. New upstream revisions require validation; previously prepared models retain their resolved identity.

## Selecting a model

| Search requirement | Model-card URL | Category | Application limits and behavior |
|---|---|---|---|
| One model for documents, code and direct image search | [onnx-community/embeddinggemma-2-ONNX](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX) | Integrated; default | 768 dimensions; 2,048 input tokens. Direct image embeddings and matching text queries. |
| Document and code retrieval with role-specific instructions | [Qwen/Qwen3-Embedding-0.6B](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B) | Integrated | 1,024 dimensions; 2,048 input tokens. Separate document-search and code-search query instructions. |
| English document retrieval | [intfloat/e5-base-v2](https://huggingface.co/intfloat/e5-base-v2) | Integrated | 768 dimensions; 512 input tokens. Required query and passage prefixes are applied automatically. |
| Smaller English retrieval model | [vectoriseai/e5-small-v2](https://huggingface.co/vectoriseai/e5-small-v2) | Integrated | 384 dimensions; 512 input tokens. Use this exact export URL for integrated E5 formatting. |
| Compact general-purpose text retrieval | [sentence-transformers/all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) | Format compatible | 384 dimensions; 256 input tokens. Also accepts code as text. |
| Smaller general-purpose text encoder | [sentence-transformers/paraphrase-MiniLM-L3-v2](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2) | Format compatible | 384 dimensions; 128 input tokens. Shorter passages and a smaller model footprint. |
| English CLS-pooling alternative | [BAAI/bge-small-en-v1.5](https://huggingface.co/BAAI/bge-small-en-v1.5) | Format compatible | 384 dimensions; 512 input tokens. The optional BGE retrieval instruction is not added automatically. |
| Compact pre-pooled embedding export | [mixedbread-ai/mxbai-embed-xsmall-v1](https://huggingface.co/mixedbread-ai/mxbai-embed-xsmall-v1) | Format compatible | 384 dimensions; 512 input tokens. Upstream adaptive dimensions and the advertised 4K context are not application options. |
| Direct visual-semantic image search | [Xenova/clip-vit-base-patch32](https://huggingface.co/Xenova/clip-vit-base-patch32) | Format compatible | 512 dimensions; 77 text tokens; 224-pixel image crop. |
| Alternative CLIP image encoder | [Xenova/clip-vit-base-patch16](https://huggingface.co/Xenova/clip-vit-base-patch16) | Format compatible | 512 dimensions; 77 text tokens; 224-pixel image crop. |
| Image captions, visible text and object or region descriptions | [microsoft/Florence-2-base-ft](https://huggingface.co/microsoft/Florence-2-base-ft) | Integrated | Generates text that is indexed with a linked or separately selected text embedder. |
| Larger caption, OCR and region model | [microsoft/Florence-2-large-ft](https://huggingface.co/microsoft/Florence-2-large-ft) | Integrated | Same image-description workflow with a larger memory allowance. |

### Text and code

The Code role uses code-aware chunk preparation followed by a supported text embedding model. Technical acceptance of code input does not establish code-retrieval quality. Qwen's integrated recipe supplies a dedicated code-search instruction; E5, MiniLM, BGE and GTE can also encode source text through their respective supported paths.

Additional code-oriented format-compatible exports include:

| Model-card URL | Representation | Qualification |
|---|---|---|
| [isuruwijesiri/all-MiniLM-L6-v2-code-search-512](https://huggingface.co/isuruwijesiri/all-MiniLM-L6-v2-code-search-512) | 384 dimensions; 512 input tokens; mean pooling | The publisher describes a CodeSearchNet fine-tune. Format and application configuration checks passed; publisher-reported accuracy was not independently reproduced in FuzzyFindAnything. |
| [isuruwijesiri/all-MiniLM-L12-v2-code-search-512](https://huggingface.co/isuruwijesiri/all-MiniLM-L12-v2-code-search-512) | 384 dimensions; 512 input tokens; mean pooling | The same qualification boundary applies to this 12-layer variant. |

Coding-language generators, masked-language-model backbones and rerankers are not interchangeable with document embedding models. The [unsupported selections](#unsupported-selections) table covers common examples.

### Direct image search and image descriptions

**Direct image search** encodes pixels and text queries into a shared vector space. Gemma2 and compatible CLIP exports provide this path. CLIP can technically accept text and code assignments, but its short, visually aligned text encoder is not a general replacement for long-document or source-code retrieval.

**Image-description search** uses a separate generation stage:

```text
Image -> caption / OCR / object or region description -> text embedder -> searchable vector
```

The four integrated Florence checkpoints support this workflow. The resulting vectors use the selected text embedder's dimensions. Generated descriptions may omit or misinterpret visual content; this path is not equivalent to direct pixel embeddings.

[ff13/fashion-clip](https://huggingface.co/ff13/fashion-clip) is an additional fashion-specific export that passed format and configuration checks. The catalogue distinguishes this and other third-party exports from integrated recipes. Alternate Gemma2 export repositories are not assumed to be different or better trained checkpoints.

## Integrated models

The registry contains **eight artifact packages and 14 selectable URLs**, including the default Gemma2 and its alias. Aliases on the same row resolve to one package and are not counted as separate models.

### Text and direct multimodal models

| Model | Accepted model-card URLs | Artifact repository and immutable revision | Output | Input / chunk / padded batch-token limit |
|---|---|---|---:|---|
| EmbeddingGemma2 | [Community export](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX); [Google model card](https://huggingface.co/google/embeddinggemma-2) | `onnx-community/embeddinggemma-2-ONNX` at `daa72c51243991dfcaf9f9137d2c573d8f7790c0` | 768 | 2,048 / 512 / 4,096 |
| Qwen3-Embedding-0.6B | [Qwen model card](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B); [Qdrant export](https://huggingface.co/Qdrant/Qwen3-Embedding-0.6B-onnx) | `Qdrant/Qwen3-Embedding-0.6B-onnx` at `af95f2c416ffe9379369ad64f9113e865db6112c` | 1,024 | 2,048 / 512 / 2,048 |
| E5-base-v2 | [intfloat/e5-base-v2](https://huggingface.co/intfloat/e5-base-v2) | Same repository at `f52bf8ec8c7124536f0efb74aca902b2995e5bcd` | 768 | 512 / 512 / 4,096 |
| E5-small-v2 | [vectoriseai/e5-small-v2](https://huggingface.co/vectoriseai/e5-small-v2) | Same repository at `a929c750d1f770cd2fd75fadf1d4609c4730e247` | 384 | 512 / 512 / 4,096 |

These are application-qualified limits, not the maximum contexts advertised upstream. Gemma2 and Qwen use the reviewed FP32 exports. Text batches contain at most eight inputs and are split when the padded token budget would be exceeded. Gemma2 supports up to two images per request through independent single-image vision execution.

The Gemma2 SentencePiece file is obtained separately from `google/embeddinggemma-2` at `914f7f89142e33e77833254d9c9b90c3cef7303b`. Existing legacy Google-URL bindings retain their original contract; the community URL identifies the current public package explicitly. Existing original-format public bindings also retain their vector and cache identities. See [migration guidance](MIGRATION.md).

Qwen uses last-real-token pooling and role-specific retrieval instructions. E5 uses mean pooling with automatic `query: ` and `passage: ` formatting. These three text-only packages may be used behind Florence but cannot directly encode image pixels.

### Image-description models

Each checkpoint provides the application's caption, OCR, object-label and region-description tasks. Other upstream Florence tasks or unregistered fine-tunes are not automatically enabled.

| Model | Accepted model-card URLs | Pinned community artifact revision | Generation allowance |
|---|---|---|---:|
| Florence-2-base-ft | [Microsoft model card](https://huggingface.co/microsoft/Florence-2-base-ft); [ONNX export](https://huggingface.co/onnx-community/Florence-2-base-ft) | `e88a44eaf3791a35eae0c5a47b3dbcd36e67eb6f` | 2.5 GiB |
| Florence-2-base | [Microsoft model card](https://huggingface.co/microsoft/Florence-2-base); [ONNX export](https://huggingface.co/onnx-community/Florence-2-base) | `d59e079711c57174f29265539fb4cc9f0f335916` | 2.5 GiB |
| Florence-2-large-ft | [Microsoft model card](https://huggingface.co/microsoft/Florence-2-large-ft); [ONNX export](https://huggingface.co/onnx-community/Florence-2-large-ft) | `04ace74913c28d7ec94af32cee5c111a10126b6f` | 6.5 GiB |
| Florence-2-large | [Microsoft model card](https://huggingface.co/microsoft/Florence-2-large); [ONNX export](https://huggingface.co/onnx-community/Florence-2-large) | `b7751295927207b2a8c2f354af8baadea5b0404b` | 6.5 GiB |

### Memory and devices

| Embedding package | Minimum model allowance |
|---|---:|
| EmbeddingGemma2 | 3 GiB |
| Qwen3-Embedding-0.6B | 3 GiB |
| E5-base-v2 | 1 GiB |
| E5-small-v2 | 0.75 GiB |

The application retains a further **2 GiB system reserve**. Acquired payload size may increase an allowance; the index, runtime, image-generation stage and other applications also require memory. Idle model workers can be unloaded, so adding every allowance together is not a universal simultaneous-RAM requirement.

CPU inference uses packaged native runtimes. GPU/NPU availability depends on the selected export, supported execution provider, driver, hardware and successful local inference checks. An explicitly requested device is not silently replaced when validation fails.

## Configuration and requirements

1. Open **Model configuration** and select the Documents/text, Source code or Images role.
2. Enter the exact HTTPS model-card URL. File URLs and revision-specific URLs are not model selections.
3. For Florence, select the image-description tasks and use the linked Documents/text embedder or a supported custom text model.
4. Select **Apply model changes**. The app acquires missing supported assets and validates modality, available memory and native execution before committing the new configuration.
5. Wait for readiness and any affected background reindexing. Saved thresholds remain unchanged; adjust them by search type to suit the new model's score distribution.

Failed or cancelled preparation retains the applied selection and index. Completed model caches support offline operation. Model changes that alter document-vector or chunking contracts reindex affected file kinds; matching dimensions alone do not make two models compatible with the same stored vectors. Back up existing data before deployment changes as described in [Migration](MIGRATION.md).

### Native format requirements

| Family | Required application-facing contract |
|---|---|
| Registered text models | Exact registered URL; pinned export, tokenizer, pooling, query/document formatting and token limits. Support for Qwen3-Embedding-0.6B does not imply support for arbitrary Qwen checkpoints. |
| BERT sentence embeddings | `model_type: bert`; supported WordPiece/BERT tokenizer; `config.json`, `tokenizer.json`, `tokenizer_config.json`, `vocab.txt`, `1_Pooling/config.json` and `onnx/model.onnx`. Supported mean, CLS or explicitly resolved pre-pooled output; no unhandled transformation modules or scoring contract. |
| CLIP | `model_type: clip`; `onnx/text_model.onnx` and `onnx/vision_model.onnx`; supported CLIP BPE vocabulary/merges; explicit text context and supported RGB resize, crop and normalization metadata. |
| EmbeddingGemma2 ONNX | Reviewed FP32 language and vision tensor interfaces, compatible pinned SentencePiece tokenizer, processor contract and named sentence-embedding output. Different quantized or multimodal layouts are not inferred to be equivalent. |
| Florence | One of the four registered checkpoints/aliases, its native graph stages and reviewed tokenizer, processor and task recipe. |
| All models | Safe immutable assets, integrity and size checks, valid ONNX external-data dependencies, supported tensor shapes and dimensions, finite normalized vectors, and successful local execution qualification. |

The generic BERT path caps inputs at 512 tokens and honors a lower repository-declared limit. The app does not expose arbitrary model conversion, remote Python code, adaptive embedding dimensions, sparse/multi-vector retrieval, or every precision variant offered by a repository.

## Compatibility catalogue

The November 2026 assessment identified **284 additional compatible-format repositories**: 274 BERT, six CLIP and four Gemma2 exports. This catalogue includes community re-exports and domain-specific fine-tunes; separate repositories are not necessarily separate trained checkpoints.

Every BERT entry is a text encoder suitable for a Documents or Code assignment at the format level, or as the text embedder behind Florence. It is not a direct image encoder. CLIP and Gemma2 provide direct multimodal paths, subject to the limits and qualification described above.

The **Checks** column records completed checks:

| Check | Scope |
|---|---|
| **Format** | Required files and relevant configuration, tokenizer and processor metadata match the implemented family. Graph execution is not established by this check. |
| **Configuration** | The application's own read-only model inspector also accepted the URL. |
| **Graph interface** | Native validators accepted processor metadata and the small Gemma2 graph files' named inputs, outputs, types and shapes. Large external weights and inference were not validated by this check. |

**Conditional formatting:** the generic path does not add the required E5 role prefixes for unregistered E5 URLs. `WhereIsAI/UAE-Large-V1` also specifies a retrieval-query prompt that is not supplied automatically. These rows remain in the catalogue for completeness but are marked **Conditional**. Prefer the integrated E5 URLs for the intended E5 retrieval workflow.

License identifiers below are repository-declared metadata, not a grant from FuzzyFindAnything. The revision links identify the exact assessed artifact snapshot.

### Direct image encoders

6 repositories.

| Model-card URL | Category | Dimensions | Maximum input tokens | Declared license | Checks | Assessed revision |
|---|---|---:|---:|---|---|---|
| [dungdq1/clip-vit-base-patch16](https://huggingface.co/dungdq1/clip-vit-base-patch16) | Format compatible | 512 | 77 | Not declared | Format | [`985e6aa2b5cd`](https://huggingface.co/dungdq1/clip-vit-base-patch16/tree/985e6aa2b5cda0afa6dd968711a1c82660df2194) |
| [ff13/fashion-clip](https://huggingface.co/ff13/fashion-clip) | Format compatible | 512 | 77 | MIT | Format; Configuration | [`0f99955d944c`](https://huggingface.co/ff13/fashion-clip/tree/0f99955d944c39bfd9d035a02ac273c7a44a406b) |
| [JONNYVERSE/clip-vit-base-patch32](https://huggingface.co/JONNYVERSE/clip-vit-base-patch32) | Format compatible | 512 | 77 | Not declared | Format | [`10e4dc15b96b`](https://huggingface.co/JONNYVERSE/clip-vit-base-patch32/tree/10e4dc15b96b650f647cae15c6bd06dd14271899) |
| [lquint/clip-vit-base-patch32-onnx](https://huggingface.co/lquint/clip-vit-base-patch32-onnx) | Format compatible | 512 | 77 | Not declared | Format; Configuration | [`971a51ae3d04`](https://huggingface.co/lquint/clip-vit-base-patch32-onnx/tree/971a51ae3d045100b5c0221efcb77adbac5f22c2) |
| [Xenova/clip-vit-base-patch16](https://huggingface.co/Xenova/clip-vit-base-patch16) | Format compatible | 512 | 77 | Not declared | Format; Configuration | [`342fdf2f67ad`](https://huggingface.co/Xenova/clip-vit-base-patch16/tree/342fdf2f67aded64d138ff074745fb4a5d2bba5f) |
| [Xenova/clip-vit-base-patch32](https://huggingface.co/Xenova/clip-vit-base-patch32) | Format compatible | 512 | 77 | MIT | Format; Configuration | [`6b7c523703ae`](https://huggingface.co/Xenova/clip-vit-base-patch32/tree/6b7c523703ae26e39be4b03774966f8571c7f523) |

### Additional Gemma2 exports

4 repositories.

| Model-card URL | Category | Dimensions | Maximum input tokens | Declared license | Checks | Assessed revision |
|---|---|---:|---:|---|---|---|
| [0x3/embeddinggemma-2-ONNX](https://huggingface.co/0x3/embeddinggemma-2-ONNX) | Format compatible | 768 | 2048 | Apache-2.0 | Format; Graph interface | [`7c352bd69b89`](https://huggingface.co/0x3/embeddinggemma-2-ONNX/tree/7c352bd69b897a4efe67e1befbb88aa0575fd784) |
| [bep256/embeddinggemma-2-ONNX](https://huggingface.co/bep256/embeddinggemma-2-ONNX) | Format compatible | 768 | 2048 | Apache-2.0 | Format; Graph interface | [`da7a7f4d5f54`](https://huggingface.co/bep256/embeddinggemma-2-ONNX/tree/da7a7f4d5f54842650f32a8a8fda84c8911433b5) |
| [Synaptics/Google-EmbeddingGemma-2](https://huggingface.co/Synaptics/Google-EmbeddingGemma-2) | Format compatible | 768 | 2048 | Apache-2.0 | Format; Graph interface | [`d1ec9e98e222`](https://huggingface.co/Synaptics/Google-EmbeddingGemma-2/tree/d1ec9e98e2221d1d1199ab09e95f028266b958cc) |
| [wesjos/embeddinggemma-2-onnx-fix](https://huggingface.co/wesjos/embeddinggemma-2-onnx-fix) | Format compatible | 768 | 2048 | Apache-2.0 | Format; Graph interface | [`4b6474837d54`](https://huggingface.co/wesjos/embeddinggemma-2-onnx-fix/tree/4b6474837d540d5330e05a2c8a2eb3902fab46cd) |

### Text and code models from Sentence Transformers

28 repositories.

| Model-card URL | Category | Dimensions | Maximum input tokens | Declared license | Checks | Assessed revision |
|---|---|---:|---:|---|---|---|
| [sentence-transformers/all-MiniLM-L12-v1](https://huggingface.co/sentence-transformers/all-MiniLM-L12-v1) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`26ca81849568`](https://huggingface.co/sentence-transformers/all-MiniLM-L12-v1/tree/26ca81849568ce77f97b2f071cfef3995f2e6be7) |
| [sentence-transformers/all-MiniLM-L12-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L12-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format; Configuration | [`a50ef00143b4`](https://huggingface.co/sentence-transformers/all-MiniLM-L12-v2/tree/a50ef00143b4d5391434df20ae11632588ac25be) |
| [sentence-transformers/all-MiniLM-L6-v1](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v1) | Format compatible | 384 | 128 | Apache-2.0 | Format; Configuration | [`d8ccfb53d087`](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v1/tree/d8ccfb53d087c448cf4f48efed6ea98a01d7e47a) |
| [sentence-transformers/all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`1110a243fdf4`](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/tree/1110a243fdf4706b3f48f1d95db1a4f5529b4d41) |
| [sentence-transformers/allenai-specter](https://huggingface.co/sentence-transformers/allenai-specter) | Format compatible | 768 | 512 | Apache-2.0 | Format; Configuration | [`2c68eeca6125`](https://huggingface.co/sentence-transformers/allenai-specter/tree/2c68eeca61259b2dd70c3f2628219f925df7031a) |
| [sentence-transformers/bert-base-nli-cls-token](https://huggingface.co/sentence-transformers/bert-base-nli-cls-token) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`710678233a49`](https://huggingface.co/sentence-transformers/bert-base-nli-cls-token/tree/710678233a49c038e7cc9f04dfa38f67a2b6cfb1) |
| [sentence-transformers/bert-base-nli-mean-tokens](https://huggingface.co/sentence-transformers/bert-base-nli-mean-tokens) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`160a52b38a51`](https://huggingface.co/sentence-transformers/bert-base-nli-mean-tokens/tree/160a52b38a51ae87295ec3eabcf11755e5d27a8d) |
| [sentence-transformers/bert-base-nli-stsb-mean-tokens](https://huggingface.co/sentence-transformers/bert-base-nli-stsb-mean-tokens) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`c1e99553cc17`](https://huggingface.co/sentence-transformers/bert-base-nli-stsb-mean-tokens/tree/c1e99553cc170c78b16717e93e8042552f5bef20) |
| [sentence-transformers/bert-base-wikipedia-sections-mean-tokens](https://huggingface.co/sentence-transformers/bert-base-wikipedia-sections-mean-tokens) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`e629a6772591`](https://huggingface.co/sentence-transformers/bert-base-wikipedia-sections-mean-tokens/tree/e629a67725917f563dd85b75c72fa32810fc8d72) |
| [sentence-transformers/bert-large-nli-cls-token](https://huggingface.co/sentence-transformers/bert-large-nli-cls-token) | Format compatible | 1024 | 128 | Apache-2.0 | Format; Configuration | [`674eb40f1733`](https://huggingface.co/sentence-transformers/bert-large-nli-cls-token/tree/674eb40f1733119e2385d85ad9e7ebd23410d02a) |
| [sentence-transformers/bert-large-nli-mean-tokens](https://huggingface.co/sentence-transformers/bert-large-nli-mean-tokens) | Format compatible | 1024 | 128 | Apache-2.0 | Format; Configuration | [`a85b0d10e168`](https://huggingface.co/sentence-transformers/bert-large-nli-mean-tokens/tree/a85b0d10e16867eb768e411c66f79475505b65dd) |
| [sentence-transformers/bert-large-nli-stsb-mean-tokens](https://huggingface.co/sentence-transformers/bert-large-nli-stsb-mean-tokens) | Format compatible | 1024 | 128 | Apache-2.0 | Format; Configuration | [`4088dbef2070`](https://huggingface.co/sentence-transformers/bert-large-nli-stsb-mean-tokens/tree/4088dbef2070e7bf5a18e9881698709d2b6f4050) |
| [sentence-transformers/msmarco-bert-co-condensor](https://huggingface.co/sentence-transformers/msmarco-bert-co-condensor) | Format compatible | 768 | 256 | Apache-2.0 | Format; Configuration | [`ddc3ea5b09ba`](https://huggingface.co/sentence-transformers/msmarco-bert-co-condensor/tree/ddc3ea5b09ba8e0922095b4caaa8c8d06e2e4b4f) |
| [sentence-transformers/msmarco-MiniLM-L12-cos-v5](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L12-cos-v5) | Format compatible | 384 | 384 | Not declared | Format; Configuration | [`09660d808c60`](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L12-cos-v5/tree/09660d808c6038205655b469ac62c39fe7288ed4) |
| [sentence-transformers/msmarco-MiniLM-L12-v3](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L12-v3) | Format compatible | 384 | 512 | Apache-2.0 | Format; Configuration | [`04138389795d`](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L12-v3/tree/04138389795d8715e1298fc51c0dc7d18d9670cd) |
| [sentence-transformers/msmarco-MiniLM-L6-cos-v5](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L6-cos-v5) | Format compatible | 384 | 384 | Not declared | Format; Configuration | [`14ca9be4bbcf`](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L6-cos-v5/tree/14ca9be4bbcf1402eac0f43a2e2ccb6e0f994ba3) |
| [sentence-transformers/msmarco-MiniLM-L6-v3](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L6-v3) | Format compatible | 384 | 512 | Apache-2.0 | Format; Configuration | [`fea93b3df392`](https://huggingface.co/sentence-transformers/msmarco-MiniLM-L6-v3/tree/fea93b3df3924e5649a4e322c345f951239d2c13) |
| [sentence-transformers/multi-qa-MiniLM-L6-cos-v1](https://huggingface.co/sentence-transformers/multi-qa-MiniLM-L6-cos-v1) | Format compatible | 384 | 512 | Not declared | Format; Configuration | [`b20736733232`](https://huggingface.co/sentence-transformers/multi-qa-MiniLM-L6-cos-v1/tree/b207367332321f8e44f96e224ef15bc607f4dbf0) |
| [sentence-transformers/nli-bert-base](https://huggingface.co/sentence-transformers/nli-bert-base) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`13a19c4a8594`](https://huggingface.co/sentence-transformers/nli-bert-base/tree/13a19c4a8594b351eefa47695a4668584cf9209b) |
| [sentence-transformers/nli-bert-base-cls-pooling](https://huggingface.co/sentence-transformers/nli-bert-base-cls-pooling) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`d72452392362`](https://huggingface.co/sentence-transformers/nli-bert-base-cls-pooling/tree/d72452392362b4149f1e53dbcdeb4c22da544c2a) |
| [sentence-transformers/nli-bert-large](https://huggingface.co/sentence-transformers/nli-bert-large) | Format compatible | 1024 | 128 | Apache-2.0 | Format; Configuration | [`156bb8082def`](https://huggingface.co/sentence-transformers/nli-bert-large/tree/156bb8082def05a5d948d9a923919f3a963a5014) |
| [sentence-transformers/nli-bert-large-cls-pooling](https://huggingface.co/sentence-transformers/nli-bert-large-cls-pooling) | Format compatible | 1024 | 128 | Apache-2.0 | Format; Configuration | [`a7b06a4dc6b8`](https://huggingface.co/sentence-transformers/nli-bert-large-cls-pooling/tree/a7b06a4dc6b8d2d84d2892801e07b1fbb8eb0815) |
| [sentence-transformers/paraphrase-MiniLM-L12-v2](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L12-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`2b9058918c3d`](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L12-v2/tree/2b9058918c3d228389532ccf813578658e4af706) |
| [sentence-transformers/paraphrase-MiniLM-L3-v2](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format; Configuration | [`4ca70771034a`](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2/tree/4ca70771034acceecb2e72475f72050fcdde4ddc) |
| [sentence-transformers/paraphrase-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L6-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format; Configuration | [`c9a2bfebc254`](https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L6-v2/tree/c9a2bfebc254878aee8c3aca9e6844d5bbb102d1) |
| [sentence-transformers/paraphrase-TinyBERT-L6-v2](https://huggingface.co/sentence-transformers/paraphrase-TinyBERT-L6-v2) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`68ed8e61dc70`](https://huggingface.co/sentence-transformers/paraphrase-TinyBERT-L6-v2/tree/68ed8e61dc7009b863c86c2ea2f748e864cd6b34) |
| [sentence-transformers/stsb-bert-base](https://huggingface.co/sentence-transformers/stsb-bert-base) | Format compatible | 768 | 128 | Apache-2.0 | Format; Configuration | [`ebc4e9811d3c`](https://huggingface.co/sentence-transformers/stsb-bert-base/tree/ebc4e9811d3c3e6b4a2c48357c7b851a147542b2) |
| [sentence-transformers/stsb-bert-large](https://huggingface.co/sentence-transformers/stsb-bert-large) | Format compatible | 1024 | 128 | Apache-2.0 | Format; Configuration | [`5f570ee6ebe4`](https://huggingface.co/sentence-transformers/stsb-bert-large/tree/5f570ee6ebe4c0b4fdba987ec0edb1ccf26ecc9f) |

### Other text and code exports

246 repositories.

| Model-card URL | Category | Dimensions | Maximum input tokens | Declared license | Checks | Assessed revision |
|---|---|---:|---:|---|---|---|
| [21ecr183/all-MiniLM-L6-v2](https://huggingface.co/21ecr183/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`f4c4ed104045`](https://huggingface.co/21ecr183/all-MiniLM-L6-v2/tree/f4c4ed1040455aa62d365e71bf601b8f7b46d86b) |
| [7886542asd/all-MiniLM-L6-v2](https://huggingface.co/7886542asd/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`b9174e3d2039`](https://huggingface.co/7886542asd/all-MiniLM-L6-v2/tree/b9174e3d20399b53f15267a36530da08091896f9) |
| [aajonaa/bge-small-en-v1.5](https://huggingface.co/aajonaa/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`379938b8f70f`](https://huggingface.co/aajonaa/bge-small-en-v1.5/tree/379938b8f70f9d0150bc5c655348e9d8225ac5ee) |
| [aakrati-agrawal/text-embedding-allmini](https://huggingface.co/aakrati-agrawal/text-embedding-allmini) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`a5d271703758`](https://huggingface.co/aakrati-agrawal/text-embedding-allmini/tree/a5d2717037589f4110e91da7ea626eb45ba2a083) |
| [adamezzaim/MNLP_M3_document_encoder](https://huggingface.co/adamezzaim/MNLP_M3_document_encoder) | Format compatible | 1024 | 512 | MIT | Format | [`fd55679b60e1`](https://huggingface.co/adamezzaim/MNLP_M3_document_encoder/tree/fd55679b60e1e2c72dab2f15219adc606098c7e9) |
| [aident-ai/bge-base-en-onnx](https://huggingface.co/aident-ai/bge-base-en-onnx) | Format compatible | 768 | 512 | MIT | Format | [`1d332a435944`](https://huggingface.co/aident-ai/bge-base-en-onnx/tree/1d332a435944023ed7f9ef9d965c7c0c5ff41537) |
| [aimanfadillah/standardized](https://huggingface.co/aimanfadillah/standardized) | Format compatible | 384 | 256 | Not declared | Format; Configuration | [`700db0493eb5`](https://huggingface.co/aimanfadillah/standardized/tree/700db0493eb54a2f693c5bbeb21eabbe6e7960a3) |
| [aimanfadillah/standardized-3](https://huggingface.co/aimanfadillah/standardized-3) | Format compatible | 384 | 256 | Not declared | Format; Configuration | [`3c53ded29683`](https://huggingface.co/aimanfadillah/standardized-3/tree/3c53ded29683b37a93edaa630cc6935fc34d358b) |
| [aimanfadillah/standardized-v2](https://huggingface.co/aimanfadillah/standardized-v2) | Format compatible | 384 | 256 | Not declared | Format; Configuration | [`63d06da238ad`](https://huggingface.co/aimanfadillah/standardized-v2/tree/63d06da238ad2e15f370f135aad62f3a21ec07af) |
| [AkashDeepak/all-MiniLM-L6-v2](https://huggingface.co/AkashDeepak/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`1d63270dcbf4`](https://huggingface.co/AkashDeepak/all-MiniLM-L6-v2/tree/1d63270dcbf4793fbbd713e58b9d906ff333cc18) |
| [akilan1700/all-MiniLM-L6-v2](https://huggingface.co/akilan1700/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`c122ce7b3a8d`](https://huggingface.co/akilan1700/all-MiniLM-L6-v2/tree/c122ce7b3a8db30d4da0ec116745a75ee40f655b) |
| [akio9899/all-MiniLM-L12-v2](https://huggingface.co/akio9899/all-MiniLM-L12-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`f3f9e1f21b30`](https://huggingface.co/akio9899/all-MiniLM-L12-v2/tree/f3f9e1f21b300bb44d9a605dd34427c5fd8d32a7) |
| [Alex3335/all-MiniLM-L6-v2](https://huggingface.co/Alex3335/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`ef5be0f58b06`](https://huggingface.co/Alex3335/all-MiniLM-L6-v2/tree/ef5be0f58b06ae867b9c428cad5e126cf6d8d477) |
| [algolia/algolia-large-en-generic-v2410](https://huggingface.co/algolia/algolia-large-en-generic-v2410) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`3b77ed46d1f3`](https://huggingface.co/algolia/algolia-large-en-generic-v2410/tree/3b77ed46d1f389bacf6819620416622d70648e65) |
| [allan56/all-MiniLM-L6-v2](https://huggingface.co/allan56/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`503cd7717540`](https://huggingface.co/allan56/all-MiniLM-L6-v2/tree/503cd77175404215576dc73e288dc0c2d36bdac0) |
| [andmmenendez/bge-base-warhammer-onnx](https://huggingface.co/andmmenendez/bge-base-warhammer-onnx) | Format compatible | 768 | 512 | Not declared | Format; Configuration | [`a91ce2cdbd06`](https://huggingface.co/andmmenendez/bge-base-warhammer-onnx/tree/a91ce2cdbd062de784b63da63dcb93fdd9b7882b) |
| [ankitgupta8768/indic-sentence-bert-nli-onnx](https://huggingface.co/ankitgupta8768/indic-sentence-bert-nli-onnx) | Format compatible | 768 | 512 | Apache-2.0 | Format | [`5c5b22a9974c`](https://huggingface.co/ankitgupta8768/indic-sentence-bert-nli-onnx/tree/5c5b22a9974c290f0febbd15e5e1a2e31e6a2192) |
| [ankitgupta8768/indic-sentence-similarity-sbert-onnx](https://huggingface.co/ankitgupta8768/indic-sentence-similarity-sbert-onnx) | Format compatible | 768 | 512 | Apache-2.0 | Format | [`3f31dc4c19d2`](https://huggingface.co/ankitgupta8768/indic-sentence-similarity-sbert-onnx/tree/3f31dc4c19d277df247f0d69154b2ed9d9f9f19c) |
| [Anne1702/all-MiniLM-L6-v2](https://huggingface.co/Anne1702/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`e6e2adc2110d`](https://huggingface.co/Anne1702/all-MiniLM-L6-v2/tree/e6e2adc2110d15ebfaa9ea458fb88477c23dc365) |
| [arlenecc/bge-micro-v2](https://huggingface.co/arlenecc/bge-micro-v2) | Format compatible | 384 | 512 | MIT | Format | [`c3643842e500`](https://huggingface.co/arlenecc/bge-micro-v2/tree/c3643842e500558a8424bc190090c7f42b11e3cd) |
| [arnaultsta/MNLP_M2_document_encoder](https://huggingface.co/arnaultsta/MNLP_M2_document_encoder) | Format compatible | 384 | 512 | MIT | Format | [`9f90a5126df8`](https://huggingface.co/arnaultsta/MNLP_M2_document_encoder/tree/9f90a5126df8c17179cf7e980c80a3455b9e0a1a) |
| [arnaultsta/MNLP_M3_document_encoder](https://huggingface.co/arnaultsta/MNLP_M3_document_encoder) | Format compatible | 384 | 512 | MIT | Format | [`6ceffdbcea82`](https://huggingface.co/arnaultsta/MNLP_M3_document_encoder/tree/6ceffdbcea8212045309e28239c43d30e831f746) |
| [Artemrai13/all-MiniLM-L6-v2](https://huggingface.co/Artemrai13/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`1bf903718be2`](https://huggingface.co/Artemrai13/all-MiniLM-L6-v2/tree/1bf903718be269ea7a4de8acae51a46d0b5d0226) |
| [ashritharai/all-MiniLM-L6-v2](https://huggingface.co/ashritharai/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`926ee90b01c8`](https://huggingface.co/ashritharai/all-MiniLM-L6-v2/tree/926ee90b01c823b2fad3bbd6e5e2c37f2e2bb0a6) |
| [atifborntough/all-MiniLM-L6-v2](https://huggingface.co/atifborntough/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`48bc11a9752f`](https://huggingface.co/atifborntough/all-MiniLM-L6-v2/tree/48bc11a9752f033d21dc602b269cca17f8a094af) |
| [avihayamor/tripmatch-ai-embedding-model](https://huggingface.co/avihayamor/tripmatch-ai-embedding-model) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`25766ca03a53`](https://huggingface.co/avihayamor/tripmatch-ai-embedding-model/tree/25766ca03a53118eb1050df3c583ae331c72fea5) |
| [avsolatorio/GIST-all-MiniLM-L6-v2](https://huggingface.co/avsolatorio/GIST-all-MiniLM-L6-v2) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`ea89dfad053b`](https://huggingface.co/avsolatorio/GIST-all-MiniLM-L6-v2/tree/ea89dfad053bba14677bb784a4269898abbdce44) |
| [avsolatorio/GIST-small-Embedding-v0](https://huggingface.co/avsolatorio/GIST-small-Embedding-v0) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`75e62fd210b9`](https://huggingface.co/avsolatorio/GIST-small-Embedding-v0/tree/75e62fd210b9fde790430e0b2f040b0b00a021b1) |
| [BAAI/bge-base-en](https://huggingface.co/BAAI/bge-base-en) | Format compatible | 768 | 512 | MIT | Format | [`b737bf5dcc6e`](https://huggingface.co/BAAI/bge-base-en/tree/b737bf5dcc6ee8bdc530531266b4804a5d77b5d8) |
| [BAAI/bge-base-en-v1.5](https://huggingface.co/BAAI/bge-base-en-v1.5) | Format compatible | 768 | 512 | MIT | Format; Configuration | [`a5beb1e3e68b`](https://huggingface.co/BAAI/bge-base-en-v1.5/tree/a5beb1e3e68b9ab74eb54cfd186867f64f240e1a) |
| [BAAI/bge-large-en-v1.5](https://huggingface.co/BAAI/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`d4aa6901d3a4`](https://huggingface.co/BAAI/bge-large-en-v1.5/tree/d4aa6901d3a41ba39fb536a557fa166f842b0e09) |
| [BAAI/bge-small-en-v1.5](https://huggingface.co/BAAI/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`5c38ec7c405e`](https://huggingface.co/BAAI/bge-small-en-v1.5/tree/5c38ec7c405ec4b44b94cc5a9bb96e735b38267a) |
| [baggiofu1234567/all-MiniLM-L6-v2](https://huggingface.co/baggiofu1234567/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`a8c277ea67d1`](https://huggingface.co/baggiofu1234567/all-MiniLM-L6-v2/tree/a8c277ea67d13ca375e8f63bd6800648e23bc5a3) |
| [barisaydin/gte-base](https://huggingface.co/barisaydin/gte-base) | Format compatible | 768 | 512 | MIT | Format; Configuration | [`ac1ed3df59e2`](https://huggingface.co/barisaydin/gte-base/tree/ac1ed3df59e27724e067286e2ca4e97ab02dcf08) |
| [barisaydin/gte-large](https://huggingface.co/barisaydin/gte-large) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`f670324d4ab7`](https://huggingface.co/barisaydin/gte-large/tree/f670324d4ab75b5713617180781462944c7afa57) |
| [basavyr/all-MiniLM-L6-v2](https://huggingface.co/basavyr/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`99af25c31441`](https://huggingface.co/basavyr/all-MiniLM-L6-v2/tree/99af25c314415aae48c44c29af702ebb3c4393c8) |
| [BEE-spoke-data/bert-plus-L8-v1.0-syntheticSTS-4k](https://huggingface.co/BEE-spoke-data/bert-plus-L8-v1.0-syntheticSTS-4k) | Format compatible | 768 | 512 | Apache-2.0 | Format; Configuration | [`04086fade4b4`](https://huggingface.co/BEE-spoke-data/bert-plus-L8-v1.0-syntheticSTS-4k/tree/04086fade4b45cd47ead03ead3ab0054f82b78e8) |
| [Bharat2004/all-MiniLM-L6-v2](https://huggingface.co/Bharat2004/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`3b1b94093680`](https://huggingface.co/Bharat2004/all-MiniLM-L6-v2/tree/3b1b94093680c9513d707e1bbc2157bd652e6e6d) |
| [bi0/all-MiniLM-L6-v2](https://huggingface.co/bi0/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`9f5d584e4ad9`](https://huggingface.co/bi0/all-MiniLM-L6-v2/tree/9f5d584e4ad92b8025778075c3f1989cc4216f72) |
| [BlackKakapo/cupidon-mini-ro](https://huggingface.co/BlackKakapo/cupidon-mini-ro) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`f4387c654261`](https://huggingface.co/BlackKakapo/cupidon-mini-ro/tree/f4387c654261ddf6eb57cc8bbc69cbf3c39baf23) |
| [BlackKakapo/cupidon-tiny-ro](https://huggingface.co/BlackKakapo/cupidon-tiny-ro) | Format compatible | 128 | 512 | Apache-2.0 | Format; Configuration | [`3fb5e1ab0c08`](https://huggingface.co/BlackKakapo/cupidon-tiny-ro/tree/3fb5e1ab0c087c7369129f4026af8a4aa3b346cb) |
| [BondingAI/all-MiniLM-L12-v2](https://huggingface.co/BondingAI/all-MiniLM-L12-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`71ef0a8138c2`](https://huggingface.co/BondingAI/all-MiniLM-L12-v2/tree/71ef0a8138c2205756423d10af21622d00361594) |
| [CamillaZAY/bge-small-en-v1.5](https://huggingface.co/CamillaZAY/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`0270cc17ae0b`](https://huggingface.co/CamillaZAY/bge-small-en-v1.5/tree/0270cc17ae0b21d7ebe72b4724d65da88702d877) |
| [CamiloGC93/bge-large-en-v1.5-soft-skills](https://huggingface.co/CamiloGC93/bge-large-en-v1.5-soft-skills) | Format compatible | 1024 | 512 | Not declared | Format | [`c6d1d4a77370`](https://huggingface.co/CamiloGC93/bge-large-en-v1.5-soft-skills/tree/c6d1d4a773703ddae92a83a31bae6b3467812828) |
| [Cescofors75/baco-embeddings](https://huggingface.co/Cescofors75/baco-embeddings) | Format compatible | 384 | 512 | Not declared | Format; Configuration | [`a262f7fadf5b`](https://huggingface.co/Cescofors75/baco-embeddings/tree/a262f7fadf5be47856cdccbd60899a8d3c149b0c) |
| [CheckMatt88/all-MiniLM-L6-v2](https://huggingface.co/CheckMatt88/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`ffc110ba3d5c`](https://huggingface.co/CheckMatt88/all-MiniLM-L6-v2/tree/ffc110ba3d5cc0bf8f29ed6df84b3b1d48a3d588) |
| [cherishlh/all-MiniLM-L6-v2](https://huggingface.co/cherishlh/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`342cc040d64f`](https://huggingface.co/cherishlh/all-MiniLM-L6-v2/tree/342cc040d64fe9b374128afb903803aade66dc80) |
| [cherishlh/bge-large-en-v1.5](https://huggingface.co/cherishlh/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format | [`954fc9f72ed3`](https://huggingface.co/cherishlh/bge-large-en-v1.5/tree/954fc9f72ed30bcd7bd16a319f6c7bbae47f8039) |
| [chliu12/all-MiniLM-L6-v2](https://huggingface.co/chliu12/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`5acbd3a084be`](https://huggingface.co/chliu12/all-MiniLM-L6-v2/tree/5acbd3a084be24a08699a3a790b83659a98ece1d) |
| [choosex/all-MiniLM-L6-v2](https://huggingface.co/choosex/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`185fb9d1a170`](https://huggingface.co/choosex/all-MiniLM-L6-v2/tree/185fb9d1a17014a09ebfa8b343a7383ed3b29ab7) |
| [chuanqidota/all-MiniLM-L6-v2](https://huggingface.co/chuanqidota/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`d53b779a68ba`](https://huggingface.co/chuanqidota/all-MiniLM-L6-v2/tree/d53b779a68bace54e0702eceaeecefb08b6d740d) |
| [CYB3RFUNK/all-MiniLM-L6-v2](https://huggingface.co/CYB3RFUNK/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`968ad566514b`](https://huggingface.co/CYB3RFUNK/all-MiniLM-L6-v2/tree/968ad566514b06a3320237bdeedd1b9d669885a1) |
| [cztchoice/stella-base-zh-v2-onnx](https://huggingface.co/cztchoice/stella-base-zh-v2-onnx) | Format compatible | 768 | 512 | Not declared | Format; Configuration | [`30b9494515fc`](https://huggingface.co/cztchoice/stella-base-zh-v2-onnx/tree/30b9494515fcb2f76b923cf04159f02725c5ec37) |
| [daemonicai/bge-micro-v2](https://huggingface.co/daemonicai/bge-micro-v2) | Format compatible | 384 | 512 | MIT | Format | [`e9c3efe66563`](https://huggingface.co/daemonicai/bge-micro-v2/tree/e9c3efe665635bba921d83f7ca6c44593d16954a) |
| [danazydiamonderru/all-MiniLM-L6-v2](https://huggingface.co/danazydiamonderru/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`be525970aa4c`](https://huggingface.co/danazydiamonderru/all-MiniLM-L6-v2/tree/be525970aa4c9a25b1f44b88215ce3f7228b6c2f) |
| [delwinn/all-MiniLM-L6-v2](https://huggingface.co/delwinn/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`68e044be06ed`](https://huggingface.co/delwinn/all-MiniLM-L6-v2/tree/68e044be06ed3dd8f1fe6a7e23b5adc7f7792bea) |
| [dernivalgeraldo/all-MiniLM-L6-v2](https://huggingface.co/dernivalgeraldo/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`4ee49f5fbfb1`](https://huggingface.co/dernivalgeraldo/all-MiniLM-L6-v2/tree/4ee49f5fbfb154366d1e208f2843501bbc87f903) |
| [Dvdidy/all-MiniLM-L6-v2](https://huggingface.co/Dvdidy/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`125662baaa70`](https://huggingface.co/Dvdidy/all-MiniLM-L6-v2/tree/125662baaa7063e858cee7c4329b8fe3e1e79c06) |
| [eadx/all-MiniLM-L6-v2](https://huggingface.co/eadx/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`5bf3e38b34dd`](https://huggingface.co/eadx/all-MiniLM-L6-v2/tree/5bf3e38b34dd76e83c31bd7cde761fdf0aeb94b3) |
| [EmmyHenz001/all-MiniLM-L6-v2](https://huggingface.co/EmmyHenz001/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`455f86dd3361`](https://huggingface.co/EmmyHenz001/all-MiniLM-L6-v2/tree/455f86dd33619a3247fbf2aeb80461a3ec7a468f) |
| [endre01/MNLP_M2_document_encoder](https://huggingface.co/endre01/MNLP_M2_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`8db888dbac3c`](https://huggingface.co/endre01/MNLP_M2_document_encoder/tree/8db888dbac3c51a66f26f30263529b32f4afb1da) |
| [ermcy/all-mini-lm-l6-v2](https://huggingface.co/ermcy/all-mini-lm-l6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`6b3f0efbbb22`](https://huggingface.co/ermcy/all-mini-lm-l6-v2/tree/6b3f0efbbb22bd6e07114cd3ac25183ab715acb3) |
| [EthanXXXXXX/all-MiniLM-L6-v2](https://huggingface.co/EthanXXXXXX/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`6f3ce37b1850`](https://huggingface.co/EthanXXXXXX/all-MiniLM-L6-v2/tree/6f3ce37b1850cadda1c3263b937020d002abdc98) |
| [falloutxvats/all-minilm-l6-v2](https://huggingface.co/falloutxvats/all-minilm-l6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`6b4fc44bfd3f`](https://huggingface.co/falloutxvats/all-minilm-l6-v2/tree/6b4fc44bfd3fe4a10cde7fdb8b049d09f5aa73ab) |
| [falloutxvats/bge-small-en-v1.5](https://huggingface.co/falloutxvats/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`7f26ae3d7cd4`](https://huggingface.co/falloutxvats/bge-small-en-v1.5/tree/7f26ae3d7cd4e382d13aae1c05c9547f3cae69c5) |
| [fengm/all-MiniLM-L6-v2](https://huggingface.co/fengm/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`9b3b22aa8e0b`](https://huggingface.co/fengm/all-MiniLM-L6-v2/tree/9b3b22aa8e0b7fbeaaf387a5f73fc2b4f4edb559) |
| [flaviawallen/MNLP_M2_document_encoder](https://huggingface.co/flaviawallen/MNLP_M2_document_encoder) | Format compatible | 384 | 512 | MIT | Format | [`636645ddf966`](https://huggingface.co/flaviawallen/MNLP_M2_document_encoder/tree/636645ddf9661ce24b943bed481cc3520125d49e) |
| [Foke121/all-MiniLM-L6-v2](https://huggingface.co/Foke121/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`9f863a7023f9`](https://huggingface.co/Foke121/all-MiniLM-L6-v2/tree/9f863a7023f9b07603e886e833f6a09b5dc3ac6a) |
| [fpc/bge-micro-smiles](https://huggingface.co/fpc/bge-micro-smiles) | Format compatible | 384 | 512 | Apache-2.0 | Format | [`ef633dd96f5f`](https://huggingface.co/fpc/bge-micro-smiles/tree/ef633dd96f5f247816227571fb1ec5d0cd732851) |
| [gaepiaz/all-MiniLM-L6-v2-java](https://huggingface.co/gaepiaz/all-MiniLM-L6-v2-java) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`ab6d6aba6abe`](https://huggingface.co/gaepiaz/all-MiniLM-L6-v2-java/tree/ab6d6aba6abe121b853d0e3bc190b9ab5ca6cfa3) |
| [gbennani/MNLP_M3_document_encoder](https://huggingface.co/gbennani/MNLP_M3_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`7f527915ebc6`](https://huggingface.co/gbennani/MNLP_M3_document_encoder/tree/7f527915ebc69bcb417848c6818353e0fe68cbed) |
| [ggrn/bge-small-en](https://huggingface.co/ggrn/bge-small-en) | Format compatible | 384 | 512 | MIT | Format | [`8bd6d057435c`](https://huggingface.co/ggrn/bge-small-en/tree/8bd6d057435c467e21c3163c783dee9d206cad61) |
| [Gidigi/gidigi_616da35f_0003](https://huggingface.co/Gidigi/gidigi_616da35f_0003) | Format compatible | 1024 | 512 | MIT | Format | [`57b21a45b096`](https://huggingface.co/Gidigi/gidigi_616da35f_0003/tree/57b21a45b0964763417faa64736d8d585c8f7827) |
| [GRBogar/all-MiniLM-L6-v2](https://huggingface.co/GRBogar/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`2a824803908b`](https://huggingface.co/GRBogar/all-MiniLM-L6-v2/tree/2a824803908b0dee6cdbc4e245c12e710eb89444) |
| [Gswrtz/MNLP_M3_document_encoder](https://huggingface.co/Gswrtz/MNLP_M3_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`30bb0534534d`](https://huggingface.co/Gswrtz/MNLP_M3_document_encoder/tree/30bb0534534dd268e32d391fce553c128510e288) |
| [harishsaravanan2408/all-MiniLM-L6-v2](https://huggingface.co/harishsaravanan2408/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`aa109a5b07e2`](https://huggingface.co/harishsaravanan2408/all-MiniLM-L6-v2/tree/aa109a5b07e24e9db2d8fb7fe6dc35132cc4f45f) |
| [harsh-cisco/bge-small-en-v1.5](https://huggingface.co/harsh-cisco/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`4f5c2b90e1c6`](https://huggingface.co/harsh-cisco/bge-small-en-v1.5/tree/4f5c2b90e1c6e43a0cf7f32483f9da9bd3b6fedc) |
| [harsh-wk/long-seq-paraphrase-MiniLM-L6-v2](https://huggingface.co/harsh-wk/long-seq-paraphrase-MiniLM-L6-v2) | Format compatible | 384 | 512 | Apache-2.0 | Format | [`16d7635c5e29`](https://huggingface.co/harsh-wk/long-seq-paraphrase-MiniLM-L6-v2/tree/16d7635c5e29f40a9c9c095ac81d0f679f5298c4) |
| [Harveyuyu/all-MiniLM-L6-v2](https://huggingface.co/Harveyuyu/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`e98e79ad4685`](https://huggingface.co/Harveyuyu/all-MiniLM-L6-v2/tree/e98e79ad468598599e4b8da285c4edb1184e7315) |
| [HashNuke/all-MiniLM-L12-v2-coreml](https://huggingface.co/HashNuke/all-MiniLM-L12-v2-coreml) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`ce93a3f73223`](https://huggingface.co/HashNuke/all-MiniLM-L12-v2-coreml/tree/ce93a3f732230aa853ccebffa14971797a2a0138) |
| [hyrinmansoor/text2frappe-s2-sbert](https://huggingface.co/hyrinmansoor/text2frappe-s2-sbert) | Format compatible | 384 | 128 | Not declared | Format | [`ad649dd3de54`](https://huggingface.co/hyrinmansoor/text2frappe-s2-sbert/tree/ad649dd3de5424a10bd2c901c377f54372268425) |
| [imaneb942/MNLP_M2_document_encoder](https://huggingface.co/imaneb942/MNLP_M2_document_encoder) | Format compatible | 1024 | 512 | MIT | Format | [`90e6ade2de50`](https://huggingface.co/imaneb942/MNLP_M2_document_encoder/tree/90e6ade2de509a5ba03610168f090b769d9a6f9f) |
| [imaneb942/MNLP_M3_document_encoder](https://huggingface.co/imaneb942/MNLP_M3_document_encoder) | Format compatible | 1024 | 512 | Not declared | Format | [`17dc8fa565e2`](https://huggingface.co/imaneb942/MNLP_M3_document_encoder/tree/17dc8fa565e23ad380ab77c78111960400ecc690) |
| [inesaltemir/MNLP_M3_document_encoder](https://huggingface.co/inesaltemir/MNLP_M3_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`73ca669a0fbe`](https://huggingface.co/inesaltemir/MNLP_M3_document_encoder/tree/73ca669a0fbe57d29476fda3cd0feca4e1c85545) |
| [intfloat/e5-large-v2](https://huggingface.co/intfloat/e5-large-v2) | Conditional | 1024 | 512 | MIT | Format; Configuration | [`f169b11e22de`](https://huggingface.co/intfloat/e5-large-v2/tree/f169b11e22de13617baa190a028a32f3493550b6) |
| [isuruwijesiri/all-MiniLM-L12-v2-code-search-512](https://huggingface.co/isuruwijesiri/all-MiniLM-L12-v2-code-search-512) | Format compatible | 384 | 512 | Apache-2.0 | Format; Configuration | [`0574cd81b67a`](https://huggingface.co/isuruwijesiri/all-MiniLM-L12-v2-code-search-512/tree/0574cd81b67ad333192c62bb5da302bec71818fe) |
| [isuruwijesiri/all-MiniLM-L6-v2-code-search-512](https://huggingface.co/isuruwijesiri/all-MiniLM-L6-v2-code-search-512) | Format compatible | 384 | 512 | Apache-2.0 | Format; Configuration | [`13b266a61703`](https://huggingface.co/isuruwijesiri/all-MiniLM-L6-v2-code-search-512/tree/13b266a617039c16d924b49a56ae978dbd8727ff) |
| [jacgandres/fine-tunning-embeddings](https://huggingface.co/jacgandres/fine-tunning-embeddings) | Format compatible | 384 | 256 | Not declared | Format | [`c0907fccb9e9`](https://huggingface.co/jacgandres/fine-tunning-embeddings/tree/c0907fccb9e9bf232d0e5f1d4263d0d727bbb7de) |
| [Jenjamin3000/MNLP_M2_document_encoder](https://huggingface.co/Jenjamin3000/MNLP_M2_document_encoder) | Format compatible | 1024 | 512 | MIT | Format | [`89448936a087`](https://huggingface.co/Jenjamin3000/MNLP_M2_document_encoder/tree/89448936a0871ea903a005a036540150c6bcd993) |
| [jhall0310/all-MiniLM-L6-v2](https://huggingface.co/jhall0310/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`1af2c182aa8f`](https://huggingface.co/jhall0310/all-MiniLM-L6-v2/tree/1af2c182aa8f1802b8f1464e969799b3a97cc177) |
| [jkm47/all-MiniLM-L6-v2](https://huggingface.co/jkm47/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`b061c55746fa`](https://huggingface.co/jkm47/all-MiniLM-L6-v2/tree/b061c55746fa12a8df4334f7ebc7b69ba433e86d) |
| [joelnishanthreddy/clipper-bge-base-en](https://huggingface.co/joelnishanthreddy/clipper-bge-base-en) | Format compatible | 768 | 512 | MIT | Format | [`c33352a4ffd5`](https://huggingface.co/joelnishanthreddy/clipper-bge-base-en/tree/c33352a4ffd5da814247f42651b4de54942ea176) |
| [JSCreatorPro/offline-cpu-bge-base-en](https://huggingface.co/JSCreatorPro/offline-cpu-bge-base-en) | Format compatible | 768 | 512 | MIT | Format | [`4fbc4a957196`](https://huggingface.co/JSCreatorPro/offline-cpu-bge-base-en/tree/4fbc4a957196a6d35d8796dafea83febdb6b4be9) |
| [JSCreatorPro/offline-cpu-bge-small-en](https://huggingface.co/JSCreatorPro/offline-cpu-bge-small-en) | Format compatible | 384 | 512 | MIT | Format | [`70173bec8ba6`](https://huggingface.co/JSCreatorPro/offline-cpu-bge-small-en/tree/70173bec8ba64e0597dd94dfb09b382acde8525c) |
| [JSCreatorPro/offline-cpu-e5-large-v2](https://huggingface.co/JSCreatorPro/offline-cpu-e5-large-v2) | Conditional | 1024 | 512 | MIT | Format | [`f7ee52706d51`](https://huggingface.co/JSCreatorPro/offline-cpu-e5-large-v2/tree/f7ee52706d51b9c3d4f04129e80ef52498497551) |
| [julpiro/MNLP_M3_document_encoder](https://huggingface.co/julpiro/MNLP_M3_document_encoder) | Format compatible | 768 | 512 | MIT | Format | [`f03947acb9a9`](https://huggingface.co/julpiro/MNLP_M3_document_encoder/tree/f03947acb9a9a6390d598f661232b9e8446ce799) |
| [jxsun123/all-MiniLM-L6-v2](https://huggingface.co/jxsun123/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`6df36f31b2e0`](https://huggingface.co/jxsun123/all-MiniLM-L6-v2/tree/6df36f31b2e090ee1e9808d697fd0bf70be5ee25) |
| [Karmukilan/all-MiniLM-L6-v2](https://huggingface.co/Karmukilan/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`ec2a666cf4fd`](https://huggingface.co/Karmukilan/all-MiniLM-L6-v2/tree/ec2a666cf4fdb4ab60df5fa15843c5e75cf46961) |
| [Karmukilan/e5-base-v2](https://huggingface.co/Karmukilan/e5-base-v2) | Conditional | 768 | 512 | MIT | Format | [`ca78b60b7f07`](https://huggingface.co/Karmukilan/e5-base-v2/tree/ca78b60b7f07ce1c3365656ee55ce7bfd77931c1) |
| [katanemo/bge-large-en-v1.5](https://huggingface.co/katanemo/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format | [`c495a59773a7`](https://huggingface.co/katanemo/bge-large-en-v1.5/tree/c495a59773a79714b4b11aed4cceeb5320805285) |
| [kendy2026/all-MiniLM-L6-v2](https://huggingface.co/kendy2026/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`97dec3ac619f`](https://huggingface.co/kendy2026/all-MiniLM-L6-v2/tree/97dec3ac619fe623eb3fa0b684879762bd2e80ed) |
| [kevbattle/all-MiniLM-L6-v2](https://huggingface.co/kevbattle/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`43a629090a2e`](https://huggingface.co/kevbattle/all-MiniLM-L6-v2/tree/43a629090a2eee88f8f8b75ed5f70ae30b6ba563) |
| [KirinWong/bge-large-en-v1.5](https://huggingface.co/KirinWong/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format | [`fe35c6b1ead9`](https://huggingface.co/KirinWong/bge-large-en-v1.5/tree/fe35c6b1ead9a457fbceb2022ed7e13c0f2b6558) |
| [Koat/gte-tiny](https://huggingface.co/Koat/gte-tiny) | Format compatible | 384 | 512 | Not declared | Format; Configuration | [`cc7f2d00a2ef`](https://huggingface.co/Koat/gte-tiny/tree/cc7f2d00a2efbdf16f680536413f83a1f96e87bd) |
| [kurier-digital/geronimo-base](https://huggingface.co/kurier-digital/geronimo-base) | Format compatible | 768 | 512 | Not declared | Format | [`80599db4f120`](https://huggingface.co/kurier-digital/geronimo-base/tree/80599db4f120e643bd9616a1eafe92e1c3a2c323) |
| [kurtletoez/all-MiniLM-L6-v2](https://huggingface.co/kurtletoez/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`3340bb734838`](https://huggingface.co/kurtletoez/all-MiniLM-L6-v2/tree/3340bb7348384410d99555952a8842d0f31f0f6b) |
| [laowang11111/all-MiniLM-L6-v2](https://huggingface.co/laowang11111/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`b3d09c40631a`](https://huggingface.co/laowang11111/all-MiniLM-L6-v2/tree/b3d09c40631a6e3ebbefc4cb34126368228200d5) |
| [ligenuis/all-MiniLM-L6-v2](https://huggingface.co/ligenuis/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`e472f76e35b5`](https://huggingface.co/ligenuis/all-MiniLM-L6-v2/tree/e472f76e35b5e17248061915b6e90ce9bb1c880c) |
| [Long0409/bge-small-en-v1.5](https://huggingface.co/Long0409/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`732d86550faa`](https://huggingface.co/Long0409/bge-small-en-v1.5/tree/732d86550faa5bcae5ac1031d5cabe0c91e0cd56) |
| [Lucky94629/gte-small-zh-onnx](https://huggingface.co/Lucky94629/gte-small-zh-onnx) | Format compatible | 512 | 512 | MIT | Format | [`3426b53af79d`](https://huggingface.co/Lucky94629/gte-small-zh-onnx/tree/3426b53af79d6ca79f22b81a67e34616053702a6) |
| [LuckyJason/bge-small-en-v1.5](https://huggingface.co/LuckyJason/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`d97fa1dc74a4`](https://huggingface.co/LuckyJason/bge-small-en-v1.5/tree/d97fa1dc74a42f431ad444201bfc25faa4f93850) |
| [Luther123456/all-MiniLM-L6-v2](https://huggingface.co/Luther123456/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`784e05378895`](https://huggingface.co/Luther123456/all-MiniLM-L6-v2/tree/784e053788955ba90351716237ad8569f1a8e531) |
| [MadishettiMahesh/all-MiniLM-L6-v2](https://huggingface.co/MadishettiMahesh/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`0b55f3263f10`](https://huggingface.co/MadishettiMahesh/all-MiniLM-L6-v2/tree/0b55f3263f100911ed542cbfeeb1dd2673a1b326) |
| [mainbrains/all-MiniLM-L6-v2](https://huggingface.co/mainbrains/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`7849fc1a0da7`](https://huggingface.co/mainbrains/all-MiniLM-L6-v2/tree/7849fc1a0da734aadd2fbb75a06542fbc7dc4d5e) |
| [Mass-14/MNLP_M3_document_encoder](https://huggingface.co/Mass-14/MNLP_M3_document_encoder) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`b4f540493661`](https://huggingface.co/Mass-14/MNLP_M3_document_encoder/tree/b4f54049366185075df6db83c77823f29fc60491) |
| [Matisse6410/MNLP_M2_document_encoder](https://huggingface.co/Matisse6410/MNLP_M2_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`41139a5394bf`](https://huggingface.co/Matisse6410/MNLP_M2_document_encoder/tree/41139a5394bf18631c9cb888670107a571703ead) |
| [Matisse6410/MNLP_M3_document_encoder](https://huggingface.co/Matisse6410/MNLP_M3_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`42e583a50f7e`](https://huggingface.co/Matisse6410/MNLP_M3_document_encoder/tree/42e583a50f7ec9ddab62f5103e1cbe79c39a7d94) |
| [michaelfeil/bge-small-en-v1.5](https://huggingface.co/michaelfeil/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`ab7b31bd10f9`](https://huggingface.co/michaelfeil/bge-small-en-v1.5/tree/ab7b31bd10f9bfbb915a28662ec4726b06c6552a) |
| [MichaelRodaRober/all-MiniLM-L6-v2](https://huggingface.co/MichaelRodaRober/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`948f034d7e30`](https://huggingface.co/MichaelRodaRober/all-MiniLM-L6-v2/tree/948f034d7e30cf5126fe47db89d379ef57f7608f) |
| [Mihaiii/Bulbasaur](https://huggingface.co/Mihaiii/Bulbasaur) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`6876f839e18a`](https://huggingface.co/Mihaiii/Bulbasaur/tree/6876f839e18ae36224049a41194a431953f08747) |
| [Mihaiii/gte-micro](https://huggingface.co/Mihaiii/gte-micro) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`6fd2397cb9df`](https://huggingface.co/Mihaiii/gte-micro/tree/6fd2397cb9dfa7c901aedf9a2a44d3c888ccafdd) |
| [Mihaiii/gte-micro-v2](https://huggingface.co/Mihaiii/gte-micro-v2) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`fa182885f6c1`](https://huggingface.co/Mihaiii/gte-micro-v2/tree/fa182885f6c1738dde53f28416685e1c8e1d4057) |
| [Mihaiii/gte-micro-v3](https://huggingface.co/Mihaiii/gte-micro-v3) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`2e9aa88864c0`](https://huggingface.co/Mihaiii/gte-micro-v3/tree/2e9aa88864c0a43095eafc464408cf956010b9b2) |
| [Mihaiii/gte-micro-v4](https://huggingface.co/Mihaiii/gte-micro-v4) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`78e1a4b348f8`](https://huggingface.co/Mihaiii/gte-micro-v4/tree/78e1a4b348f8524c3ab2e3e3475788f5adb8c98f) |
| [Mihaiii/Ivysaur](https://huggingface.co/Mihaiii/Ivysaur) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`65914d976f45`](https://huggingface.co/Mihaiii/Ivysaur/tree/65914d976f45beb4bda7485c39d88865b4ce6554) |
| [Mihaiii/Squirtle](https://huggingface.co/Mihaiii/Squirtle) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`5b991da48a92`](https://huggingface.co/Mihaiii/Squirtle/tree/5b991da48a9286637a256d4a35aab87a1a57b78a) |
| [Mihaiii/test24](https://huggingface.co/Mihaiii/test24) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`ba104dc5b85e`](https://huggingface.co/Mihaiii/test24/tree/ba104dc5b85e556a7462e06d18cb1d82e4f8a0bc) |
| [Mihaiii/test25](https://huggingface.co/Mihaiii/test25) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`e16685954cbf`](https://huggingface.co/Mihaiii/test25/tree/e16685954cbf72a44cbe640c3af7c031b55b1c64) |
| [Mihaiii/Venusaur](https://huggingface.co/Mihaiii/Venusaur) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`0dc817f0addb`](https://huggingface.co/Mihaiii/Venusaur/tree/0dc817f0addbb7bab8feeeeaded538f9ffeb3419) |
| [Mihaiii/Wartortle](https://huggingface.co/Mihaiii/Wartortle) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`14caca525341`](https://huggingface.co/Mihaiii/Wartortle/tree/14caca5253414d38a7d28b62d1b7c30ef3293a87) |
| [minna-app/bge-small-en-v1.5-coreml](https://huggingface.co/minna-app/bge-small-en-v1.5-coreml) | Format compatible | 384 | 512 | MIT | Format | [`cc41af467623`](https://huggingface.co/minna-app/bge-small-en-v1.5-coreml/tree/cc41af467623b0047fa531bf0e5ff680574a2561) |
| [mixedbread-ai/mxbai-embed-2d-large-v1](https://huggingface.co/mixedbread-ai/mxbai-embed-2d-large-v1) | Format compatible | 1024 | 512 | Apache-2.0 | Format | [`416c45d154ee`](https://huggingface.co/mixedbread-ai/mxbai-embed-2d-large-v1/tree/416c45d154ee4e435b6d19f6bd54440f38a12ee9) |
| [mixedbread-ai/mxbai-embed-xsmall-v1](https://huggingface.co/mixedbread-ai/mxbai-embed-xsmall-v1) | Format compatible | 384 | 512 | Apache-2.0 | Format; Configuration | [`e6ac24e5d6ef`](https://huggingface.co/mixedbread-ai/mxbai-embed-xsmall-v1/tree/e6ac24e5d6efb8782b59de1647b3ececb4ece94e) |
| [mmine/all-MiniLM-L6-v2](https://huggingface.co/mmine/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`9f9e23ebfd41`](https://huggingface.co/mmine/all-MiniLM-L6-v2/tree/9f9e23ebfd41ca66983b75efdb62636df5594e61) |
| [Mohamed-Sami-Ghrab/MNLP_M2_document_encoder](https://huggingface.co/Mohamed-Sami-Ghrab/MNLP_M2_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`2adae6c55042`](https://huggingface.co/Mohamed-Sami-Ghrab/MNLP_M2_document_encoder/tree/2adae6c5504234e86dff1d936ba4d797c9c8595d) |
| [Mohamed-Sami-Ghrab/MNLP_M3_document_encoder](https://huggingface.co/Mohamed-Sami-Ghrab/MNLP_M3_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`de5e5b2130b0`](https://huggingface.co/Mohamed-Sami-Ghrab/MNLP_M3_document_encoder/tree/de5e5b2130b095f32214592dcb4f5fc2bfca541a) |
| [MoralHazard/GISTsmall](https://huggingface.co/MoralHazard/GISTsmall) | Format compatible | 384 | 512 | Not declared | Format; Configuration | [`01640ed2b38e`](https://huggingface.co/MoralHazard/GISTsmall/tree/01640ed2b38e2814cde38f7ab15379a910cb330e) |
| [Mozilla/smart-tab-embedding](https://huggingface.co/Mozilla/smart-tab-embedding) | Format compatible | 384 | 256 | Not declared | Format; Configuration | [`ddd107757e10`](https://huggingface.co/Mozilla/smart-tab-embedding/tree/ddd107757e101aae901ee8667f8ac309dc7f3e93) |
| [Mozilla/smart-tab-embedding-fine-tuned](https://huggingface.co/Mozilla/smart-tab-embedding-fine-tuned) | Format compatible | 384 | 256 | Not declared | Format; Configuration | [`9a36b42dec82`](https://huggingface.co/Mozilla/smart-tab-embedding-fine-tuned/tree/9a36b42dec82579459e9edfd0a6da99c5aab25f5) |
| [MusYW/MNLP_M3_document_encoder](https://huggingface.co/MusYW/MNLP_M3_document_encoder) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`21d01726c169`](https://huggingface.co/MusYW/MNLP_M3_document_encoder/tree/21d01726c1690e42903cc378474cc680fee52b50) |
| [nikhilgawai007/gte-small](https://huggingface.co/nikhilgawai007/gte-small) | Format compatible | 384 | 512 | MIT | Format | [`7638f675ca19`](https://huggingface.co/nikhilgawai007/gte-small/tree/7638f675ca1933a90931079990a8eed7a9f52483) |
| [Nil626yhh/all-MiniLM-L6-v2](https://huggingface.co/Nil626yhh/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`3015d9ae7215`](https://huggingface.co/Nil626yhh/all-MiniLM-L6-v2/tree/3015d9ae72151d50953e37ab85fb2d483d49c281) |
| [nilay-sam-23/bge-small-en-v1.5-onnx](https://huggingface.co/nilay-sam-23/bge-small-en-v1.5-onnx) | Format compatible | 384 | 512 | MIT | Format | [`4f1021cf22b7`](https://huggingface.co/nilay-sam-23/bge-small-en-v1.5-onnx/tree/4f1021cf22b729eee8db595dbd87325978261e7e) |
| [nitsuai/bge-large-en-v1.5](https://huggingface.co/nitsuai/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format | [`d00beaaa78ba`](https://huggingface.co/nitsuai/bge-large-en-v1.5/tree/d00beaaa78ba8c8e4e810c10f5bff89f7634601b) |
| [novelcore/model1](https://huggingface.co/novelcore/model1) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`18c5b019b40d`](https://huggingface.co/novelcore/model1/tree/18c5b019b40df478f9a15b2e6aa3a63d0e83a4a1) |
| [novelcore/model11](https://huggingface.co/novelcore/model11) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`e3773152c2ea`](https://huggingface.co/novelcore/model11/tree/e3773152c2ea586b78a9eac5ff952d7fe54a2429) |
| [novelcore/model13](https://huggingface.co/novelcore/model13) | Format compatible | 384 | 512 | MIT | Format | [`3d34cf60140c`](https://huggingface.co/novelcore/model13/tree/3d34cf60140c4aacbdfddb5baeeae021c474726c) |
| [novelcore/model14](https://huggingface.co/novelcore/model14) | Format compatible | 384 | 384 | Not declared | Format | [`b8a92eb70cc7`](https://huggingface.co/novelcore/model14/tree/b8a92eb70cc72c55d2e46fc4891bab13f3bca09b) |
| [novelcore/model15](https://huggingface.co/novelcore/model15) | Format compatible | 384 | 512 | MIT | Format | [`b769a645b1a1`](https://huggingface.co/novelcore/model15/tree/b769a645b1a171336bd1d040bfb2a50a544589eb) |
| [novelcore/model16](https://huggingface.co/novelcore/model16) | Format compatible | 768 | 512 | MIT | Format | [`1de6e698e490`](https://huggingface.co/novelcore/model16/tree/1de6e698e4903113155aee7f50622919db2562b8) |
| [novelcore/model17](https://huggingface.co/novelcore/model17) | Format compatible | 768 | 512 | MIT | Format | [`a565dd6bbcc2`](https://huggingface.co/novelcore/model17/tree/a565dd6bbcc2d1d58c51d971d406a62957c9929f) |
| [novelcore/model2](https://huggingface.co/novelcore/model2) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`f6097452c055`](https://huggingface.co/novelcore/model2/tree/f6097452c0551ba272afa46b2fd358c1fbe43b7e) |
| [novelcore/model3](https://huggingface.co/novelcore/model3) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`bdd755179bb6`](https://huggingface.co/novelcore/model3/tree/bdd755179bb607ba0109ff2a9837e56eac0de5ac) |
| [novelcore/model4](https://huggingface.co/novelcore/model4) | Format compatible | 384 | 512 | Not declared | Format | [`7246b248261c`](https://huggingface.co/novelcore/model4/tree/7246b248261c5c76b0ac489fc78172ccff53d27e) |
| [novelcore/model5](https://huggingface.co/novelcore/model5) | Format compatible | 384 | 384 | Not declared | Format | [`03b9547279f2`](https://huggingface.co/novelcore/model5/tree/03b9547279f2f2f278554604ca20b863bf87b966) |
| [ogustpazjuniorgmailcom/all-MiniLM-L6-v2](https://huggingface.co/ogustpazjuniorgmailcom/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`e350f997469f`](https://huggingface.co/ogustpazjuniorgmailcom/all-MiniLM-L6-v2/tree/e350f997469f930ced9d1535f1bf003ec6eda070) |
| [ohnokayden/bge-large-en-v1.5](https://huggingface.co/ohnokayden/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format | [`3e309527e176`](https://huggingface.co/ohnokayden/bge-large-en-v1.5/tree/3e309527e176153cc186531d76d80c3784080699) |
| [OJASSharma1407/all-MiniLM-L6-v2](https://huggingface.co/OJASSharma1407/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`999cd3459821`](https://huggingface.co/OJASSharma1407/all-MiniLM-L6-v2/tree/999cd34598214f78b3558771b4fd3d80d425dfc9) |
| [OmarIDK/MNLP_M2_document_encoder](https://huggingface.co/OmarIDK/MNLP_M2_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`55b7faaad75e`](https://huggingface.co/OmarIDK/MNLP_M2_document_encoder/tree/55b7faaad75e560b3c157abe9123a0a47d9d82a0) |
| [OpenFlowLM/bge-base-en-v1.5](https://huggingface.co/OpenFlowLM/bge-base-en-v1.5) | Format compatible | 768 | 512 | MIT | Format | [`59c677f5bbea`](https://huggingface.co/OpenFlowLM/bge-base-en-v1.5/tree/59c677f5bbea2dcbf4addc0db44a6d73a1e76d82) |
| [optimum-intel-internal-testing/all-MiniLM-L6-v2](https://huggingface.co/optimum-intel-internal-testing/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`75350ccb303c`](https://huggingface.co/optimum-intel-internal-testing/all-MiniLM-L6-v2/tree/75350ccb303c8b52f5b103ed9f9dbf79611de0a2) |
| [optimum-intel-internal-testing/bge-small-en-v1.5](https://huggingface.co/optimum-intel-internal-testing/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`d040c217cdf2`](https://huggingface.co/optimum-intel-internal-testing/bge-small-en-v1.5/tree/d040c217cdf2a3191a5d45c2e2b7caecb24d68a5) |
| [orange-21/all-MiniLM-L6-v2](https://huggingface.co/orange-21/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`ebc9f05de3f7`](https://huggingface.co/orange-21/all-MiniLM-L6-v2/tree/ebc9f05de3f71c1a7f35b5e8a6bc006d4f0b821a) |
| [ori-ron/all-MiniLM-L6-v2](https://huggingface.co/ori-ron/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`d19703aa78fb`](https://huggingface.co/ori-ron/all-MiniLM-L6-v2/tree/d19703aa78fbb357e7f2e70a7a40d72031104f40) |
| [overfitting-co/A2P-constrastive-all](https://huggingface.co/overfitting-co/A2P-constrastive-all) | Format compatible | 384 | 256 | Not declared | Format | [`6333d91f9cb6`](https://huggingface.co/overfitting-co/A2P-constrastive-all/tree/6333d91f9cb6a1895113a5b995538bc48ee27a25) |
| [ozziek/all-MiniLM-L6-v2-lasttoken-false](https://huggingface.co/ozziek/all-MiniLM-L6-v2-lasttoken-false) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`4f988d922db9`](https://huggingface.co/ozziek/all-MiniLM-L6-v2-lasttoken-false/tree/4f988d922db92e3698c4226f11928eba349aeacf) |
| [pashaa/bge-base-en-v1.5](https://huggingface.co/pashaa/bge-base-en-v1.5) | Format compatible | 768 | 512 | MIT | Format | [`057446fc0c65`](https://huggingface.co/pashaa/bge-base-en-v1.5/tree/057446fc0c6586042d6233e5dfca967ba1794dcd) |
| [Paulanerus/AncientGreekVariantSBERT-ONNX](https://huggingface.co/Paulanerus/AncientGreekVariantSBERT-ONNX) | Format compatible | 768 | 512 | MIT | Format; Configuration | [`d17c4b5ff234`](https://huggingface.co/Paulanerus/AncientGreekVariantSBERT-ONNX/tree/d17c4b5ff234a96d77ccfdeb10c1d2abcff36286) |
| [pettertonar/kblab_sbert_onnx_03](https://huggingface.co/pettertonar/kblab_sbert_onnx_03) | Format compatible | 768 | 384 | Apache-2.0 | Format | [`c52327fd5de9`](https://huggingface.co/pettertonar/kblab_sbert_onnx_03/tree/c52327fd5de92d95521d7f2eed4faee72148e56f) |
| [pgilliar/MNLP_M2_document_encoder](https://huggingface.co/pgilliar/MNLP_M2_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`58ecaad3cb3d`](https://huggingface.co/pgilliar/MNLP_M2_document_encoder/tree/58ecaad3cb3dba34807dd7dd78a84e30175881ae) |
| [pgilliar/MNLP_M3_document_encoder](https://huggingface.co/pgilliar/MNLP_M3_document_encoder) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`664ccab471ac`](https://huggingface.co/pgilliar/MNLP_M3_document_encoder/tree/664ccab471acb73cf394b8c83e67d8dcb3c9098a) |
| [phob0s/paraphrase-MiniLM-L3-v2](https://huggingface.co/phob0s/paraphrase-MiniLM-L3-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`a3dde29805ab`](https://huggingface.co/phob0s/paraphrase-MiniLM-L3-v2/tree/a3dde29805abb62d6a076ba4c12fec0618f5e5b6) |
| [pramudyalyza/asktoal-model-v2](https://huggingface.co/pramudyalyza/asktoal-model-v2) | Format compatible | 384 | 256 | Not declared | Format | [`21f436b3dff8`](https://huggingface.co/pramudyalyza/asktoal-model-v2/tree/21f436b3dff8f8b3bc000dfe20d9179a6bad8713) |
| [QubeBox/all-MiniLM-L6-v2](https://huggingface.co/QubeBox/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`836ae588f79e`](https://huggingface.co/QubeBox/all-MiniLM-L6-v2/tree/836ae588f79efd92b62d46a36d9507cbd934345e) |
| [rawsh/multi-qa-MiniLM-BERT-Tiny-distill-L-2_H-128_A-cos-v1](https://huggingface.co/rawsh/multi-qa-MiniLM-BERT-Tiny-distill-L-2_H-128_A-cos-v1) | Format compatible | 128 | 512 | Not declared | Format | [`aa45a20d3fc0`](https://huggingface.co/rawsh/multi-qa-MiniLM-BERT-Tiny-distill-L-2_H-128_A-cos-v1/tree/aa45a20d3fc074dba19910c5d43475eea7e33e0f) |
| [rawsh/multi-qa-MiniLM-distill-onnx-L6-cos-v1](https://huggingface.co/rawsh/multi-qa-MiniLM-distill-onnx-L6-cos-v1) | Format compatible | 384 | 512 | Not declared | Format | [`6c5a75beceeb`](https://huggingface.co/rawsh/multi-qa-MiniLM-distill-onnx-L6-cos-v1/tree/6c5a75beceeb83a8309f9e1b263f2a678afaa8da) |
| [RedHatAI/all-MiniLM-L6-v2](https://huggingface.co/RedHatAI/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`9055ec5aa130`](https://huggingface.co/RedHatAI/all-MiniLM-L6-v2/tree/9055ec5aa13059b4c7a50cbdc26a55c8d38ea8f9) |
| [Renegad/all-MiniLM-L6-v2](https://huggingface.co/Renegad/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`d6da6b27d57b`](https://huggingface.co/Renegad/all-MiniLM-L6-v2/tree/d6da6b27d57bc01778789c3f9bdad5add703d6ed) |
| [rishi002/all-MiniLM-L6-v2](https://huggingface.co/rishi002/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`e002803f943c`](https://huggingface.co/rishi002/all-MiniLM-L6-v2/tree/e002803f943cb71dca68f2e983321fab6a6ba98c) |
| [Ruthvikkk/MNLP_M2_document_encoder](https://huggingface.co/Ruthvikkk/MNLP_M2_document_encoder) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`17e1f347d17f`](https://huggingface.co/Ruthvikkk/MNLP_M2_document_encoder/tree/17e1f347d17fe144873b1201da91788898c639cd) |
| [Ruthvikkk/MNLP_M3_document_encoder](https://huggingface.co/Ruthvikkk/MNLP_M3_document_encoder) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`2bfd601d4de7`](https://huggingface.co/Ruthvikkk/MNLP_M3_document_encoder/tree/2bfd601d4de769647b2c2734607ada25ce74dbb1) |
| [sai1000/all-MiniLM-L6-v2](https://huggingface.co/sai1000/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`c7870c59fa94`](https://huggingface.co/sai1000/all-MiniLM-L6-v2/tree/c7870c59fa9478e3b5d912bf2f243b284ff471c2) |
| [sakthivelt/all-MiniLM-L6-v2](https://huggingface.co/sakthivelt/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`53f280a8d1d4`](https://huggingface.co/sakthivelt/all-MiniLM-L6-v2/tree/53f280a8d1d496c1066f7bf8d4cfa87028a42c47) |
| [Samhita-kolluri/all-MiniLM-L6-v2-sk](https://huggingface.co/Samhita-kolluri/all-MiniLM-L6-v2-sk) | Format compatible | 384 | 256 | Apache-2.0 | Format; Configuration | [`dbed7c2f8450`](https://huggingface.co/Samhita-kolluri/all-MiniLM-L6-v2-sk/tree/dbed7c2f845075c481d163b2006b6e8e5b232175) |
| [sandeep42342/all-MiniLM-L6-v2](https://huggingface.co/sandeep42342/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`83c250a40348`](https://huggingface.co/sandeep42342/all-MiniLM-L6-v2/tree/83c250a403480b7c97efe5abf74993031ad62a7d) |
| [sayak9196/all-MiniLM-L6-v2](https://huggingface.co/sayak9196/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`f47c63d9501a`](https://huggingface.co/sayak9196/all-MiniLM-L6-v2/tree/f47c63d9501addf0bd12e91cfd414b833c3f739f) |
| [sentence-transformers-testing/stsb-bert-tiny-onnx](https://huggingface.co/sentence-transformers-testing/stsb-bert-tiny-onnx) | Format compatible | 128 | 512 | Not declared | Format | [`da60a9ed87ae`](https://huggingface.co/sentence-transformers-testing/stsb-bert-tiny-onnx/tree/da60a9ed87aeb7e57d9b15be5d021b28d153f584) |
| [ShaunGves/all-MiniLM-L6-v2](https://huggingface.co/ShaunGves/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`82cca4d8dc1c`](https://huggingface.co/ShaunGves/all-MiniLM-L6-v2/tree/82cca4d8dc1c8a50aaf3c47ef2799155b040d8a8) |
| [sheldonrobinson/all-MiniLM-L12-v2](https://huggingface.co/sheldonrobinson/all-MiniLM-L12-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`30ce63ae64e7`](https://huggingface.co/sheldonrobinson/all-MiniLM-L12-v2/tree/30ce63ae64e71b9199b3d2eae9de99f64a26eedc) |
| [sheldonrobinson/all-MiniLM-L6-v2](https://huggingface.co/sheldonrobinson/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`f73063f6e781`](https://huggingface.co/sheldonrobinson/all-MiniLM-L6-v2/tree/f73063f6e781f67d0addc25cac133aec21120b17) |
| [shibing624/text2vec-bge-large-chinese](https://huggingface.co/shibing624/text2vec-bge-large-chinese) | Format compatible | 1024 | 512 | Apache-2.0 | Format | [`1722fff91957`](https://huggingface.co/shibing624/text2vec-bge-large-chinese/tree/1722fff91957cf9f6bd8f125f2e1178ddfc9b4cc) |
| [sky-meilin/all-MiniLM-L6-v2](https://huggingface.co/sky-meilin/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`ece62ec5160b`](https://huggingface.co/sky-meilin/all-MiniLM-L6-v2/tree/ece62ec5160b4863fd7e687928acdda69ef45dba) |
| [SmartComponents/bge-micro-v2](https://huggingface.co/SmartComponents/bge-micro-v2) | Format compatible | 384 | 512 | Not declared | Format; Configuration | [`7ec22bc99290`](https://huggingface.co/SmartComponents/bge-micro-v2/tree/7ec22bc9929092529cc25e9debdfa88509eb4dbf) |
| [sokeieqw/all-MiniLM-L6-v2](https://huggingface.co/sokeieqw/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`1482cf2a4c62`](https://huggingface.co/sokeieqw/all-MiniLM-L6-v2/tree/1482cf2a4c6245afb0191360e120ab2e4bd693d8) |
| [sox90/all-MiniLM-L6-v2](https://huggingface.co/sox90/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`f3c5f627e4bd`](https://huggingface.co/sox90/all-MiniLM-L6-v2/tree/f3c5f627e4bd4bd445a552d53ff2db04f921f5c8) |
| [staccs/lecore-bge-assimilated](https://huggingface.co/staccs/lecore-bge-assimilated) | Format compatible | 768 | 512 | MIT | Format | [`4438e6e01101`](https://huggingface.co/staccs/lecore-bge-assimilated/tree/4438e6e01101acd306338c142b14c8e711a5763c) |
| [star-moon/bge-small-en-v1.5](https://huggingface.co/star-moon/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`a6fe24ce339f`](https://huggingface.co/star-moon/bge-small-en-v1.5/tree/a6fe24ce339f7f8091cb28cd686b5148f02fe8d5) |
| [Supabase/bge-small-en](https://huggingface.co/Supabase/bge-small-en) | Format compatible | 384 | 512 | MIT | Format | [`bb93e228fd9d`](https://huggingface.co/Supabase/bge-small-en/tree/bb93e228fd9d88ae832d8bc9ca5ab5ce64c7172b) |
| [syq2021/bge-base-en-v1.5](https://huggingface.co/syq2021/bge-base-en-v1.5) | Format compatible | 768 | 512 | MIT | Format | [`fc357f4c4cd1`](https://huggingface.co/syq2021/bge-base-en-v1.5/tree/fc357f4c4cd184c8e9b1a06afd7967c59f29eb74) |
| [t4gandhi/all-MiniLM-L6-v2](https://huggingface.co/t4gandhi/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`c5997a934963`](https://huggingface.co/t4gandhi/all-MiniLM-L6-v2/tree/c5997a9349638332fb0c4e9e8908f1ecd20dac1a) |
| [TaylorAI/bge-micro](https://huggingface.co/TaylorAI/bge-micro) | Format compatible | 384 | 512 | Not declared | Format; Configuration | [`4bccbd43513e`](https://huggingface.co/TaylorAI/bge-micro/tree/4bccbd43513eb9fecf444af6eecde76e55f4c839) |
| [TaylorAI/bge-micro-v2](https://huggingface.co/TaylorAI/bge-micro-v2) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`3edf6d7de0fa`](https://huggingface.co/TaylorAI/bge-micro-v2/tree/3edf6d7de0faa426b09780416fe61009f26ae589) |
| [TaylorAI/gte-tiny](https://huggingface.co/TaylorAI/gte-tiny) | Format compatible | 384 | 512 | Not declared | Format; Configuration | [`4cc5e73d86a6`](https://huggingface.co/TaylorAI/gte-tiny/tree/4cc5e73d86a67c601897257b467187234aa3bca3) |
| [terran-tech-ai/gte-small-s2orc-1mx](https://huggingface.co/terran-tech-ai/gte-small-s2orc-1mx) | Format compatible | 384 | 512 | MIT | Format | [`45715dc7d369`](https://huggingface.co/terran-tech-ai/gte-small-s2orc-1mx/tree/45715dc7d3691a6f827070733eb6d8dede7dba5e) |
| [ThembaMbulwana/all-MiniLM-L6-v2](https://huggingface.co/ThembaMbulwana/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`174470bc0846`](https://huggingface.co/ThembaMbulwana/all-MiniLM-L6-v2/tree/174470bc0846cbc29e05455504067921762344d1) |
| [thenlper/gte-base](https://huggingface.co/thenlper/gte-base) | Format compatible | 768 | 512 | MIT | Format; Configuration | [`c078288308d8`](https://huggingface.co/thenlper/gte-base/tree/c078288308d8dee004ab72c6191778064285ec0c) |
| [thenlper/gte-large](https://huggingface.co/thenlper/gte-large) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`4bef63f39fcc`](https://huggingface.co/thenlper/gte-large/tree/4bef63f39fcc5e2d6b0aae83089f307af4970164) |
| [thenlper/gte-small](https://huggingface.co/thenlper/gte-small) | Format compatible | 384 | 512 | MIT | Format; Configuration | [`17e1f347d17f`](https://huggingface.co/thenlper/gte-small/tree/17e1f347d17fe144873b1201da91788898c639cd) |
| [thisisprabalpandey/bge-base-en-v1.5](https://huggingface.co/thisisprabalpandey/bge-base-en-v1.5) | Format compatible | 768 | 512 | MIT | Format | [`40364efd3741`](https://huggingface.co/thisisprabalpandey/bge-base-en-v1.5/tree/40364efd37418188f3a658dc40c30b91e444a3df) |
| [TigerByteCyber/bge-large-en-v1.5](https://huggingface.co/TigerByteCyber/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format | [`2a5556232187`](https://huggingface.co/TigerByteCyber/bge-large-en-v1.5/tree/2a5556232187a3919f8bdefc9033a5d5e128c9c7) |
| [TigerByteCyber/bge-small-en-v1.5](https://huggingface.co/TigerByteCyber/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`afd65da133d6`](https://huggingface.co/TigerByteCyber/bge-small-en-v1.5/tree/afd65da133d6091bfde1e6f2164a39d00bc309ba) |
| [timesupcoming0001/all-MiniLM-L6-v2](https://huggingface.co/timesupcoming0001/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`eacc19df033c`](https://huggingface.co/timesupcoming0001/all-MiniLM-L6-v2/tree/eacc19df033cd7dad8831b4a83e42bc12db65b76) |
| [tomaarsen/all-MiniLM-L6-v2-backends](https://huggingface.co/tomaarsen/all-MiniLM-L6-v2-backends) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`cbf367178726`](https://huggingface.co/tomaarsen/all-MiniLM-L6-v2-backends/tree/cbf36717872643cc86bcf1c97f2d18458a3469a1) |
| [tomaarsen/mxbai-embed-large-v1-exported](https://huggingface.co/tomaarsen/mxbai-embed-large-v1-exported) | Format compatible | 1024 | 512 | Apache-2.0 | Format | [`63cda528d2b1`](https://huggingface.co/tomaarsen/mxbai-embed-large-v1-exported/tree/63cda528d2b144ba28ccfca79ec1ff04d6851b2f) |
| [Tusitala/all-MiniLM-L6-v2](https://huggingface.co/Tusitala/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`610ce8784c49`](https://huggingface.co/Tusitala/all-MiniLM-L6-v2/tree/610ce8784c4923fbbe3746acb02eb2216870390a) |
| [unsloth/all-MiniLM-L6-v2](https://huggingface.co/unsloth/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`0f79ca30c044`](https://huggingface.co/unsloth/all-MiniLM-L6-v2/tree/0f79ca30c044e92859f5852d3a29fb6e976741cd) |
| [unsloth/bge-small-en-v1.5](https://huggingface.co/unsloth/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`7382f1122c10`](https://huggingface.co/unsloth/bge-small-en-v1.5/tree/7382f1122c10708a1faa0bbe548674a14b1ffe7e) |
| [vazish/all-MiniLM-L6-v2-fine-tuned](https://huggingface.co/vazish/all-MiniLM-L6-v2-fine-tuned) | Format compatible | 384 | 256 | Not declared | Format | [`35dd1fc8b63c`](https://huggingface.co/vazish/all-MiniLM-L6-v2-fine-tuned/tree/35dd1fc8b63c3cf697568276db322a9dd8ed99c6) |
| [vectoriseai/bge-base-en-v1.5](https://huggingface.co/vectoriseai/bge-base-en-v1.5) | Format compatible | 768 | 512 | MIT | Format | [`50f86309723c`](https://huggingface.co/vectoriseai/bge-base-en-v1.5/tree/50f86309723c4923b6936c68a091bac85fda39e4) |
| [vectoriseai/bge-large-en-v1.5](https://huggingface.co/vectoriseai/bge-large-en-v1.5) | Format compatible | 1024 | 512 | MIT | Format | [`a1cd40baf61a`](https://huggingface.co/vectoriseai/bge-large-en-v1.5/tree/a1cd40baf61a1e903c90043a08987e4067dbe562) |
| [vectoriseai/bge-small-en](https://huggingface.co/vectoriseai/bge-small-en) | Format compatible | 384 | 512 | MIT | Format | [`f3152506d1d1`](https://huggingface.co/vectoriseai/bge-small-en/tree/f3152506d1d14676afcedc71338d686f2fd155ae) |
| [vectoriseai/bge-small-en-v1.5](https://huggingface.co/vectoriseai/bge-small-en-v1.5) | Format compatible | 384 | 512 | MIT | Format | [`591c291a136f`](https://huggingface.co/vectoriseai/bge-small-en-v1.5/tree/591c291a136fb1f02b1bdfd59147138320eddc76) |
| [vectoriseai/e5-base](https://huggingface.co/vectoriseai/e5-base) | Conditional | 768 | 512 | MIT | Format | [`99e557e08db3`](https://huggingface.co/vectoriseai/e5-base/tree/99e557e08db3e09f4c4c59410b613d9edd7729b0) |
| [vectoriseai/e5-base-v2](https://huggingface.co/vectoriseai/e5-base-v2) | Conditional | 768 | 512 | MIT | Format | [`72428001ff12`](https://huggingface.co/vectoriseai/e5-base-v2/tree/72428001ff12f8fd8c9542570dfecc7439903402) |
| [vectoriseai/e5-large](https://huggingface.co/vectoriseai/e5-large) | Conditional | 1024 | 512 | MIT | Format | [`6cd83325b907`](https://huggingface.co/vectoriseai/e5-large/tree/6cd83325b9072a04462fd9eaa2f5a6afb0737c50) |
| [vectoriseai/e5-large-v2](https://huggingface.co/vectoriseai/e5-large-v2) | Conditional | 1024 | 512 | MIT | Format | [`adcab1df38d9`](https://huggingface.co/vectoriseai/e5-large-v2/tree/adcab1df38d9596d6e765ed3d547413c086702cf) |
| [vectoriseai/ember-v1](https://huggingface.co/vectoriseai/ember-v1) | Format compatible | 1024 | 512 | MIT | Format | [`a1088abefbda`](https://huggingface.co/vectoriseai/ember-v1/tree/a1088abefbdabed12ddf5027b1604e1affe6998c) |
| [vectoriseai/gte-base](https://huggingface.co/vectoriseai/gte-base) | Format compatible | 768 | 512 | MIT | Format | [`b9fd30d69531`](https://huggingface.co/vectoriseai/gte-base/tree/b9fd30d69531cc17a82353ac321f1edb40e7fbd6) |
| [vectoriseai/gte-large](https://huggingface.co/vectoriseai/gte-large) | Format compatible | 1024 | 512 | MIT | Format | [`154a5ace75d4`](https://huggingface.co/vectoriseai/gte-large/tree/154a5ace75d4d094d030b3629c94868b21433668) |
| [vectoriseai/gte-small](https://huggingface.co/vectoriseai/gte-small) | Format compatible | 384 | 512 | MIT | Format | [`554a3de74026`](https://huggingface.co/vectoriseai/gte-small/tree/554a3de7402637fe9a37de142e7a33c245a22b38) |
| [vllm-project/gte-base](https://huggingface.co/vllm-project/gte-base) | Format compatible | 768 | 512 | MIT | Format | [`c5f66a9f392c`](https://huggingface.co/vllm-project/gte-base/tree/c5f66a9f392c9ad10effec7a744ca8a5b81060de) |
| [vllm-project/gte-large](https://huggingface.co/vllm-project/gte-large) | Format compatible | 1024 | 512 | MIT | Format | [`454666d9e806`](https://huggingface.co/vllm-project/gte-large/tree/454666d9e806875555b0092cdee88801bb895214) |
| [vllm-project/gte-small](https://huggingface.co/vllm-project/gte-small) | Format compatible | 384 | 512 | MIT | Format | [`32d86b14f654`](https://huggingface.co/vllm-project/gte-small/tree/32d86b14f6546c9dfc8b58a258e3b3b1bd58d1ac) |
| [w8s/all-MiniLM-L6-v2](https://huggingface.co/w8s/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`4bd4658ec347`](https://huggingface.co/w8s/all-MiniLM-L6-v2/tree/4bd4658ec3475ec38fb83c956777929de1c6c738) |
| [wellmark88/all-MiniLM-L12-v2](https://huggingface.co/wellmark88/all-MiniLM-L12-v2) | Format compatible | 384 | 128 | Apache-2.0 | Format | [`4fb325efe784`](https://huggingface.co/wellmark88/all-MiniLM-L12-v2/tree/4fb325efe7846cfc132cac72c7604d14ed026ef3) |
| [WhereIsAI/UAE-Large-V1](https://huggingface.co/WhereIsAI/UAE-Large-V1) | Conditional | 1024 | 512 | MIT | Format | [`9c9b2c999b33`](https://huggingface.co/WhereIsAI/UAE-Large-V1/tree/9c9b2c999b3350cfb3171ed429320668e39b00b8) |
| [xtlalert/ms-marco-onnx](https://huggingface.co/xtlalert/ms-marco-onnx) | Format compatible | 384 | 512 | Not declared | Format | [`d4a921d151e0`](https://huggingface.co/xtlalert/ms-marco-onnx/tree/d4a921d151e0f91ea2f58b43d85c983043d2519e) |
| [yashk2000/all-MiniLM-L6-v2](https://huggingface.co/yashk2000/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`e2b23d3b0429`](https://huggingface.co/yashk2000/all-MiniLM-L6-v2/tree/e2b23d3b0429be53ad1541750905b8162870ca3f) |
| [YenukaJ/all-MiniLM-L6-v2](https://huggingface.co/YenukaJ/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`56cabae6de57`](https://huggingface.co/YenukaJ/all-MiniLM-L6-v2/tree/56cabae6de575ae7d393ffbf2fe8ae1ea642b865) |
| [YINzhiping/all-MiniLM-L6-v2](https://huggingface.co/YINzhiping/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`84d7442b8899`](https://huggingface.co/YINzhiping/all-MiniLM-L6-v2/tree/84d7442b8899d90a407c118c20187de51d68d6fe) |
| [ykst/all-MiniLM-L6-v2](https://huggingface.co/ykst/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`3f9b62af65dd`](https://huggingface.co/ykst/all-MiniLM-L6-v2/tree/3f9b62af65ddd028b7a4874bb20b0bbba8554da4) |
| [Youssefbou62/MNLP_M2_document_encoder](https://huggingface.co/Youssefbou62/MNLP_M2_document_encoder) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`adcfc67d61bf`](https://huggingface.co/Youssefbou62/MNLP_M2_document_encoder/tree/adcfc67d61bf1fe9ccdb5127dd28864bd2b107cf) |
| [Youssefbou62/test_encoder](https://huggingface.co/Youssefbou62/test_encoder) | Format compatible | 1024 | 512 | MIT | Format; Configuration | [`49af8807584d`](https://huggingface.co/Youssefbou62/test_encoder/tree/49af8807584ded8cf2dd52292cb8711c13a01f1f) |
| [yuygg/all-MiniLM-L6-v2](https://huggingface.co/yuygg/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`0b16e574698c`](https://huggingface.co/yuygg/all-MiniLM-L6-v2/tree/0b16e574698c4d4e0790b44f5768be851d310933) |
| [Zhaoxingbing/all-MiniLM-L6-v2](https://huggingface.co/Zhaoxingbing/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`55126e29ddf4`](https://huggingface.co/Zhaoxingbing/all-MiniLM-L6-v2/tree/55126e29ddf4b318387680f61bea373fca7945fb) |
| [zhw798142627/all-MiniLM-L6-v2](https://huggingface.co/zhw798142627/all-MiniLM-L6-v2) | Format compatible | 384 | 256 | Apache-2.0 | Format | [`6244e8220cdf`](https://huggingface.co/zhw798142627/all-MiniLM-L6-v2/tree/6244e8220cdf24a939e021968f7cfcaf379884dc) |

## Unsupported selections

These are exact-URL findings from the November 2026 snapshot. A different export or a future native adapter may have different compatibility.

| Model-card URL | Compatibility limitation | Assessed revision |
|---|---|---|
| [Xenova/all-MiniLM-L6-v2](https://huggingface.co/Xenova/all-MiniLM-L6-v2) | Required files not present: 1_Pooling/config.json | [`751bff37182d`](https://huggingface.co/Xenova/all-MiniLM-L6-v2/tree/751bff37182d3f1213fa05d7196b954e230abad9) |
| [Xenova/paraphrase-MiniLM-L3-v2](https://huggingface.co/Xenova/paraphrase-MiniLM-L3-v2) | Required files not present: 1_Pooling/config.json | [`4b544e74dfc3`](https://huggingface.co/Xenova/paraphrase-MiniLM-L3-v2/tree/4b544e74dfc3256b2b56849ea5d7064fee1ac846) |
| [Xenova/bge-small-en-v1.5](https://huggingface.co/Xenova/bge-small-en-v1.5) | Required files not present: 1_Pooling/config.json | [`ea104dacec62`](https://huggingface.co/Xenova/bge-small-en-v1.5/tree/ea104dacec62c0de699686887e3f920caeb4f3e3) |
| [Xenova/gte-small](https://huggingface.co/Xenova/gte-small) | Required files not present: 1_Pooling/config.json | [`5927d1727bb1`](https://huggingface.co/Xenova/gte-small/tree/5927d1727bb12db490052a1b33265ad78058de08) |
| [openai/clip-vit-base-patch32](https://huggingface.co/openai/clip-vit-base-patch32) | Required files not present: onnx/text_model.onnx, onnx/vision_model.onnx | [`3d74acf9a28c`](https://huggingface.co/openai/clip-vit-base-patch32/tree/3d74acf9a28c67741b2f4f2ea7635f0aaf6f0268) |
| [openai/clip-vit-base-patch16](https://huggingface.co/openai/clip-vit-base-patch16) | Required files not present: onnx/text_model.onnx, onnx/vision_model.onnx | [`57c216476eef`](https://huggingface.co/openai/clip-vit-base-patch16/tree/57c216476eefef5ab752ec549e440a49ae4ae5f3) |
| [openai/clip-vit-large-patch14](https://huggingface.co/openai/clip-vit-large-patch14) | Required files not present: onnx/text_model.onnx, onnx/vision_model.onnx | [`32bd64288804`](https://huggingface.co/openai/clip-vit-large-patch14/tree/32bd64288804d66eefd0ccbe215aa642df71cc41) |
| [Xenova/clip-vit-large-patch14](https://huggingface.co/Xenova/clip-vit-large-patch14) | The required `text_config.max_position_embeddings` field is absent; the loader does not infer upstream defaults. | [`c30779016690`](https://huggingface.co/Xenova/clip-vit-large-patch14/tree/c307790166907339eed5a9a53a249af534102536) |
| [Xenova/clip-vit-large-patch14-336](https://huggingface.co/Xenova/clip-vit-large-patch14-336) | The required `text_config.max_position_embeddings` field is absent; the loader does not infer upstream defaults. | [`2e62e9b1fb2c`](https://huggingface.co/Xenova/clip-vit-large-patch14-336/tree/2e62e9b1fb2cc5e0ed013d49bb2c467c5502fe02) |
| [onnx-community/clip-vit-base-patch32-ONNX](https://huggingface.co/onnx-community/clip-vit-base-patch32-ONNX) | Required files not present: onnx/text_model.onnx, onnx/vision_model.onnx | [`b318363e9c2f`](https://huggingface.co/onnx-community/clip-vit-base-patch32-ONNX/tree/b318363e9c2fe791fd691d44862bb4cd4d7accc5) |
| [onnx-community/clip-vit-base-patch16-ONNX](https://huggingface.co/onnx-community/clip-vit-base-patch16-ONNX) | Required files not present: onnx/text_model.onnx, onnx/vision_model.onnx | [`1c7590e262dd`](https://huggingface.co/onnx-community/clip-vit-base-patch16-ONNX/tree/1c7590e262dd0f13131ae553346b24560cf76210) |
| [onnx-community/clip-vit-large-patch14-ONNX](https://huggingface.co/onnx-community/clip-vit-large-patch14-ONNX) | Required files not present: onnx/text_model.onnx, onnx/vision_model.onnx | [`80593872b9eb`](https://huggingface.co/onnx-community/clip-vit-large-patch14-ONNX/tree/80593872b9eb981779c11a798e679e1941915ab7) |
| [Xenova/chinese-clip-vit-base-patch16](https://huggingface.co/Xenova/chinese-clip-vit-base-patch16) | No integrated native adapter for `model_type: chinese_clip`. | [`f26904860903`](https://huggingface.co/Xenova/chinese-clip-vit-base-patch16/tree/f26904860903e70e050b8f48255e5f48401816e9) |
| [google/siglip-base-patch16-224](https://huggingface.co/google/siglip-base-patch16-224) | No integrated native adapter for `model_type: siglip`. | [`7fd15f0689c7`](https://huggingface.co/google/siglip-base-patch16-224/tree/7fd15f0689c79d79e38b1c2e2e2370a7bf2761ed) |
| [google/siglip2-base-patch16-224](https://huggingface.co/google/siglip2-base-patch16-224) | No integrated native adapter for `model_type: siglip`. | [`75de2d55ec2d`](https://huggingface.co/google/siglip2-base-patch16-224/tree/75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2) |
| [jinaai/jina-clip-v1](https://huggingface.co/jinaai/jina-clip-v1) | No integrated native adapter for `model_type: jina_clip`. | [`ceb3e44ca4d6`](https://huggingface.co/jinaai/jina-clip-v1/tree/ceb3e44ca4d6eceaa4f3fb58b1c1a5748b3f29b6) |
| [jinaai/jina-clip-v2](https://huggingface.co/jinaai/jina-clip-v2) | No integrated native adapter for `model_type: jina_clip`. | [`e10d47f5691d`](https://huggingface.co/jinaai/jina-clip-v2/tree/e10d47f5691d0454a0fb5d13f46f2199b74cb436) |
| [sentence-transformers/all-mpnet-base-v2](https://huggingface.co/sentence-transformers/all-mpnet-base-v2) | No integrated native adapter for `model_type: mpnet`. | [`e8c3b32edf54`](https://huggingface.co/sentence-transformers/all-mpnet-base-v2/tree/e8c3b32edf5434bc2275fc9bab85f82640a19130) |
| [sentence-transformers/paraphrase-multilingual-mpnet-base-v2](https://huggingface.co/sentence-transformers/paraphrase-multilingual-mpnet-base-v2) | No integrated native adapter for `model_type: xlm-roberta`. | [`4328cf26390c`](https://huggingface.co/sentence-transformers/paraphrase-multilingual-mpnet-base-v2/tree/4328cf26390c98c5e3c738b4460a05b95f4911f5) |
| [sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2](https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2) | Required files not present: vocab.txt | [`e8f8c211226b`](https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/tree/e8f8c211226b894fcb81acc59f3b34ba3efd5f42) |
| [sentence-transformers/LaBSE](https://huggingface.co/sentence-transformers/LaBSE) | The pipeline includes a Dense transformation that is not implemented by the generic adapter. | [`836121a0533e`](https://huggingface.co/sentence-transformers/LaBSE/tree/836121a0533e5664b21c7aacc5d22951f2b8b25b) |
| [intfloat/e5-small-v2](https://huggingface.co/intfloat/e5-small-v2) | Required files not present: onnx/model.onnx | [`ffb93f3bd404`](https://huggingface.co/intfloat/e5-small-v2/tree/ffb93f3bd4047442299a41ebb6fa998a38507c52) |
| [intfloat/multilingual-e5-small](https://huggingface.co/intfloat/multilingual-e5-small) | Required files not present: vocab.txt | [`614241f622f5`](https://huggingface.co/intfloat/multilingual-e5-small/tree/614241f622f53c4eeff9890bdc4f31cfecc418b3) |
| [intfloat/multilingual-e5-base](https://huggingface.co/intfloat/multilingual-e5-base) | No integrated native adapter for `model_type: xlm-roberta`. | [`d12875059715`](https://huggingface.co/intfloat/multilingual-e5-base/tree/d128750597153bb5987e10b1c3493a34e5a4502a) |
| [intfloat/multilingual-e5-large](https://huggingface.co/intfloat/multilingual-e5-large) | No integrated native adapter for `model_type: xlm-roberta`. | [`3d7cfbdacd47`](https://huggingface.co/intfloat/multilingual-e5-large/tree/3d7cfbdacd47fdda877c5cd8a79fbcc4f2a574f3) |
| [intfloat/multilingual-e5-large-instruct](https://huggingface.co/intfloat/multilingual-e5-large-instruct) | No integrated native adapter for `model_type: xlm-roberta`. | [`274baa43b0e1`](https://huggingface.co/intfloat/multilingual-e5-large-instruct/tree/274baa43b0e13e37fafa6428dbc7938e62e5c439) |
| [BAAI/bge-m3](https://huggingface.co/BAAI/bge-m3) | No integrated native adapter for `model_type: xlm-roberta`. | [`5617a9f61b02`](https://huggingface.co/BAAI/bge-m3/tree/5617a9f61b028005a4858fdac845db406aefb181) |
| [BAAI/bge-small-zh-v1.5](https://huggingface.co/BAAI/bge-small-zh-v1.5) | Required files not present: onnx/model.onnx | [`7999e1d33597`](https://huggingface.co/BAAI/bge-small-zh-v1.5/tree/7999e1d3359715c523056ef9478215996d62a620) |
| [BAAI/bge-base-zh-v1.5](https://huggingface.co/BAAI/bge-base-zh-v1.5) | Required files not present: onnx/model.onnx | [`f03589ceff5a`](https://huggingface.co/BAAI/bge-base-zh-v1.5/tree/f03589ceff5aac7111bd60cfc7d497ca17ecac65) |
| [BAAI/bge-large-zh-v1.5](https://huggingface.co/BAAI/bge-large-zh-v1.5) | Required files not present: onnx/model.onnx | [`79e7739b6ab9`](https://huggingface.co/BAAI/bge-large-zh-v1.5/tree/79e7739b6ab944e86d6171e44d24c997fc1e0116) |
| [mixedbread-ai/mxbai-embed-large-v1](https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1) | Declared retrieval prompts require an integrated role-aware recipe. | [`b33106f585b9`](https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/tree/b33106f585b9ce46904ad7443a3b52b7a63e231c) |
| [Snowflake/snowflake-arctic-embed-s](https://huggingface.co/Snowflake/snowflake-arctic-embed-s) | Declared retrieval prompts require an integrated role-aware recipe. | [`e596f5074675`](https://huggingface.co/Snowflake/snowflake-arctic-embed-s/tree/e596f507467533e48a2e17c007f0e1dacc837b33) |
| [Snowflake/snowflake-arctic-embed-m](https://huggingface.co/Snowflake/snowflake-arctic-embed-m) | Declared retrieval prompts require an integrated role-aware recipe. | [`fc74610d1846`](https://huggingface.co/Snowflake/snowflake-arctic-embed-m/tree/fc74610d18462d218e312aa986ec5c8a75a98152) |
| [Snowflake/snowflake-arctic-embed-l](https://huggingface.co/Snowflake/snowflake-arctic-embed-l) | Declared retrieval prompts require an integrated role-aware recipe. | [`d8fb21ca8d90`](https://huggingface.co/Snowflake/snowflake-arctic-embed-l/tree/d8fb21ca8d905d2832ee8b96c894d3298964346b) |
| [jinaai/jina-embeddings-v2-base-en](https://huggingface.co/jinaai/jina-embeddings-v2-base-en) | Required files not present: onnx/model.onnx | [`322d4d7e2f35`](https://huggingface.co/jinaai/jina-embeddings-v2-base-en/tree/322d4d7e2f35e84137961a65af894fda0385eb7a) |
| [jinaai/jina-embeddings-v2-base-code](https://huggingface.co/jinaai/jina-embeddings-v2-base-code) | Required files not present: vocab.txt | [`516f4baf13de`](https://huggingface.co/jinaai/jina-embeddings-v2-base-code/tree/516f4baf13dec4ddddda8631e019b5737c8bc250) |
| [jinaai/jina-embeddings-v3](https://huggingface.co/jinaai/jina-embeddings-v3) | The root configuration lacks the `model_type` field required by this loader; this URL has no integrated adapter. | [`ab036b023d30`](https://huggingface.co/jinaai/jina-embeddings-v3/tree/ab036b023d30b4d1138c4c3bfa9f0c445ab455d6) |
| [jinaai/jina-embeddings-v4](https://huggingface.co/jinaai/jina-embeddings-v4) | The root configuration lacks the `model_type` field required by this loader; this URL has no integrated adapter. | [`853c867b65b7`](https://huggingface.co/jinaai/jina-embeddings-v4/tree/853c867b65b749f3c3c72a06868140d842e04f06) |
| [nomic-ai/nomic-embed-text-v1.5](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5) | No integrated native adapter for `model_type: nomic_bert`. | [`e9b6763023c6`](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/tree/e9b6763023c676ca8431644204f50c2b100d9aab) |
| [nomic-ai/nomic-embed-text-v2-moe](https://huggingface.co/nomic-ai/nomic-embed-text-v2-moe) | No integrated native adapter for `model_type: nomic_bert`. | [`1066b6599d09`](https://huggingface.co/nomic-ai/nomic-embed-text-v2-moe/tree/1066b6599d099fbb93dfcb64f9c37a7c9e503e85) |
| [nomic-ai/nomic-embed-code](https://huggingface.co/nomic-ai/nomic-embed-code) | No integrated native adapter for `model_type: qwen2`. | [`11114029805c`](https://huggingface.co/nomic-ai/nomic-embed-code/tree/11114029805cee545ef111d5144b623787462a52) |
| [nomic-ai/CodeRankEmbed](https://huggingface.co/nomic-ai/CodeRankEmbed) | No integrated native adapter for `model_type: nomic_bert`. | [`3c4b60807d71`](https://huggingface.co/nomic-ai/CodeRankEmbed/tree/3c4b60807d71f79b43f3c4363786d9493691f8b1) |
| [microsoft/codebert-base](https://huggingface.co/microsoft/codebert-base) | No integrated native adapter for `model_type: roberta`. | [`3b0952feddef`](https://huggingface.co/microsoft/codebert-base/tree/3b0952feddeffad0063f274080e3c23d75e7eb39) |
| [microsoft/graphcodebert-base](https://huggingface.co/microsoft/graphcodebert-base) | No integrated native adapter for `model_type: roberta`. | [`2b0488a7bb0e`](https://huggingface.co/microsoft/graphcodebert-base/tree/2b0488a7bb0eefc7041f1bb2cad1ab26b0da269d) |
| [microsoft/unixcoder-base](https://huggingface.co/microsoft/unixcoder-base) | No integrated native adapter for `model_type: roberta`. | [`5604afdc964f`](https://huggingface.co/microsoft/unixcoder-base/tree/5604afdc964f6c53782a6813140ade5216b99006) |
| [Qwen/Qwen3-Embedding-4B](https://huggingface.co/Qwen/Qwen3-Embedding-4B) | No integrated native adapter for `model_type: qwen3`. | [`5cf2132abc99`](https://huggingface.co/Qwen/Qwen3-Embedding-4B/tree/5cf2132abc99cad020ac570b19d031efec650f2b) |
| [Qwen/Qwen3-Embedding-8B](https://huggingface.co/Qwen/Qwen3-Embedding-8B) | No integrated native adapter for `model_type: qwen3`. | [`1d8ad4ca9b3d`](https://huggingface.co/Qwen/Qwen3-Embedding-8B/tree/1d8ad4ca9b3dd8059ad90a75d4983776a23d44af) |
| [Qwen/Qwen3-Coder-30B-A3B-Instruct](https://huggingface.co/Qwen/Qwen3-Coder-30B-A3B-Instruct) | No integrated native adapter for `model_type: qwen3_moe`. | [`b2cff646eb4b`](https://huggingface.co/Qwen/Qwen3-Coder-30B-A3B-Instruct/tree/b2cff646eb4bb1d68355c01b18ae02e7cf42d120) |
| [Salesforce/blip-image-captioning-base](https://huggingface.co/Salesforce/blip-image-captioning-base) | No integrated native adapter for `model_type: blip`. | [`82a37760796d`](https://huggingface.co/Salesforce/blip-image-captioning-base/tree/82a37760796d32b1411fe092ab5d4e227313294b) |
| [Salesforce/blip2-opt-2.7b](https://huggingface.co/Salesforce/blip2-opt-2.7b) | No integrated native adapter for `model_type: blip-2`. | [`59a1ef6c1e51`](https://huggingface.co/Salesforce/blip2-opt-2.7b/tree/59a1ef6c1e5117b3f65523d1c6066825bcf315e3) |
| [BAAI/bge-reranker-base](https://huggingface.co/BAAI/bge-reranker-base) | No integrated native adapter for `model_type: xlm-roberta`. | [`2cfc18c9415c`](https://huggingface.co/BAAI/bge-reranker-base/tree/2cfc18c9415c912f9d8155881c133215df768a70) |
| [colbert-ir/colbertv2.0](https://huggingface.co/colbert-ir/colbertv2.0) | Required files not present: 1_Pooling/config.json, onnx/model.onnx | [`c1e84128e85e`](https://huggingface.co/colbert-ir/colbertv2.0/tree/c1e84128e85ef755c096a95bdb06b47793b13acf) |

A repository's general task label is not sufficient. CodeBERT/GraphCodeBERT/UniXcoder backbones, coding-language generators, cross-encoder rerankers, ColBERT-style token-vector models, SigLIP, arbitrary vision-language models, and GGUF/MLX/OpenVINO-only layouts do not become supported by entering their model-card URLs.

Authentication or metadata-access failures are not architectural rejections. The following entries could not be verified and are excluded from the compatible-format catalogue.

| Model-card URL | Verification limitation |
|---|---|
| [ArieLLL123/Meivin-Embed-0.1](https://huggingface.co/ArieLLL123/Meivin-Embed-0.1) | Required configuration returned HTTP 401; access-dependent compatibility was not established. |
| [google/embeddinggemma-300m](https://huggingface.co/google/embeddinggemma-300m) | Required configuration returned HTTP 401; access-dependent compatibility was not established. |
| [journey-ai/journey-ai-2](https://huggingface.co/journey-ai/journey-ai-2) | Required configuration returned HTTP 401; access-dependent compatibility was not established. |
| [taide/embeddinggemma-GTAIDE-300m-2605](https://huggingface.co/taide/embeddinggemma-GTAIDE-300m-2605) | Required configuration returned HTTP 401; access-dependent compatibility was not established. |
| [wonjib/all-MiniLM-L6-v2](https://huggingface.co/wonjib/all-MiniLM-L6-v2) | Required configuration returned HTTP 401; access-dependent compatibility was not established. |

## Verification scope

The dated assessment covers **1,744 repository URLs** selected from public ONNX/BERT, CLIP and Gemma2 listings; Sentence Transformers, Xenova and ONNX Community publisher listings; and additional relevant model references. All result pages for the selected queries were included. This is a complete listing of the shipped registry and the compatible-format entries identified within that assessment, not a claim to enumerate every present or future Hugging Face repository.

| Assessment outcome | Repository URLs |
|---|---:|
| Integrated registry URLs and aliases verified | 14 |
| Additional compatible-format entries, including conditional-formatting entries | 284 |
| Required files or native contract not satisfied | 1,441 |
| Required metadata inaccessible | 5 |
| **Total** | **1,744** |

The application inspector accepted 102 distinct URLs during the assessment. Four additional Gemma2 exports passed native processor and graph-interface validation. These checks do not replace full asset verification, runtime qualification or retrieval evaluation. The existing Gemma2 x64/ARM64 reference results must not be generalized to all catalogue entries.

Compatibility data is based on the public-model support in [revision b541ee3](https://github.com/sumanthewhiz/FuzzyFindAnything/tree/b541ee3c500c03ec5ef8b73cae881325a7bbe6fa). Model revisions are pinned where the application registry specifies them; other entries record the resolved revision observed on 19 November 2026. Reassess unpinned exports after upstream changes.

### Reference sources

| Reference | Details |
|---|---|
| [Embedding recipes](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/b541ee3c500c03ec5ef8b73cae881325a7bbe6fa/model-assets/embedding-recipes.json) | Qwen/E5 artifact revisions, aliases, limits and retrieval formatting |
| [Public Gemma2 package](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/b541ee3c500c03ec5ef8b73cae881325a7bbe6fa/model-assets/embeddinggemma-2-public.json) | Default package identity and supplemental tokenizer provenance |
| [Image recipes](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/b541ee3c500c03ec5ef8b73cae881325a7bbe6fa/model-assets/image-recipes.json) | Complete Florence registry and task limits |
| [Model resolver](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/b541ee3c500c03ec5ef8b73cae881325a7bbe6fa/src/FuzzyFindAnything.Core/HuggingFaceModels.cs) | Exact file, family, pooling, prompting and cache requirements |
| [Native model adapter](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/b541ee3c500c03ec5ef8b73cae881325a7bbe6fa/src/FuzzyFindAnything.HuggingFaceInference/CompatibleModel.cs) and [tokenizer](https://github.com/sumanthewhiz/FuzzyFindAnything/blob/b541ee3c500c03ec5ef8b73cae881325a7bbe6fa/src/FuzzyFindAnything.HuggingFaceInference/ModelTokenizer.cs) | Tensor interfaces, tokenization, preprocessing and execution qualification |
| [Validation reference](VALIDATION.md) | Scope and methods of application acceptance checks |
| [Qwen model card](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B) and [E5 model card](https://huggingface.co/intfloat/e5-base-v2) | Upstream retrieval roles and required formatting |
| [BGE model card](https://huggingface.co/BAAI/bge-small-en-v1.5) and [UAE model card](https://huggingface.co/WhereIsAI/UAE-Large-V1) | Query-instruction considerations |
| [Florence model card](https://huggingface.co/microsoft/Florence-2-base-ft) | Model variants and image-description tasks |

Each catalogue row links to the model card and its assessed revision. Upstream cards and artifact inventories remain the source for model-specific capabilities and terms.

## Licensing and attribution

Copyright (c) 2026 **Suman Ghosh**. This documentation is part of FuzzyFindAnything and is licensed under the [MIT License](../LICENSE).

Model weights, tokenizers and exports remain subject to their own upstream licenses and terms. Review both the original checkpoint and export repository before use or redistribution; the application's MIT license does not grant rights to those artifacts. A missing license declaration is not permission to redistribute. See [Third-party notices and model terms](../THIRD-PARTY-NOTICES.md#hugging-face-and-other-models).

Third-party names and links identify the referenced artifacts. Inclusion does not imply affiliation with or endorsement by their providers.
