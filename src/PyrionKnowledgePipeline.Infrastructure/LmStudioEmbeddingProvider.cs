using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class LmStudioEmbeddingProvider(IHttpClientFactory factory) : IEmbeddingProvider
{
    private const string Probe = "Pyrion local embedding identity probe. فشار طراحی پمپ";
    public static string ConfigurationFingerprint(LmStudioSettings settings)
    {
        var e = Require(settings);
        return KnowledgeHash.Of(JsonSerializer.Serialize(new { e.ModelId, e.ModelVersion, e.DocumentPrefix, e.QueryPrefix, e.ContextTokens, Metric = "cosine" }));
    }
    private static EmbeddingSettings Require(LmStudioSettings settings)
    {
        LocalEndpoint.Validate(settings);
        var embedding = settings.Embedding ?? throw new PreparationException("Configure a separate embedding model in Indexing Settings.");
        try { embedding.Validate(); } catch (ArgumentException ex) { throw new PreparationException(ex.Message); }
        return embedding;
    }
    public async Task<IReadOnlyList<EmbeddingModel>> GetModelsAsync(LmStudioSettings settings, CancellationToken ct)
    {
        var endpoint = LocalEndpoint.Validate(settings);
        using var client = factory.CreateClient("LmStudio");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, "/api/v1/models"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new PreparationException("Embedding model discovery requires LM Studio's native /api/v1/models endpoint. Update LM Studio and retry.");
            using var json = await ReadAsync(response, timeout.Token);
            return json.RootElement.GetProperty("models").EnumerateArray().Select(x => new EmbeddingModel(
                x.GetProperty("key").GetString()!, x.GetProperty("type").GetString()!,
                x.TryGetProperty("max_context_length", out var context) && context.TryGetInt32(out var length) ? length : null)).ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PreparationException("Embedding model discovery timed out."); }
        catch (HttpRequestException) { throw new PreparationException("LM Studio is unavailable. Start its local server and retry."); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new PreparationException("LM Studio returned an invalid capability catalog. Update LM Studio and retry."); }
    }
    public async Task<EmbeddingSpace> ValidateAsync(LmStudioSettings settings, CancellationToken ct)
    {
        var e = Require(settings);
        var matching = (await GetModelsAsync(settings, ct)).Where(x => x.Id == e.ModelId && x.Type == "embedding").ToList();
        if (matching.Count != 1) throw new PreparationException("The selected model is not uniquely available as an embedding model. Gemma/chat models cannot be used here.");
        var model = matching[0];
        if (model.ContextTokens is { } max && e.ContextTokens > max)
            throw new PreparationException($"Configured embedding context exceeds the server's {max}-token model limit.");
        var batch = await SendAsync(settings, [Probe], false, null, ct);
        var dimensions = batch.Vectors[0].Length;
        var fingerprint = ConfigurationFingerprint(settings);
        // Quantized sentinel detects common silent model replacements; it is evidence, not a cryptographic model-file identity.
        var probeHash = KnowledgeHash.Of(JsonSerializer.Serialize(batch.Vectors[0].Select(x => Math.Round(x, 4)).ToArray()));
        return new(KnowledgeHash.Of($"{fingerprint}|{dimensions}|{probeHash}"), e.ModelId!, e.ModelVersion, dimensions, fingerprint, probeHash);
    }
    public Task<EmbeddingBatch> EmbedAsync(LmStudioSettings settings, EmbeddingSpace space, IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        if (ConfigurationFingerprint(settings) != space.ConfigurationFingerprint)
            throw new PreparationException("Embedding configuration changed. Validate and reindex before searching.");
        return SendAsync(settings, texts, query, space.Dimensions, ct);
    }
    private async Task<EmbeddingBatch> SendAsync(LmStudioSettings settings, IReadOnlyList<string> texts, bool query, int? dimensions, CancellationToken ct)
    {
        var e = Require(settings);
        if (texts.Count < 1 || texts.Count > e.BatchSize || texts.Any(string.IsNullOrWhiteSpace))
            throw new PreparationException("Embedding batch is empty or exceeds the configured batch limit.");
        var input = texts.Select(x => (query ? e.QueryPrefix : e.DocumentPrefix) + x).ToArray();
        if (input.Any(x => StructureAwareChunker.Estimate(x) > e.ContextTokens))
            throw new PreparationException("Embedding input exceeds the configured estimated context budget, including prefixes. Reduce chunk/query size; no input was truncated.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(e.TimeoutSeconds));
        try
        {
            using var client = factory.CreateClient("LmStudio");
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(LocalEndpoint.Validate(settings), "embeddings"))
                { Content = JsonContent.Create(new { model = e.ModelId, input }) };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
                { await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), timeout.Token); continue; }
                if (!response.IsSuccessStatusCode) throw new PreparationException($"The local embedding endpoint returned HTTP {(int)response.StatusCode}. Verify the embedding model and its input limits.");
                using var json = await ReadAsync(response, timeout.Token);
                var root = json.RootElement;
                if (!root.TryGetProperty("model", out var identity) || identity.GetString() != e.ModelId)
                    throw new PreparationException("Embedding response model is missing or differs from the selected model. No vectors were accepted.");
                var data = root.GetProperty("data").EnumerateArray().ToList();
                if (data.Count != texts.Count) throw new PreparationException("The embedding response count does not match the requested texts.");
                var ordered = new float[texts.Count][];
                foreach (var item in data)
                {
                    var index = item.GetProperty("index").GetInt32();
                    if (index < 0 || index >= texts.Count || ordered[index] is not null)
                        throw new PreparationException("The embedding response contains invalid or duplicate input indices.");
                    var vector = item.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    dimensions ??= vector.Length;
                    if (vector.Length is < 1 or > 16000 || vector.Length != dimensions || vector.Any(x => !float.IsFinite(x)) || vector.All(x => x == 0))
                        throw new PreparationException("The embedding response has invalid values, zero norm or incompatible dimensions.");
                    ordered[index] = vector;
                }
                return new(ordered.ToList(), e.ModelId!);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PreparationException("Embedding request timed out. Retry or increase the embedding timeout."); }
        catch (HttpRequestException) { throw new PreparationException("Cannot reach the local embedding endpoint. Start LM Studio and retry."); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new PreparationException("The embedding endpoint returned an invalid payload. No vectors were accepted."); }
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 16 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new PreparationException("The embedding response exceeded the payload limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > limit) throw new PreparationException("The embedding response exceeded the payload limit.");
            await buffer.WriteAsync(bytes.AsMemory(0, count), ct);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
}
