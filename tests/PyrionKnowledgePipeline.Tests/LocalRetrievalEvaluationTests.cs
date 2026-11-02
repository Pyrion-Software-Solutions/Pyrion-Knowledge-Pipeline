using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;
using Xunit.Abstractions;

namespace PyrionKnowledgePipeline.Tests;

public sealed class LocalEmbeddingFactAttribute : FactAttribute
{
    public LocalEmbeddingFactAttribute() { if (Environment.GetEnvironmentVariable("PYRION_RUN_LM_SMOKE") != "1") Skip = "Optional local model evaluation: set PYRION_RUN_LM_SMOKE=1 and PYRION_EVAL_MODEL to an available LM Studio embedding model."; }
}
public class LocalRetrievalEvaluationTests(ITestOutputHelper output)
{
    public sealed record EvaluationData(string Description, List<EvaluationSource> Sources, List<EvaluationQuery> Queries);
    public sealed record EvaluationSource(string Key, string Project, string Number, string Revision, string Language, List<DocumentBlock> Blocks);
    public sealed record EvaluationQuery(string Id, string Query, RetrievalMode Mode, List<string> ExpectedSources, List<string> ExpectedBlocks,
        string? Project = null, string? DocumentNumber = null, string? Revision = null, string? Language = null, bool Unsupported = false);

    [LocalEmbeddingFact]
    public async Task Evaluate_labeled_bilingual_fixture_with_real_local_embeddings()
    {
        var modelId = Environment.GetEnvironmentVariable("PYRION_EVAL_MODEL") ?? throw new InvalidOperationException("Set PYRION_EVAL_MODEL explicitly; no production model is selected automatically.");
        var services = new ServiceCollection();
        services.AddHttpClient("LmStudio", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        await using var provider = services.BuildServiceProvider();
        var embeddings = new LmStudioEmbeddingProvider(provider.GetRequiredService<IHttpClientFactory>());
        var settings = new LmStudioSettings(Embedding: new(modelId, Environment.GetEnvironmentVariable("PYRION_EVAL_MODEL_VERSION") ?? "operator-labeled-local-evaluation",
            DocumentPrefix: Environment.GetEnvironmentVariable("PYRION_EVAL_DOCUMENT_PREFIX") ?? "",
            QueryPrefix: Environment.GetEnvironmentVariable("PYRION_EVAL_QUERY_PREFIX") ?? ""));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var ct = timeout.Token;
        var space = await embeddings.ValidateAsync(settings, ct);
        var data = JsonSerializer.Deserialize<EvaluationData>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval-evaluation.json"), ct), DocumentJson.Options)!;
        var indexed = new List<(EvaluationSource Source, KnowledgeChunk Chunk, float[] Vector)>();
        var chunker = new StructureAwareChunker();
        var candidateCounts = new Dictionary<int, int>();
        foreach (var source in data.Sources)
        {
            var identity = new Guid(Convert.FromHexString(KnowledgeHash.Of(source.Key))[..16]);
            var document = KnowledgeChunkingTests.Source(source.Blocks).Document with { DocumentId = identity, ProjectId = source.Project,
                DocumentNumber = source.Number, Title = "Synthetic evaluation: " + source.Key, Revision = source.Revision, Language = source.Language };
            var json = JsonSerializer.Serialize(document, DocumentJson.Options); var approved = new ApprovedSource(document, KnowledgeHash.Of(json), json);
            foreach (var target in new[] { 300, 500, 1000 }) candidateCounts[target] = candidateCounts.GetValueOrDefault(target) + chunker.Build(approved, new(target, target + 250, 40), ct).Chunks.Count;
            var chunks = chunker.Build(approved, new(), ct).Chunks;
            foreach (var batch in chunks.Chunk(8))
            {
                var vectors = await embeddings.EmbedAsync(settings, space, batch.Select(x => x.EmbeddingText).ToList(), false, ct);
                indexed.AddRange(batch.Select((chunk, i) => (source, chunk, vectors.Vectors[i])));
            }
        }
        var results = new List<object>(); var recalls = new List<double>();
        foreach (var query in data.Queries)
        {
            var timer = Stopwatch.StartNew();
            var candidates = indexed.Where(x => (query.Project is null || x.Source.Project == query.Project) &&
                (query.DocumentNumber is null || x.Source.Number == query.DocumentNumber) && (query.Revision is null || x.Source.Revision == query.Revision) &&
                (query.Language is null || x.Source.Language == query.Language));
            float[]? vector = null;
            if (query.Mode == RetrievalMode.Semantic) vector = (await embeddings.EmbedAsync(settings, space, [query.Query], true, ct)).Vectors[0];
            else candidates = candidates.Where(x => x.Chunk.DisplayText.Contains(query.Query, StringComparison.OrdinalIgnoreCase) || x.Source.Number == query.Query);
            var ranked = candidates.Select(x => new { x.Source, x.Chunk, Score = vector is null ? 1.0 : Cosine(vector, x.Vector) })
                .OrderByDescending(x => x.Score).ThenBy(x => x.Source.Key, StringComparer.Ordinal).ThenBy(x => x.Chunk.Ordinal).Take(5).ToList();
            var expected = query.ExpectedSources.SelectMany(source => query.ExpectedBlocks.Select(block => source + "/" + block)).ToHashSet();
            var found = ranked.SelectMany(x => x.Chunk.Sources.Where(s => !s.ContextOnly).Select(s => x.Source.Key + "/" + s.BlockReference)).ToHashSet();
            double? recall = expected.Count == 0 ? null : expected.Count(found.Contains) / (double)expected.Count;
            if (recall is { } value) recalls.Add(value);
            results.Add(new { query.Id, query.Query, query.Mode, query.Project, query.Revision, query.ExpectedSources, query.ExpectedBlocks,
                RecallAt5 = recall, LatencyMilliseconds = timer.Elapsed.TotalMilliseconds,
                UnsupportedReturnedChunks = query.Unsupported ? ranked.Count : (int?)null,
                Returned = ranked.Select(x => new { Source = x.Source.Key, x.Chunk.Ordinal, x.Score, Blocks = x.Chunk.Sources.Select(s => s.SourceReference) }).ToList() });
        }
        var report = new { Scope = "Real local embeddings + exact in-memory cosine; PostgreSQL latency and end-to-end UI are not measured by this evaluation.",
            Model = space, Chunking = new ChunkingSettings(), CandidateChunkCounts = candidateCounts,
            RecallAt5 = recalls.Average(), Results = results };
        var rendered = JsonSerializer.Serialize(report, DocumentJson.Options); output.WriteLine(rendered);
        var reportPath = Environment.GetEnvironmentVariable("PYRION_EVAL_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath)) await File.WriteAllTextAsync(Path.GetFullPath(reportPath), rendered, ct);
        Assert.Equal(space.Fingerprint, (await embeddings.ValidateAsync(settings, ct)).Fingerprint);
        Assert.NotEmpty(indexed); // Quality is measured and reported; there is no invented universal pass threshold.
    }
    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return dot / Math.Sqrt(aa * bb);
    }
}
