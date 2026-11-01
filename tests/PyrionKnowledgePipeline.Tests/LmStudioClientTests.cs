using System.Net;
using System.Text.Json;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class LmStudioClientTests
{
    [Theory]
    [InlineData("https://example.com/v1")]
    [InlineData("http://localhost:1234/v1?redirect=external")]
    [InlineData("http://user:pass@localhost:1234/v1")]
    [InlineData("http://192.168.1.4:1234/v1")]
    public void Rejects_nonlocal_or_ambiguous_endpoints(string url) =>
        Assert.Throws<PreparationException>(() => LocalEndpoint.Validate(new(url)));

    [Fact]
    public async Task Uses_selected_model_and_schema_then_validates_response()
    {
        var source = await Fixture.ExtractAsync();
        var calls = 0;
        using var handler = new StubHandler(async (request, ct) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get) return Fixture.Response(new { data = new[] { new { id = "selected-model" } } });
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("selected-model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("json_schema", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Contains("UNTRUSTED", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Fixture.Completion(JsonSerializer.Serialize(Fixture.Proposal(source), DocumentJson.Options));
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        var result = await client.AnalyzeAsync(new(ModelId: "selected-model"), source, "Keep words", null, default);
        Assert.Equal(7, result.Blocks.Count); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unsupported_schema_mode_retries_once_with_visible_status_and_same_validation()
    {
        var source = await Fixture.ExtractAsync();
        var messages = new List<string>();
        var posts = 0;
        using var handler = new StubHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Fixture.Response(new { data = new[] { new { id = "model" } } });
            posts++;
            if (posts == 1) return Fixture.Response(new { error = "json_schema response_format unsupported" }, HttpStatusCode.BadRequest);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.False(json.RootElement.TryGetProperty("response_format", out _));
            return Fixture.Completion(JsonSerializer.Serialize(Fixture.Proposal(source), DocumentJson.Options));
        });
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        await client.AnalyzeAsync(new(ModelId: "model"), source, "", messages.Add, default);
        Assert.Equal(2, posts);
        Assert.Contains(messages, x => x.Contains("rejected"));
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("empty")]
    [InlineData("incomplete")]
    [InlineData("missing-choices")]
    [InlineData("invalid-root")]
    [InlineData("missing-model")]
    [InlineData("http-error")]
    public async Task Invalid_or_failed_completions_are_never_fabricated(string scenario)
    {
        var source = await Fixture.ExtractAsync();
        using var handler = new StubHandler((request, ct) => Task.FromResult(request.Method == HttpMethod.Get
            ? Fixture.Response(new { data = new[] { new { id = scenario == "missing-model" ? "different-model" : "model" } } })
            : scenario switch
            {
                "invalid-json" => Fixture.Completion("not JSON"),
                "empty" => Fixture.Completion(""),
                "incomplete" => Fixture.Completion("{}", "length"),
                "missing-choices" => Fixture.Response(new { choices = Array.Empty<object>() }),
                "invalid-root" => Fixture.Response(Array.Empty<object>()),
                "http-error" => Fixture.Response(new { error = "internal error" }, HttpStatusCode.InternalServerError),
                _ => Fixture.Completion("{}")
            }));
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        await Assert.ThrowsAsync<PreparationException>(() => client.AnalyzeAsync(new(ModelId: "model"), source, "", null, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offline_and_timeout_errors_are_meaningful(bool timeout)
    {
        using var handler = new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(timeout ? new TaskCanceledException() : new HttpRequestException()));
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        var error = await Assert.ThrowsAsync<PreparationException>(() => client.GetModelsAsync(new(), default));
        Assert.Contains(timeout ? "timed out" : "Cannot connect", error.Message);
    }

    [Fact]
    public async Task Explicit_cancellation_is_not_reported_as_timeout()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        using var handler = new StubHandler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        var client = new LmStudioClient(new StubFactory(handler), new DocumentContractValidator());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetModelsAsync(new(), cts.Token));
    }
}
