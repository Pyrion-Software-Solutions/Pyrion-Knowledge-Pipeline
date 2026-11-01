using System.Net;
using Microsoft.Extensions.Options;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Infrastructure;
using PyrionKnowledgePipeline.Web.Components;

var builder = WebApplication.CreateBuilder(args);
var addresses = builder.Configuration["urls"] ?? "http://localhost:5187";
foreach (var address in addresses.Split(';', StringSplitOptions.RemoveEmptyEntries))
{
    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !LocalEndpoint.IsLoopbackHost(uri.Host))
        throw new InvalidOperationException("Pyrion must bind only to localhost or a loopback IP address.");
}
builder.WebHost.UseUrls(addresses);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddLocalInfrastructure(builder.Configuration);
var app = builder.Build();
var storage = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
Directory.CreateDirectory(storage.RootPath);
using var storageLease = new FileStream(Path.Combine(storage.RootPath, ".instance.lock"), FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None);

app.Use(async (context, next) =>
{
    if (!LocalEndpoint.IsLoopbackHost(context.Request.Host.Host) ||
        (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next(context);
});
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapGet("/documents/{id:guid}/approved.json", async (Guid id, IDocumentStore store, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var json = await store.ReadApprovedJsonAsync(id, ct);
    return json is null ? Results.NotFound() : Results.File(json, "application/json", $"pyrion-{id:N}-approved.json");
});
app.MapGet("/documents/{id:guid}/draft.json", async (Guid id, PreparationWorkflow workflow, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var draft = await workflow.ExportDraftAsync(id, ct);
    return draft is null ? Results.NotFound() : Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(draft, DocumentJson.Options),
        "application/json", $"pyrion-{id:N}-review-draft.json");
});
app.MapGet("/samples/engineering-review.docx", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.ContentRootPath, "Samples", "engineering-review.docx"),
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "engineering-review.docx"));
app.MapGet("/schemas/normalized-document-v1.schema.json", () => Results.Text(DocumentContractValidator.NormalizedSchemaJson, "application/schema+json"));
app.MapGet("/schemas/normalized-draft-v1.schema.json", () => Results.Text(DocumentContractValidator.DraftSchemaJson, "application/schema+json"));
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
