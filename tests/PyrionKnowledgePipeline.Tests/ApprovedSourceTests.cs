using System.Text.Json;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class ApprovedSourceTests
{
    [Theory]
    [InlineData("1.0")] [InlineData("1.1")]
    public async Task Validates_supported_approvals_and_hashes_exact_artifact_bytes(string version)
    {
        using var w = new TestWorkspace(); var doc = await ApproveAsync(w);
        var folder = Path.Combine(w.Root, "documents", doc.Id.ToString("N"));
        if (version == "1.0")
        {
            doc.Approved = doc.Approved! with { SchemaVersion = "1.0", ExtractionWarnings = null };
            await File.WriteAllTextAsync(Path.Combine(folder, "review.json"), JsonSerializer.Serialize(doc, DocumentJson.Options));
            await File.WriteAllTextAsync(Path.Combine(folder, "approved.json"), JsonSerializer.Serialize(doc.Approved, DocumentJson.Options));
        }
        var provider = new ApprovedDocumentProvider(w.Store, new DocumentContractValidator());
        var source = await provider.LoadAsync(doc.Id, default);
        Assert.Equal(version, source.Document.SchemaVersion);
        Assert.Equal(KnowledgeHash.Of(await File.ReadAllBytesAsync(Path.Combine(folder, "approved.json"))), source.ArtifactHash);
    }
    [Theory]
    [InlineData("draft")] [InlineData("rejected")] [InlineData("tampered")] [InlineData("duplicate")] [InlineData("missing-property")]
    public async Task Rejects_unapproved_or_inconsistent_artifacts(string scenario)
    {
        using var w = new TestWorkspace(); var doc = await ApproveAsync(w);
        var folder = Path.Combine(w.Root, "documents", doc.Id.ToString("N"));
        var artifact = Path.Combine(folder, "approved.json");
        var json = await File.ReadAllTextAsync(artifact);
        switch (scenario)
        {
            case "draft": case "rejected":
                doc.Status = scenario == "draft" ? ReviewStatus.AwaitingReview : ReviewStatus.Rejected;
                await File.WriteAllTextAsync(Path.Combine(folder, "review.json"), JsonSerializer.Serialize(doc, DocumentJson.Options)); break;
            case "tampered": await File.WriteAllTextAsync(artifact, json.Replace("Pump inspection procedure", "Changed technical title")); break;
            case "duplicate": await File.WriteAllTextAsync(artifact, json.Replace("\"schemaVersion\":", "\"schemaVersion\": \"1.1\", \"schemaVersion\":")); break;
            case "missing-property": await File.WriteAllTextAsync(artifact, json.Replace("\"formatting\": null,", ""));
                // Remove an always-required field independent of sample formatting.
                var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(artifact))!;
                node["blocks"]![0]!.AsObject().Remove("rows"); await File.WriteAllTextAsync(artifact, node.ToJsonString()); break;
        }
        await Assert.ThrowsAsync<PreparationException>(() => new ApprovedDocumentProvider(w.Store, new DocumentContractValidator()).LoadAsync(doc.Id, default));
    }
    internal static async Task<PreparedDocument> ApproveAsync(TestWorkspace w)
    {
        await w.Settings.SaveAsync(new(ModelId: "test-model"), default);
        var workflow = w.Workflow(); var doc = await workflow.UploadAsync("fixture.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        doc = await workflow.AnalyzeAsync(doc.Id, "", null, default);
        return await workflow.ApproveAsync(doc.Id, doc.Draft!.Id, "Synthetic test reviewer", default);
    }
}
