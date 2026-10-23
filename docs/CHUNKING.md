# Content chunking

[Overview](../README.md) · [Architecture](ARCHITECTURE.md) · [Migration](MIGRATION.md)

## Who creates the chunks?

**The application creates file chunks; the embedding model does not decide file boundaries.**

The model's tokenizer turns text into token IDs, and its encoder produces vectors. The application chooses source boundaries, overlap, context, budgets and provenance. This remains true for **EmbeddingGemma2**: its SentencePiece tokenizer is used to measure text, but neither the neural model nor DiskANN divides a source file into paragraphs or functions.

Image patching inside a vision processor is a different operation. Direct Gemma/CLIP images are not source-code/document chunks. Caption/OCR pipelines first generate text and retain their existing model-specific text preparation; a document/code chunk-profile update does not regenerate those image descriptions or invalidate their vectors.

## Current pipeline

```text
File kind and extension
  -> extraction/read-only source text
  -> document or code structure profile
  -> exact selected-model token count and input framing
  -> bounded chunks with source provenance
  -> embedding batches and durable vector publication
```

Profiles are selected automatically. No parser, tokenizer or chunk-size UI is required for ordinary use.

| Source | Boundary policy |
|---|---|
| Documents | Recognize available headings (Markdown-style, colon and underlined headings), paragraphs and sentence endings. Combine nearby paragraphs in the same section when they fit; split oversized units at sentence/line boundaries. |
| Supported code | Native Tree-sitter syntax trees identify function/method/declaration units and parent scope. Leading documentation/decorators are retained. Oversized units split at statement/block or line boundaries. |
| Unsupported or malformed syntax | Explicitly reported line-aware, token-bounded fallback; source content is not executed or discarded because parsing failed. |
| Extremely long line/unit | A Unicode-safe source window is the final fallback when no structural boundary fits. |

The parser is `TreeSitter.DotNet 1.3.0`, with architecture-matched native runtimes. Qualified grammar paths cover C#, Python, JavaScript/JSX, TypeScript/TSX, Java, C/C++, Go, Rust, PHP, Ruby and Bash. SQL, Kotlin, R and unknown languages use the fallback. The dependency's Swift grammar is incompatible with this pinned runtime, is not included in the app payload, and falls back with a visible warning. A `.h` file is initially treated as C; unsupported C++ constructs fall back rather than being lost.

Document structure is limited by what extraction provides. An IFilter may flatten original layout; the app does not invent original PDF/Office page, heading or paragraph metadata that was not extracted.

## Token budgets, context and overlap

All final chunk inputs are checked with the **actual selected model tokenizer**. Source offsets are UTF-16 string offsets; line numbers are one-based positions in the extracted text/source file.

| Model path | Current budget |
|---|---|
| EmbeddingGemma2 | 512 content/context tokens plus its bounded filename/title and special-token framing, always below the 8,192-token input ceiling. |
| Reviewed Qwen embedding recipe | 512 total formatted chunk tokens, including applicable prefix/special tokens, within the 2,048-token runtime ceiling. |
| Other reviewed embedding recipes | Their declared `chunk_tokens`, including their document prefix and special tokens. |
| Legacy compatible BERT/CLIP text encoders | Their admitted sequence limit, with special tokens counted before accepting a chunk. |

Code context contains a bounded parent/symbol signature; document context contains the available heading. Context is counted in the same input budget and capped separately so it cannot crowd out the source body.

Whole units that fit are not arbitrarily sliced. Oversized units use overlap bounded by **64 tokenizer tokens or one eighth of the chunk budget**, whichever is smaller, preferring a structural/line boundary. This replaces the compatible host's earlier character-count overlap for document/code preparation. Boundaries do not split UTF-16 surrogate pairs.

Distinct code spans are not deduplicated by collapsing whitespace. This preserves indentation-sensitive code and repeated declarations at different source locations. The structured profiles preserve source coverage, including comments and line breaks.

## Retrieval, persistence and upgrades

Each prepared chunk carries its profile, language, source offset/length, line range, symbol and added context. These remain associated with the committed embedding after the indexing job is removed. Document/code result cards expose the matched full chunk in a hover preview; the compact inline snippet is only a preview.

Chunking profile identity is independent of the model's document-vector identity. Text and code can still share a compatible Qwen/Gemma vector index while using different source segmentation. Query Purpose controls query instructions; it does **not** select the file chunker.

Catalog schema 8 adds full chunk text and provenance. The first startup with the new native preparation protocol updates document/code profiles and schedules an incremental background rebuild of those representations. Folder lists, source IDs, image vectors and cached caption/OCR are retained. Old prepared document/code batches are invalidated because their boundaries belong to a different profile. A later code-profile-only change invalidates code, not documents or images.

An unchanged profile does not trigger another rebuild on restart. A query-only preference or similarity-threshold change does not reindex files.

Legacy vectors that have not yet been replaced expose their saved excerpt, explicitly labeled **Matched excerpt**, rather than pretending that the entire original chunk was retained. Original files continue to open in their registered applications.

## Verification

The `chunking` native test mode covers native grammar boundaries, documentation/signature retention, section separation, long functions, Unicode/source coverage, whitespace-sensitive identity, visible fallback and selective migration. `chunking-runtime <app> <default-model> <HF-cache> <output>` additionally checks formatted token budgets and actual embedding calls in Gemma and Qwen workers.

The implementation lives in `FuzzyFindAnything.Chunking`; model-specific counting/framing stays in the native inference hosts. `ChunkingProfiles` and `Catalog.Chunks` define versioning and durable provenance. Parser/runtime upgrades or boundary-policy changes must update the relevant profile identity and pass the source-coverage and token-budget tests.
