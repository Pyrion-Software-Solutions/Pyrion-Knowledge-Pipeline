using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class KnowledgeLifecycleTests
{
    [Theory]
    [InlineData("success")] [InlineData("failure")] [InlineData("cancel")]
    public async Task Only_completed_runs_activate_and_failed_reindex_preserves_previous_generation(string outcome)
    {
        using var w = new TestWorkspace(); var doc = await ApprovedSourceTests.ApproveAsync(w);
        await w.Settings.SaveAsync(EmbeddingProviderTests.Settings, default);
        var source = new ApprovedDocumentProvider(w.Store, new DocumentContractValidator());
        var embeddings = new FakeEmbeddings(); var repository = new MemoryKnowledgeRepository();
        using var service = new KnowledgeIndexService(source, new StructureAwareChunker(), w.Settings, embeddings, repository);
        await service.StartAsync(default);
        var run = await service.StartAsync(new(doc.Id, ActivateRevision: true), default);
        Assert.Equal(IndexRunStatus.Completed, await repository.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(repository.Runs[run].Active);
        Assert.Equal(run, await service.StartAsync(new(doc.Id, ActivateRevision: true), default));
        Assert.Single(repository.Runs);
        repository.Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (outcome == "failure") embeddings.Failure = true;
        if (outcome == "cancel") embeddings.Block = true;
        var retry = await service.StartAsync(new(doc.Id, ActivateRevision: true, Force: true), default);
        if (outcome == "cancel") { await embeddings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(service.Cancel(doc.Id)); }
        var status = await repository.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(outcome == "success" ? IndexRunStatus.Completed : outcome == "cancel" ? IndexRunStatus.Cancelled : IndexRunStatus.Failed, status);
        Assert.Equal(outcome == "success" ? retry : run, repository.Runs.Values.Single(x => x.Active).Id);
        await service.StopAsync(default);
    }
    [Fact]
    public async Task Invalid_query_is_rejected_before_database_or_model_work()
    {
        using var w = new TestWorkspace();
        var service = new KnowledgeRetrievalService(new MemoryKnowledgeRepository(), w.Settings, new FakeEmbeddings(),
            new ApprovedDocumentProvider(w.Store, new DocumentContractValidator()));
        await Assert.ThrowsAsync<PreparationException>(() => service.SearchAsync(new(" "), default));
        await Assert.ThrowsAsync<PreparationException>(() => service.SearchAsync(new("pump", TopK: 51), default));
        await Assert.ThrowsAsync<PreparationException>(() => service.SearchAsync(new("pump", MinimumSimilarity: double.NaN), default));
    }
    [Fact]
    public async Task Empty_optional_UI_filters_are_treated_as_no_filter()
    {
        using var w = new TestWorkspace(); await w.Settings.SaveAsync(EmbeddingProviderTests.Settings, default);
        var service = new KnowledgeRetrievalService(new MemoryKnowledgeRepository(), w.Settings, new FakeEmbeddings(),
            new ApprovedDocumentProvider(w.Store, new DocumentContractValidator()));
        var response = await service.SearchAsync(new("فشار آزمون پمپ", RetrievalMode.Semantic, "", null, "", "", "", 5, null), default);
        Assert.Empty(response.Hits);
    }
}

internal sealed class FakeEmbeddings : IEmbeddingProvider
{
    public bool Failure, Block;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IReadOnlyList<EmbeddingModel>> GetModelsAsync(LmStudioSettings settings, CancellationToken ct) => Task.FromResult<IReadOnlyList<EmbeddingModel>>([new("embedding-model", "embedding", 2048)]);
    public Task<EmbeddingSpace> ValidateAsync(LmStudioSettings settings, CancellationToken ct) => Task.FromResult(new EmbeddingSpace("space-v1", "embedding-model", "v1", 3, LmStudioEmbeddingProvider.ConfigurationFingerprint(settings), "probe"));
    public async Task<EmbeddingBatch> EmbedAsync(LmStudioSettings settings, EmbeddingSpace space, IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        Entered.TrySetResult();
        if (Failure) throw new PreparationException("Synthetic embedding failure");
        if (Block) await Task.Delay(Timeout.Infinite, ct);
        return new(texts.Select(_ => new[] { 1f, 0f, 0f }).ToList(), space.ModelId);
    }
}
internal sealed class MemoryKnowledgeRepository : IKnowledgeRepository
{
    public Dictionary<Guid, IndexRun> Runs = [];
    public TaskCompletionSource<IndexRunStatus> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IndexHealth> HealthAsync(CancellationToken ct) => Task.FromResult(new IndexHealth(true, "Synthetic test database"));
    public Task MigrateAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<IndexRun>> ListRunsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IndexRun>>(Runs.Values.ToList());
    public Task<IndexRun?> FindCompletedAsync(Guid documentId, string sourceHash, string settingsHash, CancellationToken ct) => Task.FromResult(Runs.Values.FirstOrDefault(x => x.SourceDocumentId == documentId && x.ArtifactHash == sourceHash && x.SettingsFingerprint == settingsHash && x.Status == IndexRunStatus.Completed));
    public Task<IndexRun> BeginAsync(ApprovedSource source, string settingsHash, CancellationToken ct)
    {
        var run = new IndexRun(Guid.NewGuid(), source.Document.DocumentId, source.ArtifactHash, settingsHash, IndexRunStatus.Pending, DateTimeOffset.UtcNow, null, 0, 0, null, null, null, false);
        Runs[run.Id] = run; return Task.FromResult(run);
    }
    public Task StageAsync(Guid runId, EmbeddingSpace space, IReadOnlyList<KnowledgeChunk> chunks, IReadOnlyList<float[]> vectors, int total, CancellationToken ct)
    { var run = Runs[runId]; Runs[runId] = run with { Status = IndexRunStatus.Running, CompletedChunks = run.CompletedChunks + chunks.Count, TotalChunks = total }; return Task.CompletedTask; }
    public Task CompleteAsync(Guid runId, bool activateRevision, CancellationToken ct)
    {
        Runs[runId] = Runs[runId] with { Status = IndexRunStatus.Completed };
        if (activateRevision || Runs.Values.Any(x => x.Active && x.SourceDocumentId == Runs[runId].SourceDocumentId)) ActivateAsync(runId, ct).GetAwaiter().GetResult();
        Finished.TrySetResult(IndexRunStatus.Completed); return Task.CompletedTask;
    }
    public Task FinishAsync(Guid runId, IndexRunStatus status, string error, CancellationToken ct)
    { Runs[runId] = Runs[runId] with { Status = status, Error = error }; Finished.TrySetResult(status); return Task.CompletedTask; }
    public Task RecoverAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ActivateAsync(Guid runId, CancellationToken ct)
    { foreach (var key in Runs.Keys.ToList()) Runs[key] = Runs[key] with { Active = key == runId }; return Task.CompletedTask; }
    public Task<List<RetrievalHit>> SearchAsync(RetrievalRequest request, EmbeddingSpace? space, float[]? vector, CancellationToken ct) => Task.FromResult(new List<RetrievalHit>());
}
