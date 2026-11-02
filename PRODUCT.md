# Pyrion Knowledge Pipeline
<!-- impeccable:product-schema 1 -->

## Platform
web

## Stack
C# / .NET 10, ASP.NET Core Blazor Interactive Server, Windows localhost.

## Users and purpose
Engineering document reviewers prepare Persian and English documents, explicitly approve normalized JSON, and build a local index to retrieve source text with project, revision and block references. M1 covers DOCX extraction, local AI structural suggestions and human review; M2 adds approved knowledge indexing and retrieval testing.

## Confirmed constraints
The user approved M1 on November 1, 2026 and the M2 implementation plan on November 3, 2026. Source wording and original uploads stay unchanged. LM Studio is the only model service; content, embeddings and reviews stay on this computer. M2 uses optional local PostgreSQL/pgvector for derived retrieval data. Conversational RAG, generated answers, PDF/XLSX import and semantic cross-project deduplication remain outside M2.

## Operating context
Upload, extract, analyze, review, approve, download. Review states must survive reopening. Original locations, suggested classifications and reasons must remain visible together.

## Product principles
- Deterministic extraction before model analysis.
- Original wording is authoritative.
- Approval is explicit and tied to a specific draft.
- Errors describe recovery and never substitute fabricated results.

## Open decisions
No brand assets were supplied. Interface styling is an implementation choice within the approved simple, functional dashboard brief.
