using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using NJsonSchema;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

internal sealed record AnalysisBatch(string Id, ExtractionResult Source, string Payload);
internal sealed record BatchResponse(string BatchId, int ReviewedBlockCount, List<ReviewIssue> Issues);

internal static class AnalysisBatches
{
    internal const int MaxBlocks = 32;
    internal const int MaxPayloadCharacters = 10_000;
    private static readonly JsonSerializerOptions compactJson = new(DocumentJson.Options)
    { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static List<AnalysisBatch> Plan(ExtractionResult source, string rules)
    {
        if (source.Blocks.Count is 0 or > 1000 || source.Blocks.Select(x => x.SourceReference).Distinct().Count() != source.Blocks.Count)
            throw new PreparationException("Analysis requires 1–1,000 blocks with unique source references.");
        var result = new List<AnalysisBatch>();
        var current = new List<DocumentBlock>();
        DocumentBlock? precedingHeading = null;
        DocumentBlock? batchHeading = null;
        foreach (var block in source.Blocks)
        {
            var candidate = current.Append(block).ToList();
            if (candidate.Count > MaxBlocks || Payload(candidate, rules, batchHeading).Length > MaxPayloadCharacters)
            {
                if (current.Count == 0) throw TooLarge();
                result.Add(new($"part-{result.Count + 1}", new(current, []), Payload(current, rules, batchHeading)));
                current = [];
                batchHeading = precedingHeading;
                if (Payload([block], rules, batchHeading).Length > MaxPayloadCharacters) throw TooLarge();
            }
            current.Add(block);
            if (block.Type == BlockType.Heading) precedingHeading = block;
        }
        if (current.Count > 0)
            result.Add(new($"part-{result.Count + 1}", new(current, []), Payload(current, rules, batchHeading)));
        return result;
    }

    private static PreparationException TooLarge() => new("A source block or the reference rules exceed the per-request analysis capacity. Shorten the rules or use a model workflow with a larger context. No source text was truncated.");

    internal static string FullReferencePayload(ExtractionResult source, string rules) => Payload(source.Blocks, rules, null, aliases: false);

    private static string Payload(List<DocumentBlock> blocks, string rules, DocumentBlock? precedingHeading, bool aliases = true) =>
        JsonSerializer.Serialize(new
        {
            referenceRules = rules,
            precedingHeading = precedingHeading is null ? null : new { precedingHeading.Text, precedingHeading.HeadingLevel },
            // Cell source references are retained in extraction/export. The model
            // classifies whole blocks only, so avoid repeating cell paths while
            // still sending every cell value in its original row/cell order.
            sourceBlocks = blocks.Select((block, index) => new
            {
                sourceReference = aliases ? $"b{index + 1}" : block.SourceReference,
                block.Type, block.HeadingLevel, block.Text, block.Formatting,
                rows = block.Rows?.Select(row => row.Cells.Select(cell => cell.Text).ToArray()).ToArray()
            })
        }, compactJson);

    internal static string Schema(AnalysisBatch batch)
    {
        var issueSchema = JsonNode.Parse(DocumentContractValidator.ProposalSchemaJson)!["properties"]!["issues"]!["items"]!.DeepClone();
        foreach (var variant in issueSchema["anyOf"]!.AsArray())
            variant!["properties"]!["sourceReference"]!["enum"] = new JsonArray(Enumerable.Range(1, batch.Source.Blocks.Count)
                .Select(x => (JsonNode?)JsonValue.Create($"b{x}")).ToArray());
        return new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JsonArray("batchId", "reviewedBlockCount", "issues"),
            ["properties"] = new JsonObject
            {
                ["batchId"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(batch.Id) },
                ["reviewedBlockCount"] = new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(batch.Source.Blocks.Count) },
                ["issues"] = new JsonObject { ["type"] = "array", ["maxItems"] = batch.Source.Blocks.Count, ["items"] = issueSchema }
            }
        }.ToJsonString();
    }

    internal static async Task<AnalysisProposal> ParseAsync(string json, string schemaJson, AnalysisBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var syntax = JsonDocument.Parse(json);
            DocumentContractValidator.RejectDuplicates(syntax.RootElement);
            if ((await JsonSchema.FromJsonAsync(schemaJson, cancellationToken: ct)).Validate(json).Count > 0)
                throw new PreparationException("The model result does not match this analysis part's schema or coverage count.");
            var response = JsonSerializer.Deserialize<BatchResponse>(json, DocumentJson.Options)!;
            if (response.BatchId != batch.Id || response.ReviewedBlockCount != batch.Source.Blocks.Count || response.Issues is null ||
                response.Issues.Select(x => x.SourceReference).Distinct().Count() != response.Issues.Count)
                throw new PreparationException("The model returned incorrect coverage or duplicate issue locations.");
            var mappedIssues = response.Issues.Select(issue => issue with
            {
                SourceReference = batch.Source.Blocks[int.Parse(issue.SourceReference[1..], CultureInfo.InvariantCulture) - 1].SourceReference
            }).ToList();
            var classifications = batch.Source.Blocks.Select(source =>
            {
                var issue = mappedIssues.Find(x => x.SourceReference == source.SourceReference);
                return issue is null ? new ProposedBlock(source.SourceReference, source.Type, source.HeadingLevel)
                    : new ProposedBlock(source.SourceReference, issue.SuggestedType, issue.SuggestedHeadingLevel);
            }).ToList();
            var proposal = new AnalysisProposal(mappedIssues, classifications);
            ProposalIntegrity.Validate(proposal, batch.Source);
            return proposal;
        }
        catch (JsonException) { throw new PreparationException("The model returned invalid or empty JSON for this analysis part."); }
    }
}
