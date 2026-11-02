# Pyrion Knowledge Pipeline — Work Summary

Updated: November 3, 2026

## M2 implementation update

The user approved the M2 plan on November 3. The existing solution now includes an approved-artifact integrity bridge, deterministic structure-aware chunks/source spans, a separate local embedding adapter/settings, EF Core/Npgsql/pgvector migrations and persistence, staged indexing with cancellation/retry/recovery, explicit active revision selection, exact cosine/literal retrieval, and Blazor Knowledge Index, Indexing Settings and Retrieval Test pages.

The latest full automated run passed **91 tests**, with **2 explicit opt-in skips** (PostgreSQL integration and real-model evaluation). Build output has zero warnings/errors. The real-model evaluation was also enabled and executed separately: the installed local Nomic v1.5 embedding model returned 768 dimensions; the small synthetic labeled fixture achieved Recall@5 of 1.0 for nine supported queries. An unsupported semantic query returned five irrelevant neighbors, demonstrating the need for representative quality/threshold evaluation. Scores and latency are recorded without vectors in `docs/retrieval-evaluation-results.json`; see `docs/retrieval-evaluation.md` for the scope and limitations.

EF reports no pending model changes and successfully generated an idempotent migration script. Database schema execution, pgvector distance queries, PostgreSQL rollback/constraints, persistent retrieval across application restart and the complete live indexing GUI journey are **pending environment validation**: PostgreSQL 18 is present, but credentials/test connection and pgvector were not available during implementation. Do not mark all M2 acceptance criteria verified until the opted-in PostgreSQL test and live smoke journey pass. `PYRION_TEST_DATABASE` can be supplied at process or Windows User scope for isolated-schema tests; no password belongs in this handoff.

The original M1 workflow remains covered by regression tests and is available with the database unconfigured. Chat-setting saves preserve M2 settings. The storage lease now releases on host shutdown before disposal completes, including in-process hosts with a background service. Existing user document folders, approvals and source bytes were not rewritten or indexed for testing. The historical M1 handoff below remains as originally recorded; current local approval state may differ.

Setup, migration, private configuration, model selection, retry/rebuild and backup instructions are in `docs/knowledge-index-setup.md`. The suggested next milestone after M2 verification is retrieval-quality hardening on an authorized representative bilingual corpus; conversational RAG remains a separate explicit scope decision.

## M1 outcome and status (historical handoff)

The initial milestone is implemented: a Windows localhost application that extracts DOCX documents, uses a local LM Studio model to suggest document structure, supports human review, and exports normalized JSON. The application preserves source wording and table values while allowing reviewers to correct classifications and heading levels.

The complete implementation is recorded in commit `305ce21` (`feat: implement local DOCX preparation and review workflow`). This summary documents that work and the subsequent handoff status.

The real user document was extracted and analyzed successfully. Its JSON review draft and separate analysis report were delivered to the user's Downloads folder. The saved document remains **awaiting human review**; analysis did not approve it automatically.

## Completed work

### Application foundation

- Created a .NET 10 solution with Domain, Application, Infrastructure, Web, and automated test projects.
- Built a Blazor Interactive Server interface for the dashboard, document preparation, connection settings, review, and export.
- Added English and Persian text support, including direction-aware content rendering.
- Kept processing and storage local, with no cloud service required for extraction or analysis.

### DOCX upload and extraction

- Added metadata entry for project, document number, title, revision, and language.
- Added DOCX upload, optional reference DOCX/TXT rules import, and direct rules entry.
- Preserved original uploaded bytes in a separate source file.
- Extracted main-body paragraphs, headings, lists, simple tables, and formatting evidence in source order.
- Preserved paragraph wording, whitespace, table-cell values, and source references.
- Supported inherited heading/list properties and block content controls, including stored table-of-contents content.
- Converted the verified Wingdings 2 `F050` checkmark to ordinary Unicode `✓`, with a visible extraction note.
- Added specific errors for unsupported equations, unknown symbol-font mappings, nested tables, and unsupported structures.
- Displayed extraction notes so reviewers can identify content outside the supported extraction scope.

### Local AI analysis

- Added configurable LM Studio connection settings, connection testing, and model discovery.
- Used the selected chat model without hardcoding a model ID in the application.
- Restricted model proposals to classifications, heading levels, and review explanations. Source wording and table values are assembled from extraction data.
- Added structured response validation, duplicate-key detection, exact source coverage checks, and classification rules.
- Added progress reporting, cancellation, per-request timeouts, and limited visible retries.
- Expanded support to documents containing up to 1,000 extracted blocks.
- Split larger analyses into contiguous requests of at most 32 blocks and 10,000 serialized input characters, without truncating source text or table values.
- Combined successful requests into one review draft only after all parts passed validation. Failed analysis preserves the previous saved draft.

### Review and JSON exports

- Added a consolidated review report and a normalized document preview.
- Added manual classification revisions with reviewer explanations and a new draft identity for each saved revision.
- Added rejection and reanalysis, plus explicit approval of the exact reviewed draft.
- Added separate downloads for unapproved review JSON and approved JSON.
- Introduced normalized schema v1.1 with extraction warnings while retaining support for existing v1.0 approvals.
- Ensured review drafts have `approval.status: "awaitingReview"` and null approval attribution/time.
- Kept approved document revisions immutable within the workflow.

### Local persistence and integrity

- Saved settings, unchanged source files, extraction data, review state, and approved JSON locally.
- Used generated document directories so user filenames do not determine storage paths.
- Added atomic file replacement, stale-save version checks, per-document write locks, and a single-process storage lease.
- Verified that saved documents and settings can be reopened after an application restart.
- Restricted the application and LM Studio connection to loopback addresses.

### Launcher and repository preparation

- Created `scripts/run-app.ps1` as the PowerShell launcher.
- Made the launcher resolve the repository from its own location and verify the required SDK.
- Added automatic stopping of processes holding port 5187 and a wait for the port to become free before startup.
- Added automatic opening of the default browser after the application responds successfully.
- Added project-specific `.gitignore` rules for generated files, local settings, uploads, private exports, logs, and temporary files.
- Kept shared configuration, schemas, source code, and the intentional sample DOCX eligible for Git.
- Added setup instructions, architecture documentation, product/design context, and validation records.

## Real-document analysis result

The following results were recorded during verification on November 2, 2026:

| Item | Result |
| --- | --- |
| Extracted blocks | 249 |
| Extracted tables | 6 |
| Converted checkmarks | 33 |
| Local-model requests | 8 |
| Review entries | 232 |
| Proposed classification changes | 34 |
| Saved review status | Awaiting review |
| Source and stored original | SHA-256 hashes match |

The run used the selected `google/gemma-3-4b` model locally, with an 8,192-token context and a saved timeout of 600 seconds per request. These were verification settings, rather than fixed application requirements. Processing speed depends on the local model and hardware.

The review draft was checked against the saved extraction: all 249 ordered blocks, source references, text, table rows/cells, formatting evidence, metadata, and extraction warnings were preserved. No approved artifact was created for this document.

Delivered files use these names:

- `FEP-DD-00-PM-PRC-0004-B0.review-draft.json`
- `FEP-DD-00-PM-PRC-0004-B0.analysis-report.json`

They were saved outside the repository in the user's Downloads folder. Private source content and exports are not included in the project documentation.

## Verification completed

The latest recorded implementation validation passed **61 automated tests**, with zero build warnings or errors. The suite covers extraction fidelity, invalid uploads, JSON contracts, model response validation, multi-request analysis, cancellation, failed-analysis behavior, manual revision, rejection, approval, stale writes, persistence, and separate draft/approved download routes.

Browser verification exercised settings, model discovery, file upload, extraction, real local analysis, review, manual correction, sample approval, JSON download, and reopening saved state after restart. The real user document remained unapproved. The launcher was checked for port replacement, successful startup, and automatic browser opening.

These are recorded results from implementation verification. Creating this summary does not represent a new test run. See [the validation record](docs/validation.md) for details and the progression from the earlier extraction limit to full-document analysis.

## Run the application

Prerequisites: Windows, the .NET 10 SDK selected by `global.json` (10.0.403 with latest-patch roll-forward), and LM Studio with a chat model and local API server running. The first package restore requires internet access.

From the repository root, run:

```powershell
.\scripts\run-app.ps1
```

The launcher frees port 5187, starts the application, and opens [the local workspace](http://localhost:5187) when ready. Keep the terminal open; Ctrl+C stops the application. LM Studio starts separately, with the default API address `http://localhost:1234/v1`.

To run automated checks:

```powershell
dotnet test PyrionKnowledgePipeline.sln
```

## Storage and export contracts

Default storage:

```text
%LOCALAPPDATA%\PyrionKnowledgePipeline\
  settings.json
  .instance.lock
  documents\<generated-document-id>\
    original.docx
    review.json
    approved.json    # created only after approval
```

Back up the whole storage directory to retain settings, source files, drafts, and approvals. Changing the configured storage location selects another store; it does not migrate existing data.

The export contracts are maintained in:

- [Approved normalized document schema](src/PyrionKnowledgePipeline.Infrastructure/Schemas/normalized-document-v1.schema.json)
- [Unapproved review draft schema](src/PyrionKnowledgePipeline.Infrastructure/Schemas/normalized-draft-v1.schema.json)
- [AI analysis proposal schema](src/PyrionKnowledgePipeline.Infrastructure/Schemas/analysis-proposal.schema.json)

## Current boundaries

- Extraction covers supported DOCX main-body paragraphs and simple tables. Headers, footers, footnotes, endnotes, images, and text boxes are outside this milestone.
- Equations and unknown symbol-font mappings require conversion in a source copy before extraction. The verified Wingdings 2 checkmark mapping is supported.
- Fields, linked content, and table-of-contents entries are read as stored; they are not refreshed. Merged cells retain text in source order without visual spanning.
- Uploads are limited to 10 MB; expanded DOCX content to 50 MB and 2,000 ZIP parts. Imported rules are limited to 20,000 characters.
- Analysis supports up to 1,000 blocks. An individual block or rules input that cannot fit a bounded request is rejected without truncation.
- Model suggestions require human review. Passing validation establishes contract integrity and content preservation, rather than classification accuracy.
- The application is a local single-user tool. Reviewer names provide local attribution, without identity verification or digital signatures.
- PDF/XLSX parsing, database storage, retrieval chunking, embeddings, vector search, and a chatbot remain future work. Temporary analysis batches do not create a retrieval index.

## Next action

Open the saved document from the dashboard and review the extraction notes and consolidated report, especially the 34 proposed classification changes. Compare omitted or visually complex content with the original DOCX. Correct classifications manually or reject and reanalyze as needed.

After reviewing the complete draft, enter a reviewer name, confirm the review, and select **Approve & save JSON**. Download the approved JSON for downstream use. The delivered review draft can already be inspected as structured JSON, but remains unapproved.

## Related documentation

- [README and setup guide](README.md)
- [Architecture and workflow rules](docs/architecture.md)
- [Detailed validation record](docs/validation.md)
- [Product context](PRODUCT.md)
- [Design context](DESIGN.md)
