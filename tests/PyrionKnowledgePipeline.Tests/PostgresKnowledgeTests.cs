using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public static string? Connection => Environment.GetEnvironmentVariable("PYRION_TEST_DATABASE") ?? Environment.GetEnvironmentVariable("PYRION_TEST_DATABASE", EnvironmentVariableTarget.User);
    public PostgresFactAttribute() { if (string.IsNullOrWhiteSpace(Connection)) Skip = "Set PYRION_TEST_DATABASE to a disposable local PostgreSQL database with pgvector installed. Each test uses an isolated schema."; }
}
public class PostgresKnowledgeTests
{
    [PostgresFact]
    public async Task Migrations_staging_atomic_activation_filtering_project_isolation_and_restart_round_trip()
    {
        var schema = "pyrion_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(PostgresFactAttribute.Connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(PostgresFactAttribute.Connection!) { SearchPath = schema + ",public" };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:KnowledgeIndex"] = connection.ConnectionString }).Build();
            var factory = new KnowledgeDbFactory(config); var repository = new KnowledgeRepository(factory);
            await repository.MigrateAsync(default); await repository.MigrateAsync(default);
            Assert.True((await repository.HealthAsync(default)).Available);
            var space = new EmbeddingSpace("integration-space", "synthetic-3d", "fixture-v1", 3, "config", "probe");
            var chunker = new StructureAwareChunker(); var a = KnowledgeChunkingTests.Source();
            var b = Reidentify(a, Guid.NewGuid(), "Synthetic-B", "01");
            var runA = await IndexAsync(repository, chunker, a, space, true);
            await IndexAsync(repository, chunker, b, space, true);
            var hits = await repository.SearchAsync(new("pressure", ProjectId: a.Document.ProjectId), space, [1f, 0f, 0f], default);
            Assert.NotEmpty(hits); Assert.All(hits, hit => Assert.Equal(a.Document.ProjectId, hit.ProjectId));
            Assert.All(hits, hit => Assert.Equal(a.ArtifactHash, hit.ArtifactHash));
            var exact = await repository.SearchAsync(new(a.Document.DocumentNumber, RetrievalMode.Literal, ProjectId: "Synthetic-B"), null, null, default);
            Assert.NotEmpty(exact); Assert.All(exact, h => Assert.Equal("01", h.Revision));
            var fa = await repository.SearchAsync(new("فشار", RetrievalMode.Literal, ProjectId: a.Document.ProjectId), null, null, default);
            Assert.Contains(fa, x => x.Chunk.DisplayText.Contains("۱۲"));
            Assert.Empty(await repository.SearchAsync(new("x' OR 1=1 --", RetrievalMode.Literal), null, null, default));
            Assert.Empty(await repository.SearchAsync(new("pressure", Revision: "nonexistent"), space, [1f, 0f, 0f], default));
            Assert.Empty(await repository.SearchAsync(new("pressure"), space with { Fingerprint = "other-model" }, [1f, 0f, 0f], default));
            var incomplete = await repository.BeginAsync(a, "different-settings", default);
            await Assert.ThrowsAsync<PreparationException>(() => repository.CompleteAsync(incomplete.Id, true, default));
            // Database constraints also reject bypassing the Application activation gate.
            await using (var db = factory.Create())
                await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActiveSelections\" SET \"RunId\" = {incomplete.Id} WHERE \"RunId\" = {runA.Id}"));
            await repository.FinishAsync(incomplete.Id, IndexRunStatus.Failed, "Synthetic model failure", default);
            Assert.True((await repository.ListRunsAsync(default)).Single(x => x.Id == runA.Id).Active);
            var interrupted = await repository.BeginAsync(a, "interrupted-settings", default);
            var restarted = new KnowledgeRepository(new KnowledgeDbFactory(config));
            await restarted.RecoverAsync(default);
            Assert.Equal(IndexRunStatus.Failed, (await restarted.ListRunsAsync(default)).Single(x => x.Id == interrupted.Id).Status);
            Assert.NotEmpty(await restarted.SearchAsync(new("P-101", RetrievalMode.Literal, DocumentId: a.Document.DocumentId), null, null, default));
            var revision = Reidentify(a, Guid.NewGuid(), a.Document.ProjectId, "A");
            var inactive = await IndexAsync(repository, chunker, revision, space, false);
            Assert.True((await repository.ListRunsAsync(default)).Single(x => x.Id == runA.Id).Active);
            await repository.ActivateAsync(inactive.Id, default);
            Assert.All(await repository.SearchAsync(new("pressure", ProjectId: a.Document.ProjectId), space, [1f, 0f, 0f], default), x => Assert.Equal("A", x.Revision));
            var changed = a with { ArtifactHash = new string('f', 64) };
            await Assert.ThrowsAsync<PreparationException>(() => repository.BeginAsync(changed, "changed", default));
        }
        finally
        {
            // Only the generated, validated schema is removed; no user tables or source files are touched.
            Assert.Matches("^pyrion_test_[0-9a-f]{32}$", schema);
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
    internal static ApprovedSource Reidentify(ApprovedSource source, Guid id, string project, string revision)
    {
        var doc = source.Document with { DocumentId = id, ProjectId = project, Revision = revision };
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options); return new(doc, KnowledgeHash.Of(json), json);
    }
    private static async Task<IndexRun> IndexAsync(IKnowledgeRepository repository, IChunkingService chunker, ApprovedSource source, EmbeddingSpace space, bool active)
    {
        var preview = chunker.Build(source, new(), default);
        var run = await repository.BeginAsync(source, "integration-settings", default);
        await repository.StageAsync(run.Id, space, preview.Chunks, preview.Chunks.Select(_ => new[] { 1f, 0f, 0f }).ToList(), preview.Chunks.Count, default);
        await repository.CompleteAsync(run.Id, active, default); return run;
    }
}
