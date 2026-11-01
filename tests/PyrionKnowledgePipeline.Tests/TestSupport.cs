using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "PyrionTests", Guid.NewGuid().ToString("N"));
    public LocalDocumentStore Store { get; }
    public LocalSettingsStore Settings { get; }
    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
        var options = Options.Create(new StorageOptions { RootPath = Root });
        Store = new(options); Settings = new(options);
    }
    public PreparationWorkflow Workflow(ILmStudioClient? client = null) => new(new DocxDocumentParser(), Store, Settings,
        client ?? new MockLmStudio(), new DocumentContractValidator());
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (resolved.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PyrionTests")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(resolved, recursive: true);
    }
}

internal static class Fixture
{
    public static byte[] Docx => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", "engineering-review.docx"));
    public static DocumentMetadata Metadata => new("Engineering", "ENG-001", "Pump inspection procedure", "A", "mixed");
    public static Task<ExtractionResult> ExtractAsync() => new DocxDocumentParser().ExtractAsync(new MemoryStream(Docx), default);
    public static AnalysisProposal Proposal(ExtractionResult source) => new(
        [new(source.Blocks[1].SourceReference, BlockType.Heading, 1, "The bold 18pt paragraph is a section heading.")],
        source.Blocks.Select((b, i) => new ProposedBlock(b.SourceReference, i == 1 ? BlockType.Heading : b.Type, i == 1 ? 1 : b.HeadingLevel)).ToList());
    public static HttpResponseMessage Response(object payload, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(payload, DocumentJson.Options), Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Completion(string content, string finishReason = "stop") => Response(new
    { choices = new[] { new { finish_reason = finishReason, message = new { role = "assistant", content } } } });
}

internal sealed class MockLmStudio : ILmStudioClient
{
    public Exception? Failure { get; set; }
    public Task<IReadOnlyList<LocalModel>> GetModelsAsync(LmStudioSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LocalModel>>([new("test-model")]);
    public Task<AnalysisProposal> AnalyzeAsync(LmStudioSettings settings, ExtractionResult extraction, string rules,
        Action<string>? reportStatus, CancellationToken cancellationToken) => Failure is null
        ? Task.FromResult(Fixture.Proposal(extraction)) : Task.FromException<AnalysisProposal>(Failure);
}

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
}

internal sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}
