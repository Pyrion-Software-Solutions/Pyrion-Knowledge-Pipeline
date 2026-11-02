# M2: local knowledge index setup and recovery

M1 source storage remains authoritative. PostgreSQL stores a derived index plus a verified approved JSONB snapshot; it does not replace `IDocumentStore` or hold original DOCX bytes. Database outages do not prevent document preparation, review, approval or export.

## Prerequisites

- .NET SDK selected by `global.json`.
- A local PostgreSQL database and pgvector built for that PostgreSQL major version and architecture. During implementation PostgreSQL 18 was running, but pgvector files and database credentials were unavailable.
- LM Studio with its native `GET /api/v1/models` capability catalog and OpenAI-compatible `POST /v1/embeddings` endpoint.
- An embedding-capable local model; the chat analysis model remains separate. No model is selected automatically.

### Native Windows PostgreSQL

Docker is not required. Follow the [official pgvector Windows installation instructions](https://github.com/pgvector/pgvector#windows), using your actual PostgreSQL installation and a pinned pgvector release. Native compilation needs the Visual Studio C++ toolchain, PostgreSQL server headers, and permission to install extension files into PostgreSQL. SQL migrations cannot install a missing `vector.dll` or `vector.control`.

Using the PostgreSQL administrator account, create a dedicated local database and login through your usual administration tool. Provision `CREATE EXTENSION IF NOT EXISTS vector` in the target database if the application login cannot create extensions. Do not reuse a company production database for integration tests.

### Private connection configuration

The application reads `ConnectionStrings:KnowledgeIndex` through ASP.NET configuration. Configure `ConnectionStrings__KnowledgeIndex` privately in the environment before starting the application. The connection string needs a single loopback host, database, username and password. Comma-separated or remote hosts are rejected.

Alternatively, initialize ASP.NET user secrets for the Web project and configure `ConnectionStrings:KnowledgeIndex` there. Do not commit connection strings or passwords, pass them in command lines captured in shared logs, or put them in `appsettings.json`.

1. Restart the app after setting the private connection.
2. Open **Indexing Settings → Check database**.
3. Select **Initialize / update index database**. This is an explicit operation; migrations never run as a required startup dependency.
4. The page should report that PostgreSQL, pgvector and the knowledge schema are ready.

For repeatable CLI migrations, from the repository root:

```powershell
dotnet tool restore
dotnet ef database update --project src/PyrionKnowledgePipeline.Infrastructure --context KnowledgeDbContext
```

The design-time factory reads the same environment variable. Running migrations again is safe. Inspect generated SQL with `dotnet ef migrations script --idempotent --project src/PyrionKnowledgePipeline.Infrastructure --context KnowledgeDbContext`. Keep the generated migration and model snapshot in source control. Future schema changes require new migrations, rather than editing an applied migration.

## Embedding and chunk settings

1. Save the local `/v1` API URL in **LM Studio Settings**.
2. Open **Indexing Settings**, discover models and select an embedding-capable model.
3. Record its local artifact checksum or an explicit version label. Update this whenever model weights, quantization, preprocessing or relevant runtime settings change. The model ID alone is insufficient. A quantized bilingual sentinel probe detects common replacements but is not a cryptographic identity guarantee.
4. Enter model-specific document/query prefixes, batch size, timeout and the actual model context limit. Prefixes count toward the embedding input budget.
5. Save, then validate the saved model. The application empirically measures dimensions, validates count/indices/model identity/finite nonzero vectors, and rejects incompatible dimensions.

The locally evaluated `text-embedding-nomic-embed-text-v1.5` produced 768 dimensions and advertised 2,048 context tokens on this installation. Its [model card](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5) specifies `search_document: ` and `search_query: ` prefixes. These are evaluation settings, not application defaults or a claim that this is the best bilingual model. See the evaluation report for limitations.

Chunk defaults are target 500, maximum 750, overlap up to 40 estimated tokens. Experiments at 300, 500 and 1,000 preserve the bundled sample and synthetic corpus. These short fixtures do not establish an optimal size for large engineering documents. Counts use Unicode scalars divided by two, rounded up; this is an estimate, not the model tokenizer. The server may still reject input exceeding its actual token limit; the application never truncates to recover.

Preview chunks before indexing. Safe sentence/line splitting preserves UTF-16 source offsets. An oversized unsplittable block or complete table row fails explicitly. Confirm table header counts in the chunk preview if needed; M1 does not identify headers. Choices are derived settings scoped by source document ID and table reference. Empty text blocks and extraction notes are reported.

## Index and retrieve

1. Explicitly approve a document through M1, or use the clearly labeled nonconfidential test fixtures.
2. Open **Knowledge Index**, preview its chunks, then select **Index approved document**.
3. Pending/running generations remain inactive. Progress persists per completed embedding batch.
4. After successful indexing, select **Use this revision for search**. New revision literals never imply ordering or automatic supersession. Reindexing the currently active source replaces its generation only on successful completion.
5. Open **Retrieval Test**. Select semantic or literal mode, optional project/source/exact-number/revision/language filters and Top-K (default 5, maximum 50).
6. Inspect the original snippet, heading path, UTF-16 source spans and extraction coverage warnings. Source links reopen the locally approved review. No DOCX page numbers are invented.

Cosine similarity is `1 − cosine distance`, not confidence. An optional cutoff ranges from −1 to 1; it is model/dataset dependent. Semantic nearest-neighbor search can return unrelated sources for an unsupported query. Literal mode does case-insensitive substring matching and exact document-number matching, without stemming, Unicode rewriting or Persian spelling normalization. Source text is escaped Razor data.

Only completed, explicitly active generations in the query's compatible embedding space are searched semantically. Literal mode remains available without LM Studio. Metadata filters run in PostgreSQL before ranking. Ties use project, document number, source ID and chunk order. Multiple projects retain their separate sources even when text and document numbers match.

## Failure, retry and recovery

- One embedding worker processes at most four accepted documents at once including queued work. Batches are bounded to 1–32 texts; documents are limited to 10,000 derived chunks and approved artifacts to 100 MB. Queries are limited to 2,000 characters.
- HTTP 429/5xx responses get at most two retries with bounded backoff inside the request timeout. Invalid responses are rejected. No raw text, vectors or credentials are intentionally logged.
- Cancel stops remaining work and marks the generation cancelled. A successful previous active generation stays selected.
- A failed generation keeps its staged rows for diagnostics but cannot be searched. Retry starts a fresh generation; partial resume is not supported.
- On startup, unfinished pending/running generations are marked failed when the database is available. If the database is offline, recovery is retried before accepting new indexing work.
- Indexing unchanged approved bytes and compatible settings reuses the completed generation. **Reindex** deliberately rebuilds. Source/configuration/model checks run again before activation.
- Changing the embedding model or dimensions creates another embedding space. Incompatible active sources produce no semantic matches until reindexed; literal retrieval remains possible.
- Exact vector scans establish correctness. No ANN index, semantic deduplication or embedding cache is introduced in M2.

## Tests and real-model evaluation

```powershell
dotnet test PyrionKnowledgePipeline.sln
```

PostgreSQL integration tests are skipped with an explanation unless `PYRION_TEST_DATABASE` is configured in the process or Windows user environment. Use a disposable local database with pgvector. The test creates a randomly named `pyrion_test_<guid>` schema, applies migrations twice, exercises vector/literal queries, project/revision isolation, failed activation and recovery, and drops only that generated schema. Extension provisioning is database-wide; pre-enable pgvector in `public` if necessary. The test does not reset the user's document store.

To run the actual local-model evaluation:

```powershell
.\scripts\evaluate-retrieval.ps1 -Model 'your-local-embedding-model' -ModelVersion 'your-local-artifact-version' -DocumentPrefix 'model-specific document prefix' -QueryPrefix 'model-specific query prefix'
```

The evaluation uses committed synthetic fixtures only and exports scores, source references and latency, never vectors. Its exact cosine ranking runs in memory, so it does not measure PostgreSQL or full UI latency. Database integration and a live GUI journey are separate checks. A future representative, authorized corpus is needed before selecting production quality thresholds.

## Backups and rebuilds

Back up the complete local storage directory, including original DOCX, review snapshots, immutable approved JSON and settings. Back up PostgreSQL separately with `pg_dump` and verify restores into a separate local database. Protect environment/user-secret configuration independently.

To rebuild, initialize a fresh derived index database, point the private connection at it, then rediscover, index and explicitly activate the existing approved sources. No re-upload, source rewrite or reapproval is necessary. Preserve the old database until validation succeeds. No automatic destructive migration, source deletion or database cleanup command is supplied.
