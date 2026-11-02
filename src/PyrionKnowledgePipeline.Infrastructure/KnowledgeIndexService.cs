using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class KnowledgeIndexService(IApprovedDocumentProvider sources, IChunkingService chunker,
    ISettingsStore settingsStore, IEmbeddingProvider embeddings, IKnowledgeRepository repository)
    : BackgroundService, IKnowledgeIndexService
{
    private sealed record Job(IndexRun Run, IndexRequest Request, LmStudioSettings Settings, EmbeddingSpace Space, ChunkPreview Preview);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> operations = new();
    private readonly SemaphoreSlim startGate = new(1);
    private bool recovered;
    public async Task<ChunkPreview> PreviewAsync(Guid documentId, CancellationToken ct) =>
        chunker.Build(await sources.LoadAsync(documentId, ct), (await settingsStore.LoadAsync(ct)).Chunking ?? new(), ct);

    public static string Fingerprint(ChunkingSettings chunking, EmbeddingSpace space) => KnowledgeHash.Of($"{chunking.Fingerprint}|{space.Fingerprint}");
    public async Task<Guid> StartAsync(IndexRequest request, CancellationToken ct)
    {
        await startGate.WaitAsync(ct);
        try
        {
            if (operations.ContainsKey(request.SourceDocumentId)) throw new PreparationException("This source is already queued or indexing.");
            if (operations.Count >= 4) throw new PreparationException("The local indexing queue is full (four documents). Wait for a run to finish.");
            var health = await repository.HealthAsync(ct);
            if (!health.Available) throw new PreparationException(health.Message);
            if (!recovered) { await repository.RecoverAsync(ct); recovered = true; }
            var source = await sources.LoadAsync(request.SourceDocumentId, ct);
            var settings = await settingsStore.LoadAsync(ct);
            var preview = chunker.Build(source, settings.Chunking ?? new(), ct);
            var space = await embeddings.ValidateAsync(settings, ct);
            var fingerprint = Fingerprint(settings.Chunking ?? new(), space);
            if (!request.Force && await repository.FindCompletedAsync(request.SourceDocumentId, source.ArtifactHash, fingerprint, ct) is { } existing)
            {
                if (request.ActivateRevision) await repository.ActivateAsync(existing.Id, ct);
                return existing.Id;
            }
            var run = await repository.BeginAsync(source, fingerprint, ct);
            var cancellation = new CancellationTokenSource();
            operations[request.SourceDocumentId] = cancellation;
            if (!queue.Writer.TryWrite(new(run, request, settings, space, preview)))
            {
                operations.TryRemove(request.SourceDocumentId, out _); cancellation.Dispose();
                await repository.FinishAsync(run.Id, IndexRunStatus.Cancelled, "The queue was unavailable. Retry indexing.", CancellationToken.None);
                throw new PreparationException("The local indexing queue is unavailable.");
            }
            return run.Id;
        }
        finally { startGate.Release(); }
    }
    public bool Cancel(Guid documentId)
    {
        if (!operations.TryGetValue(documentId, out var operation)) return false;
        try { operation.Cancel(); return true; } catch (ObjectDisposedException) { return false; }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startGate.WaitAsync(stoppingToken);
        try
        {
            if (!recovered && (await repository.HealthAsync(stoppingToken)).Available)
            { await repository.RecoverAsync(stoppingToken); recovered = true; }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception) { /* Optional database must not prevent M1 startup. StartAsync retries recovery before accepting work. */ }
        finally { startGate.Release(); }
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var operation = operations[job.Request.SourceDocumentId];
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, operation.Token);
            var ct = linked.Token;
            try
            {
                var chunks = job.Preview.Chunks;
                var batchSize = job.Settings.Embedding!.BatchSize;
                for (var offset = 0; offset < chunks.Count; offset += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = chunks.Skip(offset).Take(batchSize).ToList();
                    var vectors = await embeddings.EmbedAsync(job.Settings, job.Space, batch.Select(x => x.EmbeddingText).ToList(), false, ct);
                    await repository.StageAsync(job.Run.Id, job.Space, batch, vectors.Vectors, chunks.Count, ct);
                }
                if ((await sources.LoadAsync(job.Request.SourceDocumentId, ct)).ArtifactHash != job.Run.ArtifactHash)
                    throw new PreparationException("Approved source changed during indexing. Activation was refused.");
                var current = await settingsStore.LoadAsync(ct);
                if ((current.Chunking ?? new()).Fingerprint != (job.Settings.Chunking ?? new()).Fingerprint ||
                    LmStudioEmbeddingProvider.ConfigurationFingerprint(current) != job.Space.ConfigurationFingerprint)
                    throw new PreparationException("Indexing settings changed during the run. Retry with the current settings.");
                if ((await embeddings.ValidateAsync(job.Settings, ct)).Fingerprint != job.Space.Fingerprint)
                    throw new PreparationException("Embedding identity changed during indexing. Revalidate and retry.");
                ct.ThrowIfCancellationRequested();
                await repository.CompleteAsync(job.Run.Id, job.Request.ActivateRevision, ct);
            }
            catch (OperationCanceledException) { await AbortAsync(job.Run.Id, IndexRunStatus.Cancelled, "Indexing cancelled. The previous active generation is preserved."); }
            catch (PreparationException ex) { await AbortAsync(job.Run.Id, IndexRunStatus.Failed, ex.Message); }
            catch (Exception) { await AbortAsync(job.Run.Id, IndexRunStatus.Failed, "Indexing failed. Check the database connection, disk capacity and model availability, then retry. Partial content was not activated."); }
            finally { operations.TryRemove(job.Request.SourceDocumentId, out _); operation.Dispose(); }
        }
    }
    private async Task AbortAsync(Guid runId, IndexRunStatus status, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await repository.FinishAsync(runId, status, message, timeout.Token); }
        catch (Exception) { /* Persisted unfinished state is recovered before the next accepted run after restart. */ }
    }
}
