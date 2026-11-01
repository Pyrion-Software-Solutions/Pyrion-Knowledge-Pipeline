using System.Reflection;
using System.Text.Json;
using NJsonSchema;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class DocumentContractValidator : IDocumentContractValidator
{
    public static string ProposalSchemaJson { get; } = ReadSchema("analysis-proposal.schema.json");
    public static string NormalizedSchemaJson { get; } = ReadSchema("normalized-document-v1.schema.json");
    public static string DraftSchemaJson { get; } = ReadSchema("normalized-draft-v1.schema.json");
    private static readonly Lazy<Task<JsonSchema>> proposalSchema = new(() => JsonSchema.FromJsonAsync(ProposalSchemaJson));
    private static readonly Lazy<Task<JsonSchema>> normalizedSchema = new(() => JsonSchema.FromJsonAsync(NormalizedSchemaJson));
    private static readonly Lazy<Task<JsonSchema>> draftSchema = new(() => JsonSchema.FromJsonAsync(DraftSchemaJson));

    public async Task<AnalysisProposal> ParseProposalAsync(string json, ExtractionResult source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json)) throw new PreparationException("The model returned an empty response. Retry or choose another model.");
        try
        {
            // System.Text.Json checks strict syntax before the schema validator; reject duplicate keys as well.
            using var parsed = JsonDocument.Parse(json);
            RejectDuplicates(parsed.RootElement);
            if ((await proposalSchema.Value).Validate(json).Count > 0)
                throw new PreparationException("The model response does not match the required JSON schema. Retry or choose a model with structured-output support.");
            var proposal = JsonSerializer.Deserialize<AnalysisProposal>(json, DocumentJson.Options)
                ?? throw new PreparationException("The model returned no proposal.");
            ProposalIntegrity.Validate(proposal, source);
            return proposal;
        }
        catch (JsonException) { throw new PreparationException("The model returned invalid JSON. No draft was saved. Retry or select another model."); }
    }

    public async Task ValidateNormalizedAsync(NormalizedDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((await normalizedSchema.Value).Validate(JsonSerializer.Serialize(document, DocumentJson.Options)).Count > 0)
            throw new PreparationException("The normalized document failed schema validation; approval was not saved.");
        ValidateContent(document);
    }

    public async Task ValidateDraftAsync(NormalizedDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((await draftSchema.Value).Validate(JsonSerializer.Serialize(document, DocumentJson.Options)).Count > 0)
            throw new PreparationException("The review draft failed schema validation and cannot be exported.");
        ValidateContent(document);
    }

    private static void ValidateContent(NormalizedDocument document)
    {
        if (document.SchemaVersion == "1.1" && document.ExtractionWarnings is null)
            throw new PreparationException("Version 1.1 exports must include extraction notes, even when there are none.");
        if (document.DocumentId == Guid.Empty || document.Approval.DraftId == Guid.Empty ||
            document.Blocks.Select(x => x.SourceReference).Distinct().Count() != document.Blocks.Count)
            throw new PreparationException("The normalized document contains invalid identifiers or duplicate source references.");
        foreach (var block in document.Blocks)
        {
            ProposalIntegrity.ValidateClassification(block.Type, block.HeadingLevel);
            if ((block.Type == BlockType.Table) != (block.Rows is not null) || (block.Type == BlockType.Table && block.Text != ""))
                throw new PreparationException("The normalized document contains an invalid table representation.");
        }
    }

    internal static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PreparationException("The model returned ambiguous JSON with duplicate keys. Retry analysis.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private static string ReadSchema(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PyrionKnowledgePipeline.Infrastructure.Schemas.{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
