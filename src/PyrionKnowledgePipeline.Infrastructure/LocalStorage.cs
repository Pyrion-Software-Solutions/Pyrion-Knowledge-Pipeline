using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class StorageOptions
{
    public string RootPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyrionKnowledgePipeline");
}

internal static class AtomicFile
{
    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, DocumentJson.Options, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class LocalDocumentStore(IOptions<StorageOptions> options) : IDocumentStore
{
    private readonly string root = Path.GetFullPath(Path.Combine(options.Value.RootPath, "documents"));
    // A single-writer application instance is enforced by the web host; locks protect concurrent browser circuits.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private string Folder(Guid id) => Path.Combine(root, id.ToString("N"));

    public async Task CreateAsync(PreparedDocument document, Stream original, CancellationToken cancellationToken)
    {
        var folder = Folder(document.Id);
        Directory.CreateDirectory(folder);
        await using (var stream = new FileStream(Path.Combine(folder, "original.docx"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await original.CopyToAsync(stream, cancellationToken);
        await AtomicFile.WriteJsonAsync(Path.Combine(folder, "review.json"), document, cancellationToken);
    }

    public async Task<PreparedDocument?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Folder(id), "review.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<PreparedDocument>(stream, DocumentJson.Options, cancellationToken)
                ?? throw new PreparationException("A saved review is empty. Restore it from your local backup.");
        }
        catch (JsonException) { throw new PreparationException("A saved review could not be read. Restore it from your local backup."); }
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<DocumentSummary>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var id)) continue;
            if (await GetAsync(id, cancellationToken) is { } d)
                result.Add(new(d.Id, d.Metadata.Title, d.Metadata.DocumentNumber, d.Metadata.Revision, d.Status, d.CreatedAt));
        }
        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task SaveAsync(PreparedDocument document, int expectedVersion, CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(document.Id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await GetAsync(document.Id, cancellationToken) ?? throw new PreparationException("The saved document no longer exists.");
            if (current.Version != expectedVersion || current.Status == ReviewStatus.Approved)
                throw new PreparationException("The review changed in another window or is already approved. Reopen it before continuing.");
            if (document.Status == ReviewStatus.Approved && document.Approved is null)
                throw new PreparationException("An approval must include its normalized document.");
            document.Version = expectedVersion + 1;
            if (document.Approved is not null)
                await AtomicFile.WriteJsonAsync(Path.Combine(Folder(document.Id), "approved.json"), document.Approved, cancellationToken);
            await AtomicFile.WriteJsonAsync(Path.Combine(Folder(document.Id), "review.json"), document, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]?> ReadApprovedJsonAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await GetAsync(id, cancellationToken) is not { Status: ReviewStatus.Approved, Approved: not null }) return null;
        return await File.ReadAllBytesAsync(Path.Combine(Folder(id), "approved.json"), cancellationToken);
    }
}

public sealed class LocalSettingsStore(IOptions<StorageOptions> options) : ISettingsStore
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string path = Path.Combine(options.Value.RootPath, "settings.json");

    public async Task<LmStudioSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var settings = await JsonSerializer.DeserializeAsync<LmStudioSettings>(stream, DocumentJson.Options, cancellationToken)
                ?? throw new PreparationException("Local settings are empty. Save them again in LM Studio Settings.");
            LocalEndpoint.Validate(settings);
            return settings;
        }
        catch (JsonException) { throw new PreparationException("Local settings are invalid. Save them again in LM Studio Settings."); }
    }

    public async Task SaveAsync(LmStudioSettings settings, CancellationToken cancellationToken)
    {
        LocalEndpoint.Validate(settings);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.Value.RootPath);
            await AtomicFile.WriteJsonAsync(path, settings, cancellationToken);
        }
        finally { gate.Release(); }
    }
}
