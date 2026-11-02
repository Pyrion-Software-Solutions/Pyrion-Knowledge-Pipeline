using System.Text.Json;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class KnowledgeChunkingTests
{
    internal static ApprovedSource Source(List<DocumentBlock>? blocks = null, string project = "Synthetic-A", string revision = "B0")
    {
        var doc = new NormalizedDocument("1.1", Guid.Parse("10000000-0000-0000-0000-000000000001"), project, "SYN-PUMP-001",
            "Synthetic bilingual pump requirements", revision, "mixed", blocks ?? [
                new("body/0", BlockType.Heading, 1, "Pump acceptance / پذیرش پمپ"),
                new("body/1", BlockType.Paragraph, null, "The hydrostatic test pressure is 12 bar. فشار آزمون هیدرواستاتیک ۱۲ بار است."),
                new("body/2", BlockType.List, null, "Inspect tag P-101 before startup. برچسب P-101 بررسی شود."),
                new("body/3", BlockType.Table, null, "", [new([new("Property", "body/3/row/0/cell/0"), new("Value", "body/3/row/0/cell/1")]),
                    new([new("Design temperature / دمای طراحی", "body/3/row/1/cell/0"), new("85 °C", "body/3/row/1/cell/1")])])],
            new(ReviewStatus.Approved, "Synthetic test reviewer", DateTimeOffset.Parse("2026-11-03T00:00:00Z"),
                Guid.Parse("20000000-0000-0000-0000-000000000001"), "fixture-only"), []);
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options);
        return new(doc, KnowledgeHash.Of(json), json);
    }
    [Fact]
    public void Deterministic_chunks_account_for_every_character_and_cell_in_source_order()
    {
        var source = Source(); var chunker = new StructureAwareChunker();
        var result = chunker.Build(source, new(), default);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(chunker.Build(source, new(), default)));
        Assert.Equal(Enumerable.Range(0, result.Chunks.Count), result.Chunks.Select(x => x.Ordinal));
        Assert.All(result.Chunks.Skip(1), x => Assert.Contains("Pump acceptance / پذیرش پمپ", x.HeadingPath));
        Assert.Contains(result.Chunks, x => x.DisplayText.Contains("85 °C"));
        Assert.All(source.Document.Blocks, block =>
        {
            var spans = result.Chunks.SelectMany(x => x.Sources).Where(x => x.BlockReference == block.SourceReference && !x.ContextOnly).ToList();
            if (block.Rows is null) Assert.Equal(block.Text, string.Concat(spans.Select(x => block.Text.Substring(x.Start, x.Length))));
            else Assert.Equal(block.Rows.SelectMany(x => x.Cells).Select(x => x.SourceReference), spans.Select(x => x.SourceReference));
        });
    }
    [Fact]
    public void Oversized_sentence_blocks_split_with_exact_unicode_offsets_and_no_truncation()
    {
        var text = string.Concat(Enumerable.Repeat("فشار ۱۲ بار است. Pump pressure is 12 bar. 🛠\n", 70));
        var source = Source([new("body/0", BlockType.Paragraph, null, text)]);
        var result = new StructureAwareChunker().Build(source, new(100, 130, 0), default);
        Assert.True(result.Chunks.Count > 2);
        Assert.Equal(text, string.Concat(result.Chunks.Select(x => x.DisplayText)));
        Assert.All(result.Chunks, x => Assert.True(x.EstimatedTokens <= 130));
        var bad = Source([new("body/0", BlockType.Paragraph, null, new string('x', 1000))]);
        Assert.Contains("no safe", Assert.Throws<PreparationException>(() => new StructureAwareChunker().Build(bad, new(100, 130, 0), default)).Message);
    }
    [Fact]
    public void Tables_split_only_complete_rows_and_repeat_explicit_headers_as_context()
    {
        var rows = Enumerable.Range(0, 20).Select(r => new TableRow([new($"row {r}: {new string('a', 60)}", $"table/{r}/0"), new($"مقدار {r}", $"table/{r}/1")])).ToList();
        var source = Source([new("table", BlockType.Table, null, "", rows)]);
        var config = new ChunkingSettings(100, 180, 0, new() { [$"{source.Document.DocumentId:N}:table"] = 1 });
        var chunks = new StructureAwareChunker().Build(source, config, default).Chunks;
        Assert.True(chunks.Count > 1);
        Assert.Equal(rows.SelectMany(x => x.Cells).Select(x => x.SourceReference), chunks.SelectMany(x => x.Sources).Where(x => !x.ContextOnly).Select(x => x.SourceReference));
        Assert.All(chunks.Skip(1), x => { Assert.StartsWith(rows[0].Cells[0].Text, x.EmbeddingText); Assert.Contains(x.Sources, s => s.Row == 0 && s.ContextOnly); });
        var giant = Source([new("table", BlockType.Table, null, "", [new([new(new string('x', 1000), "cell")])])]);
        Assert.Contains("cannot fit intact", Assert.Throws<PreparationException>(() => new StructureAwareChunker().Build(giant, new(100, 130, 0), default)).Message);
    }
    [Fact]
    public void Sections_do_not_blend_and_configuration_changes_chunk_identity()
    {
        var source = Source([new("h1", BlockType.Heading, 1, "A"), new("p1", BlockType.Paragraph, null, "First."),
            new("h2", BlockType.Heading, 1, "B"), new("p2", BlockType.Paragraph, null, "Second."), new("empty", BlockType.Paragraph, null, "")]);
        var chunker = new StructureAwareChunker(); var result = chunker.Build(source, new(), default);
        Assert.DoesNotContain(result.Chunks, c => c.DisplayText.Contains("First.") && c.DisplayText.Contains("Second."));
        Assert.Contains(result.CoverageNotes, x => x.Contains("empty"));
        Assert.NotEqual(result.Chunks[0].Id, chunker.Build(source, new(300, 500, 0), default).Chunks[0].Id);
    }
    [Theory]
    [InlineData(300)] [InlineData(500)] [InlineData(1000)]
    public async Task Candidate_sizes_preserve_the_existing_nonconfidential_DOCX_fixture(int target)
    {
        using var w = new TestWorkspace(); var workflow = w.Workflow();
        await w.Settings.SaveAsync(new(ModelId: "test-model"), default);
        var doc = await workflow.UploadAsync("synthetic.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        doc = await workflow.AnalyzeAsync(doc.Id, "", null, default);
        await workflow.ApproveAsync(doc.Id, doc.Draft!.Id, "Synthetic test reviewer", default);
        var source = await new ApprovedDocumentProvider(w.Store, new DocumentContractValidator()).LoadAsync(doc.Id, default);
        var preview = new StructureAwareChunker().Build(source, new(target, target + 250, 40), default);
        Assert.NotEmpty(preview.Chunks);
        Assert.All(preview.Chunks, x => Assert.True(x.EstimatedTokens <= target + 250));
    }
}
