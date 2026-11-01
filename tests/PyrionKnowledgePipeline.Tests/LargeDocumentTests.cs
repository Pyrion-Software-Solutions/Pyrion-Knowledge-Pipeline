using System.Text.Json;
using System.Text.Json.Nodes;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class LargeDocumentTests
{
    private static ExtractionResult Source(int count) => new(Enumerable.Range(0, count).Select(i =>
        new DocumentBlock($"word/document.xml#body/{i}", BlockType.Paragraph, null, $"Original paragraph {i}.  Preserve all wording.\t")).ToList(), ["Review omitted images in the original."]);

    private static async Task<(JsonElement Payload, JsonElement Schema)> ReadRequest(HttpRequestMessage request, CancellationToken ct)
    {
        using var root = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        using var payload = JsonDocument.Parse(root.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return (payload.RootElement.Clone(), root.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").Clone());
    }

    private static object Reply(JsonElement payload, JsonElement schema, object[]? issues = null) => new
    {
        batchId = schema.GetProperty("properties").GetProperty("batchId").GetProperty("enum")[0].GetString(),
        reviewedBlockCount = payload.GetProperty("sourceBlocks").GetArrayLength(),
        issues = issues ?? []
    };

    [Fact]
    public async Task Analyzes_all_249_blocks_in_order_and_merges_only_validated_changes()
    {
        var source = Source(249);
        var seen = new List<string>();
        var messages = new List<string>();
        var posts = 0;
        using var handler = new StubHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Fixture.Response(new { data = new[] { new { id = "model" } } });
            posts++;
            var (payload, schema) = await ReadRequest(request, ct);
            var blocks = payload.GetProperty("sourceBlocks");
            Assert.InRange(blocks.GetArrayLength(), 1, 32);
            foreach (var block in blocks.EnumerateArray()) seen.Add(block.GetProperty("text").GetString()!);
            var issues = posts == 1 ? new object[] { new { sourceReference = blocks[0].GetProperty("sourceReference").GetString(),
                suggestedType = "heading", suggestedHeadingLevel = 1, reason = "A section title is missing its heading style." } } : [];
            return Fixture.Completion(JsonSerializer.Serialize(Reply(payload, schema, issues)));
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        var result = await client.AnalyzeAsync(new(ModelId: "model"), source, "Preserve wording", messages.Add, default);
        Assert.Equal(8, posts);
        Assert.Equal(source.Blocks.Select(x => x.Text), seen);
        Assert.Equal(source.Blocks.Select(x => x.SourceReference), result.Blocks.Select(x => x.SourceReference));
        Assert.Equal(249, result.Blocks.Count);
        Assert.Single(result.Issues);
        Assert.Equal(BlockType.Heading, result.Blocks[0].Type);
        Assert.All(result.Blocks.Skip(1), x => Assert.Equal(BlockType.Paragraph, x.Type));
        Assert.Contains(messages, x => x.Contains("249 of 249"));
        ProposalIntegrity.Validate(result, source);
    }

    [Fact]
    public async Task Large_tables_send_every_cell_value_without_repeating_cell_reference_metadata()
    {
        var source = Source(40);
        var cells = Enumerable.Range(0, 200).Select(i => new TableCell($"Value {i}", $"word/document.xml#body/0/row/0/cell/{i}")).ToList();
        source.Blocks[0] = new DocumentBlock(source.Blocks[0].SourceReference, BlockType.Table, null, "", [new TableRow(cells)]);
        var sawTable = false;
        using var handler = new StubHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Fixture.Response(new { data = new[] { new { id = "model" } } });
            var (payload, schema) = await ReadRequest(request, ct);
            foreach (var block in payload.GetProperty("sourceBlocks").EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "table") continue;
                sawTable = true;
                Assert.Equal(cells.Select(x => x.Text), block.GetProperty("rows")[0].EnumerateArray().Select(x => x.GetString()));
            }
            return Fixture.Completion(JsonSerializer.Serialize(Reply(payload, schema)));
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        var result = await client.AnalyzeAsync(new(ModelId: "model"), source, "", null, default);
        Assert.True(sawTable);
        Assert.Equal(40, result.Blocks.Count);
        Assert.Equal(BlockType.Table, result.Blocks[0].Type);
        Assert.Equal(200, source.Blocks[0].Rows![0].Cells.Count);
    }

    [Fact]
    public async Task Oversized_single_block_is_rejected_without_truncation_or_model_submission()
    {
        var posts = 0;
        var source = Source(1);
        source.Blocks[0] = source.Blocks[0] with { Text = new string('x', 20_000) };
        using var handler = new StubHandler((request, ct) =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Task.FromResult(Fixture.Response(new { data = new[] { new { id = "model" } } }));
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        var error = await Assert.ThrowsAsync<PreparationException>(() => client.AnalyzeAsync(new(ModelId: "model"), source, "", null, default));
        Assert.Contains("No source text was truncated", error.Message);
        Assert.Equal(0, posts);
        Assert.Equal(20_000, source.Blocks[0].Text.Length);
    }

    [Theory]
    [InlineData("coverage")]
    [InlineData("unknown-reference")]
    [InlineData("duplicates")]
    [InlineData("replacement-text")]
    public async Task Invalid_batch_results_fail_after_one_visible_retry(string failure)
    {
        var posts = 0;
        var source = Source(40);
        var messages = new List<string>();
        using var handler = new StubHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Fixture.Response(new { data = new[] { new { id = "model" } } });
            posts++;
            var (payload, schema) = await ReadRequest(request, ct);
            var reply = JsonSerializer.SerializeToNode(Reply(payload, schema))!;
            var issue = new JsonObject { ["sourceReference"] = payload.GetProperty("sourceBlocks")[0].GetProperty("sourceReference").GetString(),
                ["suggestedType"] = "heading", ["suggestedHeadingLevel"] = 1, ["reason"] = "Review heading style." };
            switch (failure)
            {
                case "coverage": reply["reviewedBlockCount"] = 1; break;
                case "unknown-reference": issue["sourceReference"] = "unknown"; reply["issues"] = new JsonArray(issue); break;
                case "duplicates": reply["issues"] = new JsonArray(issue, issue.DeepClone()); break;
                case "replacement-text": issue["text"] = "Changed technical fact"; reply["issues"] = new JsonArray(issue); break;
            }
            return Fixture.Completion(reply.ToJsonString());
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        var error = await Assert.ThrowsAsync<PreparationException>(() => client.AnalyzeAsync(new(ModelId: "model"), source, "", messages.Add, default));
        Assert.Contains("No partial draft was saved", error.Message);
        Assert.Equal(2, posts);
        Assert.Contains(messages, x => x.Contains("retrying this part once"));
    }

    [Fact]
    public async Task Failure_after_a_completed_part_does_not_save_a_partial_draft()
    {
        using var workspace = new TestWorkspace();
        await workspace.Settings.SaveAsync(new(ModelId: "model"), default);
        var document = new PreparedDocument { Id = Guid.NewGuid(), Metadata = Fixture.Metadata, OriginalFileName = "large.docx", Extraction = Source(80) };
        await workspace.Store.CreateAsync(document, new MemoryStream(Fixture.Docx), default);
        var posts = 0;
        using var handler = new StubHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Fixture.Response(new { data = new[] { new { id = "model" } } });
            posts++;
            var (payload, schema) = await ReadRequest(request, ct);
            return Fixture.Completion(posts == 1 ? JsonSerializer.Serialize(Reply(payload, schema)) : "invalid json");
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        var error = await Assert.ThrowsAsync<PreparationException>(() => workspace.Workflow(client).AnalyzeAsync(document.Id, "", null, default));
        Assert.Contains("part 2 of 3", error.Message);
        var saved = await workspace.Store.GetAsync(document.Id, default);
        Assert.Equal(ReviewStatus.Extracted, saved!.Status);
        Assert.Null(saved.Draft);
        Assert.Null(saved.Approved);
    }

    [Fact]
    public async Task Cancellation_between_parts_stops_remaining_requests()
    {
        var source = Source(80);
        var posts = 0;
        using var cts = new CancellationTokenSource();
        using var handler = new StubHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Fixture.Response(new { data = new[] { new { id = "model" } } });
            posts++;
            var (payload, schema) = await ReadRequest(request, ct);
            return Fixture.Completion(JsonSerializer.Serialize(Reply(payload, schema)));
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AnalyzeAsync(new(ModelId: "model"), source, "",
            message => { if (message.StartsWith("Completed part 1")) cts.Cancel(); }, cts.Token));
        Assert.Equal(1, posts);
    }
}
