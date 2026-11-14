# Pyrion Knowledge Pipeline

A Windows localhost application for preparing engineering documents in Persian and English. It extracts DOCX content deterministically, asks a local LM Studio model for structural suggestions, and produces a clearly marked JSON review draft. Approved normalized JSON requires explicit human approval.

M2 adds an optional local PostgreSQL/pgvector knowledge index, structure-aware chunks, separate LM Studio embeddings, and source-only retrieval. Open **Indexing Settings**, **Knowledge Index**, and **Retrieval Test** from the sidebar. See [database/model setup, migrations, recovery and smoke tests](docs/knowledge-index-setup.md) and the [measured synthetic retrieval baseline](docs/retrieval-evaluation.md). M1 remains usable without PostgreSQL.

## Prerequisites

- Windows with the .NET 10 SDK. This workspace was built with SDK 10.0.403.
- LM Studio installed with a chat-capable model and its local server running. Default URL: `http://localhost:1234/v1`.
- Internet access for the first NuGet package restore. The running application uses no cloud services or remote fonts.
- A modern browser. Microsoft Word is not required.

## Run locally

Run the launcher from PowerShell:

```powershell
.\scripts\run-app.ps1
```

The launcher works from any current folder and checks the .NET SDK first. It stops any process holding TCP port 5187, waits until the port is free, then restores/builds and starts the app at `http://localhost:5187`. Once the app responds successfully, it automatically opens that URL in your default browser. Running it again restarts the app. If a process cannot be stopped or the port remains occupied, the launcher reports the error and exits. Keep the terminal open while using the app; Ctrl+C stops it. LM Studio is started separately.

For manual setup and tests, use the following commands instead.

From the repository root in PowerShell:

```powershell
dotnet restore PyrionKnowledgePipeline.sln
dotnet build PyrionKnowledgePipeline.sln
dotnet test PyrionKnowledgePipeline.sln
dotnet run --project src/PyrionKnowledgePipeline.Web
```

Open [the local workspace](http://localhost:5187). Stop the server with Ctrl+C. Only loopback binding is allowed; do not add a public proxy or tunnel. This is a single-user local application, without user accounts. Only one process may write a given storage directory.

## Walk through the complete milestone

1. Open **LM Studio Settings**, set the `/v1` base URL, and select **Test connection**.
2. Choose an available **chat** model and select **Save settings**. The model list can also include embedding models; those cannot perform this analysis.
3. Open **Document Preparation**. Download the included sample and select **Fill sample metadata**, or supply your own project, number, title, revision and language.
4. Choose the source DOCX. Optionally import a DOCX template or UTF-8 TXT rules file, or enter rules directly.
5. Select **Upload & extract DOCX** and inspect the ordered source blocks and extraction notes.
6. Select **Request AI analysis**. Larger documents are processed automatically in smaller requests, with progress for each part. Cancel is available while analysis runs. No source text or table cell values are truncated.
7. Inspect the consolidated report and normalized preview. **Download saved draft JSON** exports the unapproved review draft immediately after successful analysis. Source text, table values and source references stay authoritative. The model can propose classifications and heading levels only.
8. Optionally open **Revise classifications manually**, change classifications, provide a reason, and save a new draft. Alternatively reject the draft, update rules, and analyze again.
9. Enter the reviewer name, confirm that you reviewed the report and extraction notes, and select **Approve & save JSON**.
10. Select **Download approved JSON**. Reopen the saved document from the dashboard after refreshing or restarting the application.

The included sample is illustrative engineering content, not an operating instruction. It includes an unstyled bold 18pt section title to demonstrate a possible structural issue.

## Local configuration and files

By default all settings and documents live under:

```text
%LOCALAPPDATA%\PyrionKnowledgePipeline\
  settings.json
  documents\<generated-document-id>\
    original.docx       unchanged uploaded bytes
    review.json         metadata, extraction, rules, draft, review status
    approved.json       created only on approval
```

Use **LM Studio Settings** to persist the API URL, selected model ID, and analysis timeout (5–600 seconds per request). A large document can take multiple requests. No model ID is hardcoded. Only `localhost` and loopback IP addresses are accepted, HTTP redirects and proxies are disabled, and requests use `/v1/models` and `/v1/chat/completions`.

To choose another storage location, add a `Storage` section to `src/PyrionKnowledgePipeline.Web/appsettings.json`:

```json
{
  "Storage": { "RootPath": "D:\\PyrionData" }
}
```

Or set `Storage__RootPath` in the process environment before starting the application. Use an absolute path outside the repository and web root. Changing the location selects another store; it does not migrate existing documents. Back up the whole directory to preserve source files, drafts, approvals and settings. The folder is not encrypted by the application; protect it using your Windows account and disk permissions.

Original files use generated document directories and a fixed internal filename. User filenames never determine storage paths. Draft and approved content are separate, writes use atomic file replacement, and version checks reject stale concurrent saves. An approved revision is immutable in the workflow; upload a separate document for a new revision. Reviewer names are local attribution, not verified identity or a digital signature.

## Contracts and boundaries

- [Completed work and handoff summary](WORK_SUMMARY.md)
- [Architecture](docs/architecture.md)
- [Approved normalized JSON schema v1.0 / v1.1](src/PyrionKnowledgePipeline.Infrastructure/Schemas/normalized-document-v1.schema.json)
- [Unapproved normalized draft schema v1.1](src/PyrionKnowledgePipeline.Infrastructure/Schemas/normalized-draft-v1.schema.json)
- [AI analysis proposal schema](src/PyrionKnowledgePipeline.Infrastructure/Schemas/analysis-proposal.schema.json)
- [Validation record](docs/validation.md)

JSON is structured document data, not a trained model or a vector representation. Original text and table cells are carried into the normalized document by the application. The model never supplies replacement wording. References such as `word/document.xml#body/4/row/1/cell/1` point to extraction locations, not rendered Word page numbers.

New exports use schema v1.1 and include `extractionWarnings`, so missing images, headers and other extraction limits remain visible in the JSON. Draft exports have `approval.status: "awaitingReview"` and null approval attribution/time. They are not approved documents. Previously saved v1.0 approvals remain readable and unchanged.

## Current limitations

- DOCX body paragraphs, heading levels 1–6, practical numbering metadata and simple tables only. Images, text boxes, headers, footers and notes are not extracted; visible extraction notes identify these cases. No OCR or visual Word pagination.
- Block content controls are unwrapped in stored order, including a stored table of contents and paragraphs inside table-cell controls; source references retain the wrapper location. Linked content and fields are not refreshed. Wingdings 2 `F050` checkmarks are extracted as Unicode `✓`, with a visible extraction note. Other symbol-font/code pairs and equations are rejected with specific errors instead of guessed or discarded.
- Nested tables and other unsupported body/cell structures are rejected. Merged cells retain source row/cell text but do not reproduce visual spanning. Stored field display text is not recalculated. Tracked changes produce a warning; resolve them in Word before preparing authoritative content.
- Direct run bold/font size evidence and inherited paragraph heading/list properties are extracted; this is not a full Word style or layout engine. Original list numbering IDs/levels are retained; rendered list numbers are not reconstructed.
- Upload limit: 10 MB; expanded DOCX limit: 50 MB and 2,000 ZIP parts. Reference TXT files are limited to 128 KB and imported rules to 20,000 characters.
- Analysis supports up to 1,000 blocks. Requests contain up to 32 blocks and 10,000 serialized input characters, including rules and preceding-heading context. Every source block is included exactly once. Table requests contain every cell value in row/cell order; repeated cell source-reference metadata stays in extraction/export rather than consuming model context. An oversized single block or rules file is rejected without truncation. Use a model with enough context; the real local workflow uses an 8,192-token context. Completion output is limited to 4,096 tokens per request.
- Unsupported schema mode triggers one visible plain-JSON retry only for an explicit schema/format rejection. An invalid result for a large-document part is retried once visibly. If any part still fails, no partial draft is saved and the previous saved draft remains available. Analysis batches are temporary model requests, not retrieval chunks or an index.
- The latest draft and any approved document persist; this milestone does not provide an audit log of every superseded draft or link separate uploads into a revision family. Rejected drafts cannot be approved until a new analysis creates a reviewable draft.
- LM Studio authentication is not implemented; use a loopback server without API authentication for this milestone.
- No XLSX/PDF parsing, OCR, generated answers or chat. M2 indexing uses one bounded worker inside the existing Web host; PostgreSQL/pgvector and LM Studio embeddings are optional local dependencies for retrieval.

## Tests

Automated unit and host-integration tests use a mocked LM Studio client or HTTP handler and isolated temporary storage. They do not require or alter the real LM Studio server. They exercise ordering and exact text, inherited headings, table references, file validation, schema and semantic validation, multi-request coverage and merging, failure handling, cancellation, manual revision, rejection, approval, stale writes, restart persistence, and distinct draft/approved JSON downloads.

Implementation references: [Blazor Interactive Server](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0), [Open XML SDK](https://learn.microsoft.com/en-us/office/open-xml/word/overview), [LM Studio structured output](https://lmstudio.ai/docs/developer/openai-compat/structured-output), and [NJsonSchema](https://github.com/RicoSuter/NJsonSchema).

## License

This project is licensed under the [MIT License](LICENSE).
