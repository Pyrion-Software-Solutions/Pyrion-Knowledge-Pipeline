using System.Diagnostics;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Application;

public sealed class KnowledgeRetrievalService(IKnowledgeRepository repository, ISettingsStore settingsStore,
    IEmbeddingProvider embeddings, IApprovedDocumentProvider sources) : IKnowledgeRetrievalService
{
    public async Task<RetrievalResponse> SearchAsync(RetrievalRequest request, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        request = request with { Query = request.Query.Trim(), ProjectId = Empty(request.ProjectId), DocumentNumber = Empty(request.DocumentNumber),
            Revision = Empty(request.Revision), Language = Empty(request.Language) };
        if (request.Query.Length is < 1 or > 2000) throw new PreparationException("Enter a query of 1–2,000 characters.");
        if (request.TopK is < 1 or > 50) throw new PreparationException("Choose Top-K between 1 and 50.");
        if (!Enum.IsDefined(request.Mode)) throw new PreparationException("Select semantic or literal retrieval.");
        if (request.MinimumSimilarity is { } min && (!double.IsFinite(min) || min is < -1 or > 1))
            throw new PreparationException("Choose an optional cosine similarity threshold from −1 to 1.");
        if (request.Language is not (null or "en" or "fa" or "mixed")) throw new PreparationException("Select Persian, English, mixed or all languages.");
        if (new[] { request.ProjectId, request.DocumentNumber, request.Revision }.Any(x => x?.Length > 200))
            throw new PreparationException("Keep each metadata filter within 200 characters.");
        var health = await repository.HealthAsync(ct);
        if (!health.Available) throw new PreparationException(health.Message);
        EmbeddingSpace? space = null;
        float[]? vector = null;
        if (request.Mode == RetrievalMode.Semantic)
        {
            var settings = await settingsStore.LoadAsync(ct);
            space = await embeddings.ValidateAsync(settings, ct);
            vector = (await embeddings.EmbedAsync(settings, space, [request.Query], true, ct)).Vectors[0];
        }
        var results = await repository.SearchAsync(request, space, vector, ct);
        foreach (var group in results.GroupBy(x => x.SourceDocumentId))
        {
            if ((await sources.LoadAsync(group.Key, ct)).ArtifactHash != group.First().ArtifactHash)
                throw new PreparationException("An indexed source differs from its approved local artifact. Restore the source or rebuild from a valid approval before using these results.");
        }
        var explanation = results.Count == 0
            ? "No matching active sources. Check filters, index and explicitly activate an approved revision, or reindex with the current embedding model. A cosine threshold is a cutoff, not evidence that an unsupported question has an answer."
            : $"{results.Count} source chunks retrieved. Scores describe retrieval ranking, not factual confidence.";
        if (results.Count == 0 && space is not null)
        {
            var active = (await repository.ListRunsAsync(ct)).Where(x => x.Active &&
                (request.DocumentId is null || request.DocumentId == x.SourceDocumentId)).ToList();
            if (active.Count > 0 && active.All(x => x.SpaceFingerprint != space.Fingerprint))
                explanation = "Embedding model/configuration mismatch: the active generations use a different embedding space. Reindex the approved sources with the selected model, or use literal search.";
        }
        return new(results, explanation, timer.Elapsed.TotalMilliseconds);
    }
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
