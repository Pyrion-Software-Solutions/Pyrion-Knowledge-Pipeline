using System.Text.Json;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class ContractTests
{
    [Fact]
    public async Task Accepts_valid_complete_proposal()
    {
        var source = await Fixture.ExtractAsync();
        var json = JsonSerializer.Serialize(Fixture.Proposal(source), DocumentJson.Options);
        var result = await new DocumentContractValidator().ParseProposalAsync(json, source, default);
        Assert.Equal(7, result.Blocks.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("```json\n{}\n```")]
    [InlineData("{}")]
    [InlineData("{\"issues\":[],\"blocks\":[]}")]
    [InlineData("{\"issues\":[],\"issues\":[],\"blocks\":[]}")]
    public async Task Rejects_empty_invalid_incomplete_or_ambiguous_JSON(string json)
    {
        var source = await Fixture.ExtractAsync();
        await Assert.ThrowsAsync<PreparationException>(() => new DocumentContractValidator().ParseProposalAsync(json, source, default));
    }

    [Theory]
    [InlineData("reordered")]
    [InlineData("unknown")]
    [InlineData("table-conversion")]
    [InlineData("level")]
    [InlineData("missing-reason")]
    public async Task Rejects_semantically_invalid_proposals(string scenario)
    {
        var source = await Fixture.ExtractAsync();
        var p = Fixture.Proposal(source);
        switch (scenario)
        {
            case "reordered": (p.Blocks[0], p.Blocks[1]) = (p.Blocks[1], p.Blocks[0]); break;
            case "unknown": p.Blocks[0] = p.Blocks[0] with { SourceReference = "unknown" }; break;
            case "table-conversion": p.Blocks[4] = p.Blocks[4] with { Type = BlockType.Paragraph }; break;
            case "level": p.Blocks[1] = p.Blocks[1] with { HeadingLevel = 9 }; break;
            case "missing-reason": p.Issues.Clear(); break;
        }
        await Assert.ThrowsAsync<PreparationException>(() => new DocumentContractValidator().ParseProposalAsync(JsonSerializer.Serialize(p, DocumentJson.Options), source, default));
    }

    [Fact]
    public async Task Rejects_additional_properties_that_could_rewrite_source_text()
    {
        var source = await Fixture.ExtractAsync();
        var json = JsonSerializer.Serialize(Fixture.Proposal(source), DocumentJson.Options).Replace("\"type\": \"heading\"", "\"text\": \"changed fact\", \"type\": \"heading\"");
        await Assert.ThrowsAsync<PreparationException>(() => new DocumentContractValidator().ParseProposalAsync(json, source, default));
    }

    [Fact]
    public async Task Normalized_contract_rejects_missing_metadata_or_approval()
    {
        using var workspace = new TestWorkspace();
        await workspace.Settings.SaveAsync(new(ModelId: "test-model"), default);
        var workflow = workspace.Workflow();
        var doc = await workflow.UploadAsync("test.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        doc = await workflow.AnalyzeAsync(doc.Id, "", null, default);
        var pending = PreparationWorkflow.Preview(doc);
        var validator = new DocumentContractValidator();
        await validator.ValidateDraftAsync(pending, default);
        Assert.Equal("1.1", pending.SchemaVersion);
        Assert.NotNull(pending.ExtractionWarnings);
        await Assert.ThrowsAsync<PreparationException>(() => validator.ValidateNormalizedAsync(pending, default));
        doc = await workflow.ApproveAsync(doc.Id, doc.Draft!.Id, "Reviewer", default);
        await validator.ValidateNormalizedAsync(doc.Approved!, default);
        await Assert.ThrowsAsync<PreparationException>(() => validator.ValidateDraftAsync(doc.Approved!, default));
        await Assert.ThrowsAsync<PreparationException>(() => validator.ValidateNormalizedAsync(doc.Approved! with { ProjectId = "" }, default));
    }
}
