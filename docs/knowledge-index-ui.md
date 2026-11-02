# Knowledge index interface

These Operate-mode routes extend the existing workbench described in [DESIGN.md](../DESIGN.md): paper-white panels, dark teal navigation, system typography, labeled controls and textual feedback. They introduce no separate visual system.

- `/knowledge-index` presents approved sources as separated report rows with revision, indexing state, progress and actions. A chunk preview follows the list, with heading paths, unchanged text, coverage notes and expandable source spans/embedded context. Table-header choices remain derived index settings. Empty sources link to Document Preparation; unavailable database feedback links to Indexing Settings. Indexing, failed/cancelled, inactive, active and outdated states have explicit text.
- `/retrieval-test` places the query above semantic/literal mode and project, approved-source, exact-number, revision, language and Top-K filters. The optional cosine cutoff appears in semantic mode. Results display source text, metadata, heading paths, score/metric, expandable exact references, extraction warnings and a link to the approved review. No generated answer is displayed. Search progress and cancellation use the shared workbench feedback pattern.
- `/settings/indexing` separates the embedding/chunk form from the database status and initialization area. Chat settings remain linked at `/settings`. Saved-model validation, service progress, errors and notices are explicit. The parent settings navigation matches its own route so only Indexing Settings is active here.

Persian/English query and document text use automatic direction; source whitespace is preserved. References retain block locations and UTF-16 spans. Narrow layouts stack forms and report headings and wrap navigation/actions.

Desktop/mobile captures for all three routes were reviewed under `.impeccable/review/`. The navigation correction was independently confirmed at both widths. Database-populated indexing and retrieval states remain visually unverified by the user's explicit choice; their implementation is not screenshot validation. See [setup and recovery](knowledge-index-setup.md) for operational behavior.
