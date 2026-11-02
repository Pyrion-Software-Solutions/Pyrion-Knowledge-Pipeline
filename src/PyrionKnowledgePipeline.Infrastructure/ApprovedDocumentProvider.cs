using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class ApprovedDocumentProvider(IDocumentStore store, IDocumentContractValidator validator) : IApprovedDocumentProvider
{
    private static readonly Lazy<Task<JsonSchema>> schema = new(() => JsonSchema.FromJsonAsync(DocumentContractValidator.NormalizedSchemaJson));
    public async Task<ApprovedSource> LoadAsync(Guid id, CancellationToken ct)
    {
        var review = await store.GetAsync(id, ct);
        if (review is not { Status: ReviewStatus.Approved, Approved: not null, Draft: not null })
            throw new PreparationException("Only a committed, explicitly approved document can be indexed.");
        var bytes = await store.ReadApprovedJsonAsync(id, ct)
            ?? throw new PreparationException("The approved artifact is missing. Restore the local source from backup.");
        if (bytes.Length > 100 * 1024 * 1024) throw new PreparationException("The approved artifact exceeds the indexing safety limit.");
        try
        {
            var json = new UTF8Encoding(false, true).GetString(bytes);
            using var parsed = JsonDocument.Parse(json);
            DocumentContractValidator.RejectDuplicates(parsed.RootElement);
            if ((await schema.Value).Validate(json).Count != 0)
                throw new PreparationException("The approved artifact does not match the supported v1.0/v1.1 contract.");
            var normalized = JsonSerializer.Deserialize<NormalizedDocument>(json, DocumentJson.Options)!;
            await validator.ValidateNormalizedAsync(normalized, ct);
            var expected = PreparationWorkflow.Preview(review, normalized.Approval) with { SchemaVersion = normalized.SchemaVersion,
                ExtractionWarnings = normalized.SchemaVersion == "1.0" ? normalized.ExtractionWarnings : review.Extraction.Warnings };
            if (normalized.DocumentId != id || normalized.Approval.DraftId != review.Draft.Id ||
                !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(review.Approved, DocumentJson.Options), JsonSerializer.SerializeToNode(normalized, DocumentJson.Options)) ||
                !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected, DocumentJson.Options), JsonSerializer.SerializeToNode(normalized, DocumentJson.Options)))
                throw new PreparationException("The approved artifact differs from its committed review or extracted source. Restore a consistent backup; indexing did not change the source.");
            return new(normalized, KnowledgeHash.Of(bytes), json);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new PreparationException("The approved artifact is not strict UTF-8 JSON. Restore the local source from backup."); }
    }
}
