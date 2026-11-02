using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PyrionKnowledgePipeline.Domain;

public enum IndexRunStatus { Pending, Running, Completed, Failed, Cancelled }
public enum RetrievalMode { Semantic, Literal }

public sealed record ChunkingSettings(int TargetTokens = 500, int MaximumTokens = 750, int OverlapTokens = 40,
    Dictionary<string, int>? TableHeaderRows = null)
{
    public const string Version = "structure-v1";
    public const string TokenMethod = "estimated: Unicode scalars / 2 (not a model tokenizer)";
    public void Validate()
    {
        if (TargetTokens is < 100 or > 2000 || MaximumTokens < TargetTokens || MaximumTokens > 4000 ||
            OverlapTokens < 0 || OverlapTokens > TargetTokens / 4 ||
            TableHeaderRows?.Any(x => x.Value is < 0 or > 20) == true)
            throw new ArgumentException("Choose target 100–2,000, maximum between target and 4,000, and overlap at most one quarter of target. Header counts must be 0–20.");
    }
    public string Fingerprint => KnowledgeHash.Of(JsonSerializer.Serialize(new
    {
        Version, TargetTokens, MaximumTokens, OverlapTokens,
        Headers = TableHeaderRows?.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() ?? []
    }));
}

public sealed record EmbeddingSettings(string? ModelId = null, string ModelVersion = "", int BatchSize = 8,
    int TimeoutSeconds = 180, string DocumentPrefix = "", string QueryPrefix = "", int ContextTokens = 2048)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 500 || string.IsNullOrWhiteSpace(ModelVersion) ||
            ModelVersion.Length > 200 || BatchSize is < 1 or > 32 || TimeoutSeconds is < 5 or > 600 ||
            ContextTokens is < 128 or > 32768 || DocumentPrefix.Length > 1000 || QueryPrefix.Length > 1000)
            throw new ArgumentException("Select an embedding model and enter its local artifact/version identity; batch 1–32, timeout 5–600 seconds, context 128–32,768 tokens, prefixes at most 1,000 characters.");
    }
}

public sealed record ApprovedSource(NormalizedDocument Document, string ArtifactHash, string Json);
public sealed record SourceSpan(string BlockReference, string SourceReference, int Start, int Length,
    int? Row = null, int? Column = null, bool ContextOnly = false);
public sealed record KnowledgeChunk(string Id, int Ordinal, string DisplayText, string EmbeddingText,
    string ContentType, List<string> HeadingPath, List<SourceSpan> Sources, int EstimatedTokens, string ContentHash);
public sealed record ChunkPreview(List<KnowledgeChunk> Chunks, List<string> CoverageNotes, string ConfigurationFingerprint);
public sealed record EmbeddingModel(string Id, string Type, int? ContextTokens);
public sealed record EmbeddingSpace(string Fingerprint, string ModelId, string ModelVersion, int Dimensions,
    string ConfigurationFingerprint, string ProbeHash, string Metric = "cosine");
public sealed record EmbeddingBatch(List<float[]> Vectors, string ModelId);
public sealed record IndexRun(Guid Id, Guid SourceDocumentId, string ArtifactHash, string SettingsFingerprint,
    IndexRunStatus Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int CompletedChunks,
    int TotalChunks, string? Error, string? ModelId, int? Dimensions, bool Active,
    string? SpaceFingerprint = null, string? EmbeddingConfigurationFingerprint = null);
public sealed record IndexRequest(Guid SourceDocumentId, bool ActivateRevision = false, bool Force = false);
public sealed record RetrievalRequest(string Query, RetrievalMode Mode = RetrievalMode.Semantic,
    string? ProjectId = null, Guid? DocumentId = null, string? DocumentNumber = null, string? Revision = null,
    string? Language = null, int TopK = 5, double? MinimumSimilarity = null);
public sealed record RetrievalHit(Guid SourceDocumentId, string ProjectId, string DocumentNumber, string Title,
    string Revision, string Language, KnowledgeChunk Chunk, double Score, string Metric,
    string ModelId, int Dimensions, List<string> Warnings, string ArtifactHash = "");
public sealed record RetrievalResponse(List<RetrievalHit> Hits, string Explanation, double ElapsedMilliseconds);
public sealed record IndexHealth(bool Available, string Message);

public static class KnowledgeHash
{
    public static string Of(string value) => Of(Encoding.UTF8.GetBytes(value));
    public static string Of(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
