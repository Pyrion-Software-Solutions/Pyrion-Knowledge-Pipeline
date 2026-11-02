using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class KnowledgeRepository(KnowledgeDbFactory factory) : IKnowledgeRepository
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, DocumentJson.Options);
    public async Task<IndexHealth> HealthAsync(CancellationToken ct)
    {
        try
        {
            await using var db = factory.Create();
            if (!await db.Database.CanConnectAsync(ct)) return new(false, "Cannot connect to PostgreSQL. Check the local service, database name and private credentials.");
            await db.Database.OpenConnectionAsync(ct);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_extension WHERE extname = 'vector')";
            if (!Equals(await command.ExecuteScalarAsync(ct), true)) return new(false, "The vector extension is not enabled. Install pgvector for this PostgreSQL version, then initialize the index database.");
            if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) return new(false, "Knowledge database migrations are pending. Initialize/update the index database in Indexing Settings.");
            return new(true, "PostgreSQL, pgvector and knowledge schema are ready.");
        }
        catch (PreparationException ex) { return new(false, ex.Message); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException)
        { return new(false, "Knowledge database is unavailable. Check connection configuration, credentials, pgvector and migration permissions."); }
    }
    public async Task MigrateAsync(CancellationToken ct)
    {
        try { await using var db = factory.Create(); await db.Database.MigrateAsync(ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        { throw new PreparationException("Database initialization failed. Confirm local credentials, database creation, pgvector installation and extension/schema permissions. Existing source files were not changed."); }
    }
    public async Task<IReadOnlyList<IndexRun>> ListRunsAsync(CancellationToken ct)
    {
        await using var db = factory.Create();
        var active = await db.Active.Select(x => x.RunId).ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(500).ToListAsync(ct);
        var recentIds = runs.Select(x => x.Id).ToList();
        runs.AddRange(await db.Runs.AsNoTracking().Where(x => active.Contains(x.Id) && !recentIds.Contains(x.Id)).ToListAsync(ct));
        var spaces = await db.Spaces.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        return runs.OrderByDescending(x => x.StartedAt).Select(x => Map(x, x.SpaceId is null ? null : spaces.GetValueOrDefault(x.SpaceId), active.Contains(x.Id))).ToList();
    }
    private static IndexRun Map(IndexGenerationRow x, EmbeddingSpaceRow? space, bool active) => new(x.Id, x.SourceDocumentId,
        x.ArtifactHash, x.SettingsFingerprint, Enum.Parse<IndexRunStatus>(x.Status), x.StartedAt, x.FinishedAt, x.CompletedChunks,
        x.TotalChunks, x.Error, space?.ModelId, space?.Dimensions, active, space?.Id, space?.ConfigurationFingerprint);
    public async Task<IndexRun?> FindCompletedAsync(Guid documentId, string sourceHash, string settingsHash, CancellationToken ct)
    {
        await using var db = factory.Create();
        var run = await db.Runs.AsNoTracking().Where(x => x.SourceDocumentId == documentId && x.ArtifactHash == sourceHash &&
            x.SettingsFingerprint == settingsHash && x.Status == "Completed").OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        if (run is null) return null;
        var space = await db.Spaces.FindAsync([run.SpaceId], ct);
        return Map(run, space, await db.Active.AnyAsync(x => x.RunId == run.Id, ct));
    }
    public async Task<IndexRun> BeginAsync(ApprovedSource source, string settingsHash, CancellationToken ct)
    {
        await using var db = factory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var d = source.Document;
        var identity = await db.Documents.SingleOrDefaultAsync(x => x.ProjectId == d.ProjectId && x.DocumentNumber == d.DocumentNumber, ct);
        if (identity is null) { identity = new() { Id = Guid.NewGuid(), ProjectId = d.ProjectId, DocumentNumber = d.DocumentNumber }; db.Documents.Add(identity); }
        var revision = await db.Revisions.FindAsync([d.DocumentId], ct);
        if (revision is null) db.Revisions.Add(new() { Id = d.DocumentId, DocumentId = identity.Id, ArtifactHash = source.ArtifactHash,
            Revision = d.Revision, Title = d.Title, Language = d.Language, SchemaVersion = d.SchemaVersion,
            ApprovedBy = d.Approval.ApprovedBy!, ApprovedAt = d.Approval.ApprovedAt!.Value.ToUniversalTime(), Json = source.Json });
        else if (revision.ArtifactHash != source.ArtifactHash || revision.DocumentId != identity.Id)
            throw new PreparationException("This immutable approved source has changed since import. Restore the original artifact; create a new approved source for revisions.");
        var row = new IndexGenerationRow { Id = Guid.NewGuid(), DocumentId = identity.Id, SourceDocumentId = d.DocumentId,
            ArtifactHash = source.ArtifactHash, SettingsFingerprint = settingsHash, StartedAt = DateTimeOffset.UtcNow };
        db.Runs.Add(row); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Map(row, null, false);
    }
    public async Task StageAsync(Guid runId, EmbeddingSpace space, IReadOnlyList<KnowledgeChunk> chunks, IReadOnlyList<float[]> vectors, int total, CancellationToken ct)
    {
        if (chunks.Count != vectors.Count || chunks.Count == 0 || vectors.Any(v => v.Length != space.Dimensions || v.Any(x => !float.IsFinite(x)) || v.All(x => x == 0)))
            throw new PreparationException("Invalid embedding batch cannot be staged.");
        await using var db = factory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var run = await db.Runs.SingleAsync(x => x.Id == runId, ct);
        if (run.Status is not ("Pending" or "Running") || run.SpaceId is not null && run.SpaceId != space.Fingerprint)
            throw new PreparationException("The index run is no longer writable or its embedding space changed.");
        if (!await db.Spaces.AnyAsync(x => x.Id == space.Fingerprint, ct)) db.Spaces.Add(new() { Id = space.Fingerprint,
            ModelId = space.ModelId, ModelVersion = space.ModelVersion, Dimensions = space.Dimensions,
            ConfigurationFingerprint = space.ConfigurationFingerprint, ProbeHash = space.ProbeHash });
        for (var i = 0; i < chunks.Count; i++) db.Chunks.Add(new() { RunId = runId, Id = chunks[i].Id, Ordinal = chunks[i].Ordinal,
            DisplayText = chunks[i].DisplayText, Json = Json(chunks[i]), SpaceId = space.Fingerprint, Dimensions = space.Dimensions, Vector = new(vectors[i]) });
        run.SpaceId = space.Fingerprint; run.Status = "Running"; run.CompletedChunks += chunks.Count; run.TotalChunks = total;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
    public async Task CompleteAsync(Guid runId, bool activateRevision, CancellationToken ct)
    {
        await using var db = factory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var run = await db.Runs.SingleAsync(x => x.Id == runId, ct);
        if (run.Status != "Running" || run.SpaceId is null || run.TotalChunks < 1 || run.CompletedChunks != run.TotalChunks ||
            await db.Chunks.CountAsync(x => x.RunId == runId && x.SpaceId == run.SpaceId, ct) != run.TotalChunks)
            throw new PreparationException("An incomplete generation cannot be completed or activated.");
        run.Status = "Completed"; run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var active = await db.Active.SingleOrDefaultAsync(x => x.DocumentId == run.DocumentId, ct);
        var sameRevision = active is not null && await db.Runs.AnyAsync(x => x.Id == active.RunId && x.SourceDocumentId == run.SourceDocumentId, ct);
        if (activateRevision || sameRevision) await SetActiveAsync(db, run, ct);
        await transaction.CommitAsync(ct);
    }
    public async Task ActivateAsync(Guid runId, CancellationToken ct)
    {
        await using var db = factory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var run = await db.Runs.SingleAsync(x => x.Id == runId && x.Status == "Completed", ct);
        await SetActiveAsync(db, run, ct); await transaction.CommitAsync(ct);
    }
    private static async Task SetActiveAsync(KnowledgeDbContext db, IndexGenerationRow run, CancellationToken ct)
    {
        // Parameterized UPSERT serializes concurrent revisions of the same project/document identity.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ActiveSelections\" (\"DocumentId\", \"RunId\") VALUES ({run.DocumentId}, {run.Id}) ON CONFLICT (\"DocumentId\") DO UPDATE SET \"RunId\" = EXCLUDED.\"RunId\"", ct);
    }
    public async Task FinishAsync(Guid runId, IndexRunStatus status, string error, CancellationToken ct)
    {
        if (status is not (IndexRunStatus.Failed or IndexRunStatus.Cancelled)) throw new ArgumentException("Only failure/cancellation can abort an index run.");
        await using var db = factory.Create();
        await db.Runs.Where(x => x.Id == runId && (x.Status == "Pending" || x.Status == "Running"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status.ToString()).SetProperty(x => x.Error, error)
                .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = factory.Create();
        await db.Runs.Where(x => x.Status == "Pending" || x.Status == "Running").ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.Status, "Failed").SetProperty(x => x.Error, "Interrupted by application shutdown. Retry starts a fresh generation.")
                .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
    }
    public async Task<List<RetrievalHit>> SearchAsync(RetrievalRequest request, EmbeddingSpace? space, float[]? vector, CancellationToken ct)
    {
        await using var db = factory.Create();
        var query = from active in db.Active
                    join run in db.Runs on active.RunId equals run.Id
                    join revision in db.Revisions on run.SourceDocumentId equals revision.Id
                    join doc in db.Documents on run.DocumentId equals doc.Id
                    join chunk in db.Chunks on run.Id equals chunk.RunId
                    join model in db.Spaces on chunk.SpaceId equals model.Id
                    where run.Status == "Completed" && run.SpaceId == chunk.SpaceId && run.DocumentId == revision.DocumentId
                    select new { run, revision, doc, chunk, model };
        if (request.ProjectId is { } project) query = query.Where(x => x.doc.ProjectId == project);
        if (request.DocumentId is { } document) query = query.Where(x => x.revision.Id == document);
        if (request.DocumentNumber is { } number) query = query.Where(x => x.doc.DocumentNumber == number);
        if (request.Revision is { } revisionFilter) query = query.Where(x => x.revision.Revision == revisionFilter);
        if (request.Language is { } language) query = query.Where(x => x.revision.Language == language);
        if (request.Mode == RetrievalMode.Semantic)
        {
            if (space is null || vector is null || vector.Length != space.Dimensions) throw new PreparationException("A compatible query vector is required.");
            query = query.Where(x => x.chunk.SpaceId == space.Fingerprint && x.chunk.Dimensions == space.Dimensions);
        }
        else
        {
            var literal = request.Query.ToLowerInvariant();
            query = query.Where(x => x.chunk.DisplayText.ToLower().Contains(literal) || x.doc.DocumentNumber == request.Query);
        }
        var queryVector = new Vector(vector ?? [1f]);
        var scored = query.Select(x => new { x.revision, x.doc, x.chunk, x.model,
            Score = request.Mode == RetrievalMode.Semantic ? 1.0 - x.chunk.Vector.CosineDistance(queryVector) : 1.0 });
        if (request.Mode == RetrievalMode.Semantic && request.MinimumSimilarity is { } minimum) scored = scored.Where(x => x.Score >= minimum);
        var rows = await scored.OrderByDescending(x => x.Score).ThenBy(x => x.doc.ProjectId).ThenBy(x => x.doc.DocumentNumber)
            .ThenBy(x => x.revision.Id).ThenBy(x => x.chunk.Ordinal).Take(request.TopK).AsNoTracking().ToListAsync(ct);
        return rows.Select(x => new RetrievalHit(x.revision.Id, x.doc.ProjectId, x.doc.DocumentNumber, x.revision.Title,
            x.revision.Revision, x.revision.Language, JsonSerializer.Deserialize<KnowledgeChunk>(x.chunk.Json, DocumentJson.Options)!, x.Score,
            request.Mode == RetrievalMode.Semantic ? "cosine similarity (1 − distance)" : "literal match",
            x.model.ModelId, x.model.Dimensions, Warnings(x.revision.Json), x.revision.ArtifactHash)).ToList();
    }
    private static List<string> Warnings(string json)
    {
        var doc = JsonSerializer.Deserialize<NormalizedDocument>(json, DocumentJson.Options)!;
        return doc.ExtractionWarnings ?? ["Legacy v1.0 approval: extraction coverage notes are unavailable."];
    }
}
