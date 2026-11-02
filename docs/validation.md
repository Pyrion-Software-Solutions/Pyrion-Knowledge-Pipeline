# Initial milestone validation

## M2 validation — November 3, 2026

- Full suite: 91 passed, zero failed, two explicit opt-in skips. All 61 original M1 tests remain included. Build has zero warnings/errors.
- New automated coverage: raw v1.0/v1.1 approval validation/tampering, complete source/cell coverage, deterministic chunk IDs, headings/sections, Unicode offsets, oversized blocks/rows without truncation, embedding capabilities/count/indices/model/dimensions/invalid values/timeouts/retries, idempotency, cancellation and failed-reindex activation behavior, and new routes with PostgreSQL unconfigured.
- EF model/migration consistency passed; an idempotent migration SQL script was generated successfully without connecting to a database.
- Actual local LM Studio evaluation ran separately and passed its protocol/identity checks. The small synthetic set measured Recall@5 of 1.0 over nine supported queries; its unsupported semantic query returned five neighbors. This evaluates local embeddings plus in-memory cosine, not PostgreSQL or full-application latency. See [evaluation](retrieval-evaluation.md).
- Browser checks used an isolated temporary source/settings directory. They exercised responsive M2 routes, actual embedding-model discovery, settings save and bilingual model validation, and actionable database-offline search feedback. Browser verification found and fixed query/settings text bindings before delivery.
- PostgreSQL integration is explicitly skipped until `PYRION_TEST_DATABASE` is available. pgvector files were absent from the detected PostgreSQL 18 installation; database access required credentials. Migration execution, database constraints/rollback, vector-query filtering and the complete live indexing/restart demonstration remain unverified. These are prerequisites, not successful checks.

The original M1 validation history follows.

Validation performed on Windows on November 1, 2026, using .NET SDK 10.0.403 and .NET runtime 10.0.14.

## Automated checks

`dotnet test PyrionKnowledgePipeline.sln` built all five projects with zero warnings and zero errors, and passed all 45 tests (zero failed or skipped).

Coverage includes:

- DOCX paragraph/table ordering, inherited heading styles, list metadata, exact spaces/tabs/breaks, special hyphens, Persian text and table-cell references.
- Invalid packages, nested tables, equations, wrong extensions, empty/oversized uploads and missing metadata.
- JSON syntax, required schema fields, duplicate keys, added replacement-text properties, missing/reordered/unknown source references, invalid heading levels, table conversion and unexplained changes.
- Selected-model requests, schema mode, one visible fallback for an unsupported format, offline/timeout/cancellation handling, missing models, invalid roots, empty and incomplete completions, and server failures.
- Manual draft revision, rejection, exact-draft approval, terminal approved state, stale writes, failed reanalysis preserving the prior draft, byte-identical original storage, and reopening saved state.
- Host rendering, sample download, denied unapproved downloads, successful approved downloads, response headers and invalid-host rejection.

`dotnet list PyrionKnowledgePipeline.sln package --vulnerable --include-transitive` reported no known vulnerable packages from the configured sources.

The UI mechanical design check reported no findings. Browser verification also checks the narrow layout, preserved Persian text, connection settings and real-model workflow.

## Real local environment

The LM Studio endpoint at `http://localhost:1234/v1/models` responded with two available models. The chat model `google/gemma-3-4b` was discovered and selected through the UI, rather than hardcoded in the application. The sample DOCX was uploaded through the browser, and all seven source blocks were shown correctly. Reloading after an application restart reopened the saved extraction and connection settings.

An initial model proposal contained an invalid type/heading-level combination. The application visibly rejected it and created no draft or approval. The proposal schema was then strengthened to enforce the relationship through separate heading and non-heading alternatives; all automated tests passed again.

The stricter-schema retry returned a valid seven-block proposal and five model issue entries. Some model reasoning was weak or inconsistent; the sample's missing heading was corrected using the manual classification UI. A new draft ID was saved, the report and preview were reviewed, and the sample was explicitly approved under the reviewer name `Sample verification`.

The complete browser path was exercised: settings → real model discovery/selection → source file chooser/upload → extraction → real local analysis → consolidated report → manual revision → explicit approval → JSON download. The successful retry completed within the saved 180-second timeout. The application was restarted and the approved review reopened successfully.

Artifact checks passed:

- The stored original DOCX has the same SHA-256 as the included sample.
- The browser-downloaded JSON is byte-identical to the stored approved JSON.
- Every ordered source reference, source text string and table row/cell value matches the extraction.
- The manual Heading 1 correction is present in approved content, and both the review snapshot and normalized artifact report `approved`.
- Exactly seven ordered blocks are present.

Sample document ID: `e05e0b27-79ab-4640-bf00-82fec2105069`. The sample lives in the default local application store and can be reopened from the dashboard. Its approved export was downloaded into the Windows Downloads folder.

A 390px mobile viewport showed no horizontal overflow and no unlabeled form controls. The viewport override was reset. Browser error/warning logs were empty during real analysis. Restarting the host emitted expected connection warnings in the open tab; reloading restored the workspace and saved approval. A final screenshot is saved locally at `artifacts/approved-review.jpg` (ignored by Git).

The final report pass corrected the approved-state wording and limited newly generated reviewer issue entries to changed classifications, while retaining matching existing model issues. All 45 tests passed again with zero build warnings or errors. The earlier approved demonstration is retained unchanged; its review entries include per-block reviewer notes from that demonstration.

This confirms the workflow, source preservation and failure gates. It does not establish the accuracy of arbitrary model suggestions or support for document structures listed as limitations in the README.

## DOCX compatibility fix — November 2, 2026

A user-provided DOCX was incorrectly blocked by a blanket equation/symbol check. Inspection found no equations, 33 Wingdings 2 `F050` symbols, and a block content control containing 45 paragraphs. The installed Microsoft Wingdings 2 font's character map and glyph metadata identify `F050` as its `check` glyph.

The parser now maps that specific font/code pair to Unicode `✓`, unwraps block content controls in source order, and retains wrapper locations in source references. Unknown symbols and unsupported equations still fail explicitly. Stored table-of-contents text is extracted without updating its fields. Regression tests cover the verified symbol in paragraphs and tables, case-insensitive font/code matching, unknown font/code rejection, nested content-control ordering and references, controls inside table cells, and deleted symbols.

All 52 automated tests passed with no build warnings or errors. The actual user file passed the upload use case using isolated temporary storage: 249 extracted blocks, 6 tables, 33 checkmarks, and 45 content-control block references. SHA-256 verification confirmed both the original file and the stored upload were unchanged. Temporary verification data was removed; no private document content was added to the repository or submitted to a model.

This file exceeds the existing 100-block AI analysis limit. The compatibility fix enables extraction and preserves that limit; it does not claim full-document AI analysis for this document.

## Large-document analysis and review JSON — November 2, 2026

The subsequent large-document update replaces the 100-block document limit with contiguous analysis requests for up to 1,000 extracted blocks. Each request contains at most 32 blocks and 10,000 serialized input characters. Table input sends all cell values in order without repeating cell reference metadata; exported JSON retains full cell references. All parts must pass schema, coverage, location and classification validation before a single consolidated review draft is committed.

All 61 automated tests pass with zero build warnings or errors. Added coverage includes all 249 blocks appearing exactly once in the combined proposal, every paragraph's full text being sent, safe translation of short model aliases back to exact source references, unchanged classifications under the explicit changes-only response contract, full table-cell value coverage, invalid/duplicate/out-of-part locations, incorrect coverage counts, forbidden replacement text, oversized blocks without truncation, one visible invalid-result retry, failed middle parts leaving no partial draft, and cancellation between requests. Host integration tests verify that draft JSON is unavailable before analysis, has `awaitingReview` status after analysis, cannot be mistaken for an approved export, and is unavailable after approval.

New normalized exports use v1.1 and include extraction warnings. Separate draft and approved schemas enforce their respective approval metadata. Existing v1.0 approvals remain readable.

The actual user document completed eight validated local-model requests covering all 249 extracted blocks. The selected `google/gemma-3-4b` model ran locally with an 8,192-token context. The saved per-request timeout was increased to 600 seconds for this run. The model returned 232 review entries, including 34 proposed classification changes. No classification was approved automatically.

The browser downloaded the review draft successfully. Artifact verification passed for all 249 ordered blocks, every source text string, every table row/cell value and reference, original formatting evidence, all six tables, all 33 checkmarks, metadata, extraction warnings, saved draft identity and proposed classifications. Original-source SHA-256 still matches the stored upload. Export approval status and saved review status are both `awaitingReview`, attribution/time remain null, and no approved artifact exists.

Clearly named review-draft JSON and a separate analysis-report JSON were saved to the user's Downloads directory. The report includes original/suggested classifications, change flags, exact source locations, model reasoning and extraction warnings. Private document content remains outside the repository. The saved review can be reopened after restarting the app without rerunning analysis.
