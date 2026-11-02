using System.Text;
using System.Text.Json;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class StructureAwareChunker : IChunkingService
{
    public static int Estimate(string text) => (text.EnumerateRunes().Count() + 1) / 2;
    private sealed record Unit(string Text, List<SourceSpan> Sources, string Type);

    public ChunkPreview Build(ApprovedSource source, ChunkingSettings settings, CancellationToken ct)
    {
        try { settings.Validate(); } catch (ArgumentException ex) { throw new PreparationException(ex.Message); }
        var chunks = new List<KnowledgeChunk>();
        var notes = new List<string>(source.Document.ExtractionWarnings ?? []);
        if (source.Document.SchemaVersion == "1.0") notes.Add("Legacy v1.0 source: extraction coverage notes were not required by this contract.");
        var headings = new List<(int Level, string Text)>();
        var pending = new List<Unit>();
        string Context() => string.Join(" / ", headings.Select(x => x.Text));
        string Embed(string text, string? header = null) => string.Join("\n", new[] { Context(), header, text }.Where(x => !string.IsNullOrEmpty(x)));
        void Emit(List<Unit> units, string? header = null, List<SourceSpan>? headerSources = null)
        {
            if (units.Count == 0) return;
            var display = string.Join("\n", units.Select(x => x.Text));
            var embedded = Embed(display, header);
            if (Estimate(embedded) > settings.MaximumTokens)
                throw new PreparationException("Heading/table context exceeds the maximum chunk size. Increase the maximum or reduce the designated table headers; no text was truncated.");
            var spans = units.SelectMany(x => x.Sources).ToList();
            if (headerSources is not null) spans.InsertRange(0, headerSources.Select(x => x with { ContextOnly = true }));
            var hash = KnowledgeHash.Of(embedded);
            var ordinal = chunks.Count;
            var id = KnowledgeHash.Of($"{source.ArtifactHash}|{settings.Fingerprint}|{ordinal}|{hash}|{JsonSerializer.Serialize(spans)}");
            chunks.Add(new(id, ordinal, display, embedded, units.Select(x => x.Type).Distinct().Count() == 1 ? units[0].Type : "mixed",
                headings.Select(x => x.Text).ToList(), spans, Estimate(embedded), hash));
            if (chunks.Count > 10000) throw new PreparationException("This document exceeds the 10,000-chunk indexing limit.");
        }
        void Flush() { Emit(pending); pending.Clear(); }
        void Add(Unit unit)
        {
            if (pending.Count > 0 && Estimate(Embed(string.Join("\n", pending.Select(x => x.Text).Append(unit.Text)))) > settings.TargetTokens)
            {
                var overlap = new List<Unit>();
                foreach (var previous in pending.AsEnumerable().Reverse())
                {
                    if (Estimate(string.Join("\n", overlap.Select(x => x.Text).Prepend(previous.Text))) > settings.OverlapTokens) break;
                    overlap.Insert(0, previous with { Sources = previous.Sources.Select(x => x with { ContextOnly = true }).ToList() });
                }
                Flush();
                if (Estimate(Embed(string.Join("\n", overlap.Select(x => x.Text).Append(unit.Text)))) <= settings.MaximumTokens) pending.AddRange(overlap);
            }
            pending.Add(unit);
        }
        foreach (var block in source.Document.Blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (block.Type == BlockType.Heading)
            {
                Flush();
                headings.RemoveAll(x => x.Level >= block.HeadingLevel!.Value);
                // Heading text is primary content once; inherited headings are additional embedding context.
                if (string.IsNullOrWhiteSpace(block.Text)) notes.Add($"Empty heading {block.SourceReference}: no searchable characters; source retained in approved JSON.");
                else foreach (var unit in Split(block, settings.MaximumTokens - Estimate(Context()) - 2)) Emit([unit]);
                headings.Add((block.HeadingLevel!.Value, block.Text));
                continue;
            }
            if (block.Type == BlockType.Table)
            {
                Flush();
                var rows = block.Rows!;
                var headerCount = settings.TableHeaderRows?.GetValueOrDefault($"{source.Document.DocumentId:N}:{block.SourceReference}") ?? 0;
                if (headerCount >= rows.Count && headerCount != 0)
                    throw new PreparationException($"Header selection at {block.SourceReference} must leave at least one data row.");
                Unit Row(int r) => new(string.Join("\t", rows[r].Cells.Select(c => c.Text)),
                    rows[r].Cells.Select((c, col) => new SourceSpan(block.SourceReference, c.SourceReference, 0, c.Text.Length, r, col)).ToList(), "table");
                var headers = Enumerable.Range(0, headerCount).Select(Row).ToList();
                var headerText = headerCount == 0 ? null : string.Join("\n", headers.Select(x => x.Text));
                var headerSpans = headers.SelectMany(x => x.Sources).ToList();
                var group = new List<Unit>();
                // Headers are included as primary content in the first chunk and repeated only as context afterward.
                group.AddRange(headers);
                var first = true;
                for (var r = headerCount; r < rows.Count; r++)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = Row(r);
                    var repeated = first ? null : headerText;
                    if (Estimate(Embed(row.Text, headerText)) > settings.MaximumTokens)
                        throw new PreparationException($"Table row {r} at {block.SourceReference} cannot fit intact with context. Increase the maximum; cells were not split or truncated.");
                    if (group.Count > (first ? headerCount : 0) && Estimate(Embed(string.Join("\n", group.Select(x => x.Text).Append(row.Text)), repeated)) > settings.TargetTokens)
                    {
                        Emit(group, repeated, first ? null : headerSpans);
                        group.Clear(); first = false;
                    }
                    group.Add(row);
                }
                Emit(group, first ? null : headerText, first ? null : headerSpans);
                continue;
            }
            if (string.IsNullOrWhiteSpace(block.Text))
            {
                notes.Add($"Empty source block {block.SourceReference}: no searchable characters; source retained in approved JSON.");
                continue;
            }
            foreach (var unit in Split(block, settings.MaximumTokens - Estimate(Context()) - 2)) Add(unit);
        }
        Flush();
        if (chunks.Count == 0) throw new PreparationException("This approval has no searchable text.");
        return new(chunks, notes, settings.Fingerprint);
    }

    private static IEnumerable<Unit> Split(DocumentBlock block, int budget)
    {
        if (budget < 1) throw new PreparationException($"Heading context leaves no space for {block.SourceReference}.");
        var start = 0;
        while (start < block.Text.Length)
        {
            var end = start;
            var safe = start;
            var scalars = 0;
            while (end < block.Text.Length && scalars < budget * 2)
            {
                var ch = block.Text[end];
                end += char.IsHighSurrogate(ch) && end + 1 < block.Text.Length && char.IsLowSurrogate(block.Text[end + 1]) ? 2 : 1;
                scalars++;
                if (ch is '\n' or '\r' || (ch is '.' or '!' or '?' or '؟' or '؛') &&
                    (end == block.Text.Length || char.IsWhiteSpace(block.Text[end]))) safe = end;
            }
            if (end < block.Text.Length)
            {
                if (safe == start) throw new PreparationException($"Oversized block {block.SourceReference} has no safe sentence/line boundary within the chunk budget. Increase the maximum; no content was truncated.");
                end = safe;
            }
            yield return new(block.Text[start..end], [new(block.SourceReference, block.SourceReference, start, end - start)], block.Type.ToString().ToLowerInvariant());
            start = end;
        }
    }
}
