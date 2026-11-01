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

## Later milestones — design only

`IXlsxDocumentParser` and `ITextPdfDocumentParser` reserve parser boundaries without registered implementations. A later PostgreSQL `IDocumentStore` implementation can map projects, documents, revisions and approvals into relational tables, with approved normalized data in `jsonb`. Original files would stay in protected file/object storage with references. Future chunk records would refer to one approved document revision and its source references; embeddings could later use pgvector. None of those features or dependencies are part of this milestone.
