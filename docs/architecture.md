# Initial milestone architecture

## Solution references

```text
Web → Application → Domain
Web → Infrastructure → Application + Domain
Tests → Web + Infrastructure (and transitive dependencies)
```

Web hosts Blazor Interactive Server and composes infrastructure through dependency injection. Its small download routes belong to the same host; there is no separate Web API. Application owns use cases, interfaces, proposal integrity and approval rules. Domain owns the provider-independent data records. Infrastructure owns Open XML extraction, LM Studio HTTP calls, JSON Schema validation and filesystem persistence.

## Processing and trust boundaries

1. `PreparationWorkflow.UploadAsync` validates metadata, extension and bounded source bytes. `DocxDocumentParser` verifies the DOCX package and reads it without modification.
2. Paragraphs and tables are traversed in main-body order. Block content-control wrappers are traversed recursively, including stored table-of-contents paragraphs and controls inside table cells; source references retain `/sdtContent/` locations. Whitespace, tabs, line breaks, Persian/English wording and table values are preserved. The verified Wingdings 2 `F050` checkmark is mapped to Unicode `✓` with an extraction note. Unknown font/code pairs and unsupported equations are rejected rather than silently dropped or guessed.
3. `LmStudioClient` discovers available models at the configured loopback endpoint. `AnalysisBatches` plans contiguous requests with at most 32 blocks and 10,000 serialized input characters, including unchanged reference rules and preceding-heading context. All paragraph wording and table cell values are sent; table-cell reference metadata is retained in extraction/export and omitted from model input. No source content is truncated. Each request has its own timeout and respects cancellation. The selected model receives an untrusted data envelope with no tools, file access or external service calls.
4. For a small single request the model returns issues and the complete ordered classification map. Batched requests use short local block aliases (`b1`, `b2`, etc.) to reduce prompt/schema overhead. The model returns a schema-constrained part identifier, exact reviewed block count, and one issue/classification proposal per affected alias. Only aliases in the current part are permitted. The application maps them back to exact original source references. Under this explicit changes-only protocol, blocks without an issue retain their original classification. The application assembles one full ordered map and validates it against the entire extraction. The model never supplies replacement wording, table values or metadata. These batches are transient analysis requests, not retrieval chunks. Native schema mode constrains the response directly; the full schema is added to the system prompt only for the unsupported-schema fallback.
5. A formal schema validates proposal syntax/shape. Additional checks enforce complete coverage, exact source order, known references, heading levels, unchanged table classification, and a matching explanation for each changed classification. Duplicate JSON keys are rejected.
6. The consolidated report and proposed preview remain drafts. Manual classification changes create another draft ID. Approval names the exact draft and is rejected if that draft is stale, rejected, already approved or no longer awaiting review.
7. A completed analysis can be downloaded as a schema-validated v1.1 JSON draft, with `awaitingReview` status, null approval attribution/time and extraction warnings. The draft route is unavailable before analysis or after rejection/approval. Explicit approval validates the separate approved contract and persists approval time, reviewer name, draft ID and model ID. The approved download route still requires saved approval state.

Source text and reference files are untrusted input. They are escaped by Razor, never evaluated as markup or application instructions. Document text and model response bodies are not written to logs. Prompt instructions are defense in depth; the restricted output contract and deterministic assembly enforce content preservation even if a model follows an injected source instruction.

## State transitions

```text
Upload → Extracted
Extracted / Rejected / AwaitingReview → valid analysis → AwaitingReview (new draft ID)
AwaitingReview → manual classification revision → AwaitingReview (new draft ID)
AwaitingReview → explicit rejection → Rejected
AwaitingReview → explicit approval + valid normalized contract → Approved
Approved → terminal for this uploaded revision
```

Failures and cancellation do not advance review state. Each multi-request part is validated independently; an invalid model result is retried once with visible progress. The aggregate is saved only after every part passes and complete source coverage/order is validated. No partial draft is committed. A failed reanalysis retains the previous saved draft. A save compares a monotonically increasing version to prevent another circuit from overwriting newer work. Storage is single-process by a directory lease, with per-document write locks. Reads allow atomic replacement on Windows. User-facing filenames do not form disk paths.

## Persistence and normalized content

`IDocumentStore` isolates local filesystem storage. Each generated document directory contains unchanged `original.docx`, a current `review.json` snapshot and, after approval, `approved.json`. The review snapshot retains extraction, rules, latest proposal and final content. `ISettingsStore` persists connection settings separately. The application writes temporary files and renames them atomically. Approved output is written before the approval snapshot: if a write is interrupted, the download gate still consults the committed snapshot. No multi-file database transaction or superseded-draft audit log is claimed.

Schema v1.0 contains document/project identifiers, document number, title, revision, language, ordered blocks, original text, optional table rows/cells, formatting evidence, source references and approval metadata. New v1.1 exports add extraction warnings and a separately validated unapproved draft form. Existing v1.0 approvals remain readable. Sections are represented by heading blocks and heading levels. The entire extracted ordered content is present for a future structure-aware chunker, without reparsing the original file; extraction warnings identify unsupported source content.

## M2 approved knowledge index

The existing projects and dependency direction are retained. Domain adds chunk/source-span contracts, indexing states, settings and retrieval records. Application owns indexing/retrieval interfaces and the retrieval use case. Infrastructure implements approved-source validation, chunking, LM Studio embeddings, EF Core/Npgsql/pgvector persistence and an in-host bounded indexing worker. Web composes these services and supplies `/knowledge-index`, `/retrieval-test` and `/settings/indexing`.

`IDocumentStore` remains filesystem-backed. `ApprovedDocumentProvider` discovers committed approvals through that abstraction, validates the original raw JSON against v1.0/v1.1 schemas (including duplicate-key rejection), compares it with the saved approval and unchanged extraction/proposal, and records SHA-256 of the exact bytes. A file named `approved.json` alone is insufficient. Imported snapshots cannot overwrite a previously imported source with different bytes. Reviewer attribution is local metadata, not a digital signature.

`StructureAwareChunker` respects heading paths, paragraph/list ordering and complete table rows. It preserves text and Unicode through exact UTF-16 spans; inherited context and overlap are distinguished from primary coverage. Header counts are explicit derived settings scoped to a source ID/table reference because M1 contains no header flags. Unsafe oversized blocks/rows fail without truncation. Empty blocks and extraction warnings appear in coverage notes. The approved artifact is never rewritten. Chunk IDs include the approved byte hash, configuration fingerprint, ordinal, embedding text hash and spans.

The relational model contains `KnowledgeDocuments` (unique project/document number), `ApprovedRevisions` (source UUID, opaque revision literal, immutable verified JSONB snapshot, hash and approval metadata), `EmbeddingSpaces`, `IndexGenerations`, `KnowledgeChunks` and `ActiveSelections`. A chunk row physically holds its single generation-specific embedding, dimensions, text and JSONB source-span contract. No original upload bytes are copied into PostgreSQL. JSONB retains the verified content for audit; its serialization is not the original byte representation, so the exact-byte hash is stored separately. Local source files remain authoritative.

Variable-dimension pgvector columns allow model changes without rebuilding the schema. Composite foreign keys constrain embedding-space dimensions; vector norm/dimension checks reject zero/incompatible vectors. Migrations add guards requiring matching source identity/hash, completed matching active selections, complete chunk counts, immutable completed generations/chunks, and immutable imported approval snapshots. Approval JSON must declare approval and match its source identity/revision. Only the Application/provider path imports artifacts; the database does not independently implement the full JSON Schema validator.

`KnowledgeIndexService` is a singleton hosted service inside Web, with one worker and at most four accepted documents including queued work. Source/model/configuration preflight runs before a persisted pending generation is queued. Chunks and vectors commit in bounded batches to an inactive generation, without keeping a transaction open during HTTP requests. Final source/settings/model checks precede a short transaction that completes the generation and updates the active pointer atomically. A changed revision needs explicit activation; reindexing an active source may replace its active generation after success. Failure/cancellation preserves the previous pointer. Interrupted pending/running work is marked failed at startup when available, or before accepting new work after a database outage. Retry starts fresh; partial resume is not implemented. Failed staged rows remain diagnostic, derived data.

Embedding settings extend the existing local settings record with optional fields, preserving old settings JSON. Chat-setting saves retain embedding/chunk settings. Native LM Studio model metadata distinguishes embedding models from chat models. The provider validates empirical dimensions, ordered indices, count, response model identity, finite/nonzero values and bounded payloads. Configuration identity includes an operator-supplied local artifact/version identity, prefixes, context limit, dimensions and a quantized bilingual sentinel. This probe detects common drift but does not establish a cryptographic model-file identity. HTTP 429/5xx retries are bounded; queries and documents use separate configured prefixes. All requests use validated loopback URLs, no redirects and no proxies.

Semantic retrieval embeds queries in the same compatible space and executes parameterized, exact cosine queries against completed active generations. Project/source/document-number/revision/language filters precede ranking. Ties use deterministic metadata/source/ordinal ordering. Literal mode uses case-insensitive substring matching and an exact document-number path, without LM Studio. Persian spelling normalization and stemming are intentionally absent. Returned snippets carry the original source references; the retrieval service rechecks matching local approved byte hashes before returning results. A cosine threshold is not an unsupported-question detector. No generated answers, hybrid fusion, reranker or ANN index is included.

Database construction is lazy. Explicit migration/health operations and knowledge screens report optional dependency failures without making PostgreSQL a startup requirement for M1. Connection strings use ASP.NET secret/environment configuration and must point to one loopback host. The host remains loopback-only. Settings, queues, queries, batches, response sizes and chunk counts are bounded; Razor escapes retrieved source content. Logs intentionally omit source text, vectors and secrets.

See [setup and recovery](knowledge-index-setup.md), [evaluation scope/results](retrieval-evaluation.md), generated EF migrations, and the PostgreSQL integration test. `IXlsxDocumentParser` and `ITextPdfDocumentParser` remain unimplemented future boundaries.
