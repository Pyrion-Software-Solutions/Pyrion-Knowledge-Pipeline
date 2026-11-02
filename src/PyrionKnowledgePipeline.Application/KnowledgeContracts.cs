using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Application;

public interface IApprovedDocumentProvider
{
    Task<ApprovedSource> LoadAsync(Guid id, CancellationToken ct);
}
public interface IChunkingService
{
    ChunkPreview Build(ApprovedSource source, ChunkingSettings settings, CancellationToken ct);
}
public interface IEmbeddingProvider
{
    Task<IReadOnlyList<EmbeddingModel>> GetModelsAsync(LmStudioSettings settings, CancellationToken ct);
    Task<EmbeddingSpace> ValidateAsync(LmStudioSettings settings, CancellationToken ct);
    Task<EmbeddingBatch> EmbedAsync(LmStudioSettings settings, EmbeddingSpace space, IReadOnlyList<string> texts,
        bool query, CancellationToken ct);
}
public interface IKnowledgeRepository
{
    Task<IndexHealth> HealthAsync(CancellationToken ct);
    Task MigrateAsync(CancellationToken ct);
    Task<IReadOnlyList<IndexRun>> ListRunsAsync(CancellationToken ct);
    Task<IndexRun?> FindCompletedAsync(Guid documentId, string sourceHash, string settingsHash, CancellationToken ct);
    Task<IndexRun> BeginAsync(ApprovedSource source, string settingsHash, CancellationToken ct);
    Task StageAsync(Guid runId, EmbeddingSpace space, IReadOnlyList<KnowledgeChunk> chunks,
        IReadOnlyList<float[]> vectors, int total, CancellationToken ct);
    Task CompleteAsync(Guid runId, bool activateRevision, CancellationToken ct);
    Task FinishAsync(Guid runId, IndexRunStatus status, string error, CancellationToken ct);
    Task RecoverAsync(CancellationToken ct);
    Task ActivateAsync(Guid runId, CancellationToken ct);
    Task<List<RetrievalHit>> SearchAsync(RetrievalRequest request, EmbeddingSpace? space, float[]? vector, CancellationToken ct);
}
public interface IKnowledgeIndexService
{
    Task<ChunkPreview> PreviewAsync(Guid documentId, CancellationToken ct);
    Task<Guid> StartAsync(IndexRequest request, CancellationToken ct);
    bool Cancel(Guid documentId);
}
public interface IKnowledgeRetrievalService
{
    Task<RetrievalResponse> SearchAsync(RetrievalRequest request, CancellationToken ct);
}
