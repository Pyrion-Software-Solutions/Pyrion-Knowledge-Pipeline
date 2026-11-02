# M2 synthetic retrieval baseline — November 3, 2026

The actual local LM Studio model `text-embedding-nomic-embed-text-v1.5` was evaluated with synthetic, nonconfidential fixtures only. It returned **768 dimensions**. The local capability catalog advertised a **2,048-token** context limit. Document/query prefixes were `search_document: ` and `search_query: `, following the [model card](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5).

Model identity was recorded as an operator-labeled local evaluation version plus a sentinel/configuration fingerprint. The evaluation did not independently hash the model file or establish a preferred bilingual model.

## Dataset and method

The [committed dataset](../tests/PyrionKnowledgePipeline.Tests/Fixtures/retrieval-evaluation.json) contains six intentionally approved synthetic source fixtures across three projects, opaque revisions, English/Persian/mixed languages, similar pump clauses with different pressures, a table-derived temperature, a heading/subsection, exact document numbers/codes and unsupported queries. Engineering values are illustrative, not operating instructions.

Eleven queries were manually labeled before running retrieval. Nine have expected source/block sets; two are unsupported. Six supported queries use semantic retrieval; three use literal/exact-identifier lookup. Top-K is 5. Recall@5 is the fraction of expected source/block pairs present among primary source spans in returned chunks, averaged over the nine supported queries. Unsupported queries have no recall denominator and are reported separately.

The test uses the application chunker and actual local embedding adapter, then exact **in-memory cosine ranking**. It does not measure PostgreSQL behavior or full browser search latency. It does not save vectors. Raw per-query scores, returned references, configuration identity and measured latency are in [the result artifact](retrieval-evaluation-results.json).

## Measured baseline

| Check | Result |
| --- | --- |
| Supported queries | 9 |
| Mean Recall@5 | 1.0 |
| Derived chunks at target 300 / 500 / 1,000 | 15 / 15 / 15 |
| Supported semantic query latency | 38–102 ms, embedding plus in-memory ranking |
| Literal lookup latency | 0.10–1.12 ms, in-memory only |
| Unsupported exact identifier | 0 returned chunks |
| Unsupported semantic question | 5 returned neighbors; none labeled relevant |

The 500-token target / 750-token maximum / 40-token maximum overlap is a provisional starting point. All three candidates preserved the short fixtures; these samples do not distinguish a best chunk size. Additional automated tests preserve the bundled DOCX sample, long bilingual paragraphs and complete table rows across size boundaries.

## Interpretation and next evaluation

This is a small regression fixture, with several filters producing fewer than five candidates and bilingual context making some matches easy. Recall@5 of 1.0 on this set does not establish production Persian quality, cross-language equivalence, numerical discrimination or robustness across a large corpus. The unsupported semantic query confirms that nearest-neighbor search still returns content without supporting evidence when no cutoff is applied. No universal similarity threshold is proposed from these results.

Use Recall@5 ≥ this fixture's baseline as a regression check for this exact fixture only; treat any decline as a prompt to inspect returned source/block pairs. Before choosing a production embedding model or similarity cutoff, add an authorized representative bilingual corpus with many near-miss clauses, project/revision collisions, Persian spelling variations and substantially more candidates per filter; compare Recall@1/5, unsupported-query false positives and PostgreSQL/end-to-end latency. Select thresholds from those measurements.

## Reproduce

Start LM Studio locally with the intended embedding model, then run `scripts/evaluate-retrieval.ps1` with explicit model ID/version and its documented prefixes. See [setup and tests](knowledge-index-setup.md). Automated model tests use HTTP mocks by default; the real-model evaluation is opt-in.
