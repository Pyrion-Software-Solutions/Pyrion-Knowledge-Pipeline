# Pyrion Knowledge Pipeline
<!-- impeccable:product-schema 1 -->

## Platform
web

## Stack
C# / .NET 10, ASP.NET Core Blazor Interactive Server, Windows localhost.

## Users and purpose
Engineering document reviewers prepare Persian and English documents for future knowledge retrieval. This first milestone extracts DOCX structure, obtains local AI suggestions, and requires explicit human approval before exporting normalized JSON.

## Confirmed constraints
The user approved the initial milestone plan on November 1, 2026. Source wording and original uploads stay unchanged. LM Studio is the only model service; settings, documents and reviews stay on this computer. No database, RAG, embeddings, chunking, PDF or XLSX implementation in this milestone.

## Operating context
Upload, extract, analyze, review, approve, download. Review states must survive reopening. Original locations, suggested classifications and reasons must remain visible together.

## Product principles
- Deterministic extraction before model analysis.
- Original wording is authoritative.
- Approval is explicit and tied to a specific draft.
- Errors describe recovery and never substitute fabricated results.

## Open decisions
No brand assets were supplied. Interface styling is an implementation choice within the approved simple, functional dashboard brief.
