using System.Text.Json;
using System.Text.Json.Serialization;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Application;

public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed class PreparationException(string message) : Exception(message);
public sealed record LocalModel(string Id);
public sealed record LmStudioSettings(string BaseUrl = "http://localhost:1234/v1", string? ModelId = null, int TimeoutSeconds = 180,
    EmbeddingSettings? Embedding = null, ChunkingSettings? Chunking = null);

public interface IDocumentParser
{
    string Extension { get; }
    Task<ExtractionResult> ExtractAsync(Stream source, CancellationToken cancellationToken);
}

// Format boundaries only: this milestone registers no implementations of these interfaces.
public interface IXlsxDocumentParser : IDocumentParser;
public interface ITextPdfDocumentParser : IDocumentParser;

public interface IDocumentStore
{
    Task CreateAsync(PreparedDocument document, Stream original, CancellationToken cancellationToken);
    Task<PreparedDocument?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken);
    Task SaveAsync(PreparedDocument document, int expectedVersion, CancellationToken cancellationToken);
    Task<byte[]?> ReadApprovedJsonAsync(Guid id, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<LmStudioSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(LmStudioSettings settings, CancellationToken cancellationToken);
}

public interface ILmStudioClient
{
    Task<IReadOnlyList<LocalModel>> GetModelsAsync(LmStudioSettings settings, CancellationToken cancellationToken);
    Task<AnalysisProposal> AnalyzeAsync(LmStudioSettings settings, ExtractionResult extraction, string rules,
        Action<string>? reportStatus, CancellationToken cancellationToken);
}

public interface IDocumentContractValidator
{
    Task<AnalysisProposal> ParseProposalAsync(string json, ExtractionResult source, CancellationToken cancellationToken);
    Task ValidateNormalizedAsync(NormalizedDocument document, CancellationToken cancellationToken);
    Task ValidateDraftAsync(NormalizedDocument document, CancellationToken cancellationToken);
}

public static class ProposalIntegrity
{
    public static void Validate(AnalysisProposal proposal, ExtractionResult source)
    {
        if (proposal.Blocks is null || proposal.Issues is null || proposal.Blocks.Count != source.Blocks.Count)
            throw new PreparationException("The model returned an incomplete structure. Every source block must appear exactly once.");
        for (var i = 0; i < source.Blocks.Count; i++)
        {
            var original = source.Blocks[i];
            var proposed = proposal.Blocks[i];
            if (proposed is null || proposed.SourceReference != original.SourceReference)
                throw new PreparationException("The proposal contains missing, reordered or unknown source references.");
            ValidateClassification(proposed.Type, proposed.HeadingLevel);
            if ((original.Type == BlockType.Table) != (proposed.Type == BlockType.Table))
                throw new PreparationException("Tables cannot be converted to text blocks or text blocks to tables.");
            if ((original.Type != proposed.Type || original.HeadingLevel != proposed.HeadingLevel) &&
                !proposal.Issues.Any(x => x is not null && x.SourceReference == proposed.SourceReference &&
                    x.SuggestedType == proposed.Type && x.SuggestedHeadingLevel == proposed.HeadingLevel))
                throw new PreparationException("A proposed change is missing its explanation in the consolidated report.");
        }
        foreach (var issue in proposal.Issues)
        {
            if (issue is null || string.IsNullOrWhiteSpace(issue.Reason) || issue.Reason.Length > 2000)
                throw new PreparationException("Every issue needs a nonempty explanation of at most 2,000 characters.");
            var block = proposal.Blocks.Find(x => x.SourceReference == issue.SourceReference);
            if (block is null || block.Type != issue.SuggestedType || block.HeadingLevel != issue.SuggestedHeadingLevel)
                throw new PreparationException("An issue does not match the proposed structure or source location.");
        }
    }

    public static void ValidateClassification(BlockType type, int? headingLevel)
    {
        if (!Enum.IsDefined(type) || (type == BlockType.Heading ? headingLevel is not (>= 1 and <= 6) : headingLevel is not null))
            throw new PreparationException("Heading levels must be 1–6; other block types must have a null heading level.");
    }
}
