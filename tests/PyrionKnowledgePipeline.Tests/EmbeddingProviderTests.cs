using System.Net;
using System.Text.Json;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class EmbeddingProviderTests
{
    internal static LmStudioSettings Settings => new(Embedding: new("embedding-model", "synthetic-sha256-v1", BatchSize: 8));
    internal static HttpResponseMessage Catalog(string type = "embedding") => Fixture.Response(new { models = new[] { new { key = "embedding-model", type, max_context_length = 2048 } } });
    internal static HttpResponseMessage Vectors(int count = 1, string? model = "embedding-model", int dimensions = 3) => Fixture.Response(new
    { model, data = Enumerable.Range(0, count).Select(i => new { index = i, embedding = Enumerable.Range(1, dimensions).Select(x => (float)x).ToArray() }).ToArray() });
    [Fact]
    public async Task Validates_capabilities_bilingual_probe_dimensions_and_separate_query_prefix()
    {
        var inputs = new List<string>();
        var provider = new LmStudioEmbeddingProvider(new StubFactory(new StubHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Catalog();
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            inputs.Add(payload.RootElement.GetProperty("input")[0].GetString()!);
            return Vectors();
        })));
        var settings = Settings with { Embedding = Settings.Embedding! with { QueryPrefix = "query: ", DocumentPrefix = "document: " } };
        var space = await provider.ValidateAsync(settings, default);
        Assert.Equal(3, space.Dimensions); Assert.Contains("فشار", inputs[0]);
        await provider.EmbedAsync(settings, space, ["pump"], false, default);
        await provider.EmbedAsync(settings, space, ["پمپ"], true, default);
        Assert.Equal("document: pump", inputs[1]); Assert.Equal("query: پمپ", inputs[2]);
        Assert.NotEqual(space.ConfigurationFingerprint, LmStudioEmbeddingProvider.ConfigurationFingerprint(settings with { Embedding = settings.Embedding! with { ModelVersion = "replaced" } }));
    }
    [Theory]
    [InlineData("count")] [InlineData("model")] [InlineData("empty")] [InlineData("zero")]
    [InlineData("nonfinite")] [InlineData("indices")] [InlineData("chat")]
    public async Task Rejects_invalid_model_outputs_without_accepting_vectors(string scenario)
    {
        var provider = new LmStudioEmbeddingProvider(new StubFactory(new StubHandler((request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Catalog(scenario == "chat" ? "llm" : "embedding"));
            return Task.FromResult(scenario switch
            {
                "count" => Vectors(2), "model" => Vectors(model: null), "empty" => Vectors(dimensions: 0),
                "zero" => Fixture.Response(new { model = "embedding-model", data = new[] { new { index = 0, embedding = new[] { 0f, 0f } } } }),
                "nonfinite" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"model\":\"embedding-model\",\"data\":[{\"index\":0,\"embedding\":[1e100]}]}") },
                "indices" => Fixture.Response(new { model = "embedding-model", data = new[] { new { index = 4, embedding = new[] { 1f } } } }),
                _ => Vectors()
            });
        })));
        await Assert.ThrowsAsync<PreparationException>(() => provider.ValidateAsync(Settings, default));
    }
    [Fact]
    public async Task Rejects_dimension_drift_and_uses_bounded_transient_retries()
    {
        var requests = 0;
        var provider = new LmStudioEmbeddingProvider(new StubFactory(new StubHandler((request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Catalog());
            requests++;
            return Task.FromResult(requests < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Vectors(dimensions: requests == 3 ? 3 : 4));
        })));
        var space = await provider.ValidateAsync(Settings, default);
        Assert.Equal(3, requests);
        await Assert.ThrowsAsync<PreparationException>(() => provider.EmbedAsync(Settings, space, ["word"], false, default));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Distinguishes_timeout_from_explicit_cancellation(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource(); if (cancelled) cancellation.Cancel();
        var provider = new LmStudioEmbeddingProvider(new StubFactory(new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(new OperationCanceledException()))));
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ValidateAsync(Settings, cancellation.Token));
        else Assert.Contains("timed out", (await Assert.ThrowsAsync<PreparationException>(() => provider.ValidateAsync(Settings, default))).Message);
    }
}
