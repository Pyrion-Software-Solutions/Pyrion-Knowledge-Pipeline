using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Tests;

public class WebIntegrationTests
{
    [Fact]
    public async Task Local_host_serves_UI_and_only_downloads_explicitly_approved_JSON()
    {
        using var w = new TestWorkspace();
        await using var factory = new LocalWebFactory(w.Root);
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        Assert.Contains("Your document workspace", await http.GetStringAsync("/"));
        Assert.Contains("Test connection", await http.GetStringAsync("/settings"));
        Assert.Contains("Upload &amp; extract DOCX", await http.GetStringAsync("/preparation"));
        Assert.Equal(Fixture.Docx, await http.GetByteArrayAsync("/samples/engineering-review.docx"));
        await factory.Services.GetRequiredService<ISettingsStore>().SaveAsync(new(ModelId: "test-model"), default);
        var workflow = factory.Services.GetRequiredService<PreparationWorkflow>();
        var doc = await workflow.UploadAsync("test.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        var url = $"/documents/{doc.Id}/approved.json";
        var draftUrl = $"/documents/{doc.Id}/draft.json";
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(draftUrl)).StatusCode);
        doc = await workflow.AnalyzeAsync(doc.Id, "", null, default);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url)).StatusCode);
        var draftDownload = await http.GetAsync(draftUrl);
        Assert.Equal(HttpStatusCode.OK, draftDownload.StatusCode);
        var draftContent = JsonSerializer.Deserialize<NormalizedDocument>(await draftDownload.Content.ReadAsStringAsync(), DocumentJson.Options)!;
        Assert.Equal(ReviewStatus.AwaitingReview, draftContent.Approval.Status);
        Assert.Null(draftContent.Approval.ApprovedAt);
        Assert.NotNull(draftContent.ExtractionWarnings);
        doc = await workflow.ApproveAsync(doc.Id, doc.Draft!.Id, "Integration reviewer", default);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(draftUrl)).StatusCode);
        var download = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/json", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", download.Headers.CacheControl!.ToString());
        Assert.Contains("attachment", download.Content.Headers.ContentDisposition!.ToString());
        var result = JsonSerializer.Deserialize<NormalizedDocument>(await download.Content.ReadAsStringAsync(), DocumentJson.Options);
        Assert.Equal(ReviewStatus.Approved, result!.Approval.Status);
        Assert.Equal("Integration reviewer", result.Approval.ApprovedBy);
        using var badHost = new HttpRequestMessage(HttpMethod.Get, "/"); badHost.Headers.Host = "external.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(badHost)).StatusCode);
    }

    private sealed class LocalWebFactory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Storage:RootPath", root);
            builder.ConfigureServices(services => { services.RemoveAll<ILmStudioClient>(); services.AddSingleton<ILmStudioClient, MockLmStudio>(); });
        }
    }
}
