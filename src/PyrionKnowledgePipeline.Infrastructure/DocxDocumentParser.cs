using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class DocxDocumentParser : IDocumentParser
{
    public string Extension => ".docx";

    public Task<ExtractionResult> ExtractAsync(Stream source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using (var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (archive.Entries.Count > 2000 || archive.Entries.Sum(x => x.Length) > 50 * 1024 * 1024)
                    throw new PreparationException("The DOCX package exceeds the safe expanded-size limit (50 MB or 2,000 parts).");
                if (archive.GetEntry("word/document.xml") is null)
                    throw new PreparationException("The file is not a valid Word DOCX package.");
            }
            source.Position = 0;
            using var document = WordprocessingDocument.Open(source, false, new OpenSettings { MaxCharactersInPart = 10_000_000 });
            if (document.DocumentType != WordprocessingDocumentType.Document)
                throw new PreparationException("Only standard DOCX documents are supported; macro documents and templates are not accepted.");
            var main = document.MainDocumentPart ?? throw new PreparationException("The DOCX has no main document part.");
            var body = main.Document?.Body ?? throw new PreparationException("The DOCX has no document body.");
            if (body.Descendants().Any(x => IsExtractedContent(x) &&
                x.NamespaceUri == "http://schemas.openxmlformats.org/officeDocument/2006/math"))
                throw new PreparationException("This DOCX contains an equation that cannot yet be extracted faithfully. Convert the equation to ordinary Unicode text in a copy of the document before uploading it.");
            var styles = main.StyleDefinitionsPart?.Styles?.Elements<Style>()
                .Where(x => x.StyleId?.Value is not null).ToDictionary(x => x.StyleId!.Value!, x => x) ?? [];
            var blocks = new List<DocumentBlock>();
            var warnings = new HashSet<string>();
            var symbols = body.Descendants<SymbolChar>().Where(IsExtractedContent).ToList();
            foreach (var symbol in symbols) ReadSymbol(symbol);
            if (symbols.Count > 0)
                warnings.Add($"Converted {symbols.Count} Wingdings 2 checkmark(s) (F050) to Unicode ✓. The original DOCX is unchanged.");
            if (main.HeaderParts.Any() || main.FooterParts.Any() || main.FootnotesPart is not null || main.EndnotesPart is not null)
                warnings.Add("Headers, footers, footnotes and endnotes are outside this milestone. Only the main document body is extracted.");
            if (body.Descendants<Drawing>().Any() || body.Descendants<Picture>().Any() || body.Descendants<TextBoxContent>().Any())
                warnings.Add("Images and text boxes are not extracted. Review the original for content outside body paragraphs and tables.");
            if (body.Descendants<InsertedRun>().Any() || body.Descendants<DeletedRun>().Any())
                warnings.Add("Tracked changes are present. Inserted text is included and deleted text is excluded; resolve tracked changes in Word for an authoritative source.");
            if (body.Descendants<SimpleField>().Any() || body.Descendants<FieldChar>().Any())
                warnings.Add("Field display text is extracted as stored; fields are not recalculated.");
            foreach (var (element, reference) in EnumerateBlocks(body, "word/document.xml#body", warnings, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (element is Paragraph p)
                {
                    var properties = ResolveParagraphProperties(p, styles);
                    var outline = properties.Select(x => x.OutlineLevel?.Val?.Value).FirstOrDefault(x => x is not null);
                    int? level = outline is >= 0 and <= 5 ? outline + 1 : null;
                    if (outline is >= 6 and <= 8) warnings.Add("Heading levels 7–9 are retained as paragraphs; normalized headings support levels 1–6.");
                    var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    if (level is null && styleId is not null)
                    {
                        var name = styles.GetValueOrDefault(styleId)?.StyleName?.Val?.Value ?? styleId;
                        var compact = name.Replace(" ", "");
                        if (compact.Length == 8 && compact.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && compact[7] is >= '1' and <= '6')
                            level = compact[7] - '0';
                    }
                    var numbering = properties.Select(x => x.NumberingProperties).FirstOrDefault(x => x is not null);
                    if (numbering?.NumberingId?.Val?.Value == 0) numbering = null;
                    var runs = p.Descendants<Run>().Where(x => !x.Ancestors<TextBoxContent>().Any()).ToList();
                    var fontSizes = runs.Select(x => decimal.TryParse(x.RunProperties?.FontSize?.Val?.Value,
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var size) ? (decimal?)(size / 2) : null).ToList();
                    var bold = runs.Where(x => !string.IsNullOrWhiteSpace(ReadText(x))).ToList();
                    var evidence = new TextEvidence(styleId, bold.Count > 0 && bold.All(x => x.RunProperties?.Bold is { } b && (b.Val is null || b.Val.Value)),
                        fontSizes.Max(), numbering?.NumberingId?.Val?.Value.ToString(CultureInfo.InvariantCulture), numbering?.NumberingLevelReference?.Val?.Value);
                    blocks.Add(new DocumentBlock(reference, level.HasValue ? BlockType.Heading : numbering is not null ? BlockType.List : BlockType.Paragraph,
                        level, ReadText(p), Formatting: evidence));
                }
                else if (element is Table table)
                {
                    if (table.Descendants<Table>().Any()) throw new PreparationException("Nested tables are not supported yet. Use a DOCX with simple tables.");
                    if (table.Descendants<GridSpan>().Any() || table.Descendants<VerticalMerge>().Any())
                        warnings.Add("Merged table cells are extracted in source row/cell order; visual cell spanning is not represented.");
                    var rows = table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().Select((row, r) =>
                        new PyrionKnowledgePipeline.Domain.TableRow(row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Select((cell, c) =>
                            new PyrionKnowledgePipeline.Domain.TableCell(ReadCell(cell, $"{reference}/row/{r}/cell/{c}", warnings, cancellationToken),
                                $"{reference}/row/{r}/cell/{c}")).ToList())).ToList();
                    if (rows.Count == 0 || rows.Any(x => x.Cells.Count == 0)) throw new PreparationException("The DOCX contains an empty or unsupported table.");
                    blocks.Add(new DocumentBlock(reference, BlockType.Table, null, "", rows));
                }
                else if (element is not SectionProperties)
                    throw new PreparationException($"The document contains an unsupported body structure at {reference}. Convert it to ordinary paragraphs or tables in a copy of the document first.");
            }
            return Task.FromResult(new ExtractionResult(blocks, warnings.ToList()));
        }
        catch (PreparationException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or OpenXmlPackageException or System.Xml.XmlException or IOException or ArgumentException)
        {
            throw new PreparationException("The DOCX could not be read. It may be corrupt, encrypted, or not a supported Word document.");
        }
    }

    private static IEnumerable<(OpenXmlElement Element, string Reference)> EnumerateBlocks(
        OpenXmlCompositeElement container, string parentReference, HashSet<string> warnings, CancellationToken ct)
    {
        var index = 0;
        foreach (var element in container.Elements())
        {
            ct.ThrowIfCancellationRequested();
            var reference = $"{parentReference}/{index++}";
            if (element is SdtBlock control)
            {
                warnings.Add("Content controls are extracted in stored order. Linked content and the table of contents are not refreshed.");
                if (control.SdtContentBlock is { } content)
                    foreach (var block in EnumerateBlocks(content, reference + "/sdtContent", warnings, ct))
                        yield return block;
            }
            else yield return (element, reference);
        }
    }

    private static string ReadCell(DocumentFormat.OpenXml.Wordprocessing.TableCell cell, string reference,
        HashSet<string> warnings, CancellationToken ct)
    {
        var paragraphs = new List<string>();
        foreach (var (element, location) in EnumerateBlocks(cell, reference, warnings, ct))
        {
            if (element is Paragraph p) paragraphs.Add(ReadText(p));
            else if (element is not TableCellProperties)
                throw new PreparationException($"The table cell contains an unsupported structure at {location}. No cell content was discarded.");
        }
        return string.Join("\n", paragraphs);
    }

    private static bool IsExtractedContent(OpenXmlElement element) =>
        !element.Ancestors<TextBoxContent>().Any() && !element.Ancestors<DeletedRun>().Any();

    private static string ReadSymbol(SymbolChar symbol)
    {
        // The installed Microsoft Wingdings 2 font identifies F050 as its "check"
        // glyph. Map only this verified font/code pair, never private-use codes
        // from another font or an unknown symbol whose meaning could differ.
        if (string.Equals(symbol.Font?.Value?.Trim(), "Wingdings 2", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(symbol.Char?.Value, "F050", StringComparison.OrdinalIgnoreCase))
            return "\u2713";
        throw new PreparationException($"Unsupported symbol font '{symbol.Font?.Value ?? "unspecified"}', character '{symbol.Char?.Value ?? "unspecified"}'. Its meaning cannot be safely inferred. Replace this symbol with its Unicode equivalent in a copy of the document.");
    }

    private static List<ParagraphProperties> ResolveParagraphProperties(Paragraph paragraph, Dictionary<string, Style> styles)
    {
        var result = new List<ParagraphProperties>();
        if (paragraph.ParagraphProperties is { } own) result.Add(own);
        var id = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var visited = new HashSet<string>();
        while (id is not null && visited.Add(id) && styles.TryGetValue(id, out var style))
        {
            if (style.StyleParagraphProperties is { } props)
                result.Add(new ParagraphProperties(props.ChildElements.Select(x => x.CloneNode(true))));
            id = style.BasedOn?.Val?.Value;
        }
        return result;
    }

    private static string ReadText(OpenXmlElement element) => string.Concat(element.Descendants()
        .Where(IsExtractedContent)
        .Select(x => x switch
        {
            Text text => text.Text,
            TabChar => "\t",
            Break => "\n",
            CarriageReturn => "\n",
            NoBreakHyphen => "\u2011",
            SoftHyphen => "\u00ad",
            SymbolChar symbol => ReadSymbol(symbol),
            _ => ""
        }));
}
