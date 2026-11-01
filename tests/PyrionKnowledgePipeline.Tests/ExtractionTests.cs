using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class ExtractionTests
{
    [Fact]
    public async Task Preserves_order_headings_lists_tables_whitespace_and_Persian()
    {
        var source = Fixture.Docx;
        using var stream = new MemoryStream(source);
        var result = await new DocxDocumentParser().ExtractAsync(stream, default);
        Assert.Equal(7, result.Blocks.Count);
        Assert.Equal([BlockType.Heading, BlockType.Paragraph, BlockType.Paragraph, BlockType.List, BlockType.Table, BlockType.Heading, BlockType.Paragraph], result.Blocks.Select(x => x.Type));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"word/document.xml#body/{i}"), result.Blocks.Select(x => x.SourceReference));
        Assert.Equal(1, result.Blocks[0].HeadingLevel);
        Assert.True(result.Blocks[1].Formatting!.Bold);
        Assert.Equal(18m, result.Blocks[1].Formatting!.FontSizePoints);
        Assert.Equal("  Verify pressure remains at 10 bar.\tRecord the result.\nDo not alter set points.  ", result.Blocks[2].Text);
        Assert.Equal("1", result.Blocks[3].Formatting!.NumberingId);
        Assert.Equal("10 bar", result.Blocks[4].Rows![1].Cells[1].Text);
        Assert.Equal("word/document.xml#body/4/row/1/cell/1", result.Blocks[4].Rows![1].Cells[1].SourceReference);
        Assert.Equal(2, result.Blocks[5].HeadingLevel);
        Assert.Equal("فشار مجاز ۱۰ بار است. نتیجه بازرسی ثبت شود.", result.Blocks[6].Text);
        Assert.Equal(source, stream.ToArray());
    }

    [Fact]
    public async Task Resolves_inherited_heading_style_and_keeps_empty_paragraphs()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Custom" }), new Run(new Text("A heading"))), new Paragraph()));
            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(
                new Style(new StyleParagraphProperties(new OutlineLevel { Val = 1 })) { StyleId = "Parent", Type = StyleValues.Paragraph },
                new Style(new BasedOn { Val = "Parent" }) { StyleId = "Custom", Type = StyleValues.Paragraph });
        }
        stream.Position = 0;
        var result = await new DocxDocumentParser().ExtractAsync(stream, default);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(2, result.Blocks[0].HeadingLevel);
        Assert.Equal("", result.Blocks[1].Text);
    }

    [Fact]
    public async Task Rejects_disguised_or_corrupt_DOCX()
    {
        await Assert.ThrowsAsync<PreparationException>(() => new DocxDocumentParser().ExtractAsync(new MemoryStream("not a docx"u8.ToArray()), default));
    }

    [Fact]
    public async Task Preserves_special_hyphens_and_does_not_treat_disabled_numbering_as_a_list()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new NumberingProperties(new NumberingId { Val = 0 })),
                new Run(new Text("a"), new NoBreakHyphen(), new Text("b"), new SoftHyphen(), new Text("c")))));
        }
        stream.Position = 0;
        var result = await new DocxDocumentParser().ExtractAsync(stream, default);
        Assert.Equal("a\u2011b\u00adc", result.Blocks[0].Text);
        Assert.Equal(BlockType.Paragraph, result.Blocks[0].Type);
    }

    [Fact]
    public async Task Rejects_equations_instead_of_silently_losing_technical_content()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(
                new DocumentFormat.OpenXml.Math.OfficeMath(new DocumentFormat.OpenXml.Math.Run(new DocumentFormat.OpenXml.Math.Text("x=2"))))));
        }
        stream.Position = 0;
        var error = await Assert.ThrowsAsync<PreparationException>(() => new DocxDocumentParser().ExtractAsync(stream, default));
        Assert.Contains("equation", error.Message);
    }

    [Theory]
    [InlineData("Wingdings 2", "F050")]
    [InlineData("wingdings 2", "f050")]
    public async Task Extracts_verified_checkmarks_in_paragraphs_and_table_cells(string font, string code)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var paragraph = new Paragraph(new Run(new Text("Before "), new SymbolChar { Font = font, Char = code }, new Text(" after")));
            var table = new Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                new DocumentFormat.OpenXml.Wordprocessing.TableCell(new Paragraph(new Run(new SymbolChar { Font = font, Char = code })))));
            doc.AddMainDocumentPart().Document = new Document(new Body(paragraph, table));
        }
        var original = stream.ToArray();
        stream.Position = 0;
        var result = await new DocxDocumentParser().ExtractAsync(stream, default);
        Assert.Equal("Before ✓ after", result.Blocks[0].Text);
        Assert.Equal("✓", result.Blocks[1].Rows![0].Cells[0].Text);
        Assert.Contains(result.Warnings, x => x.Contains("2 Wingdings 2 checkmark(s)"));
        Assert.Equal(original, stream.ToArray());
    }

    [Theory]
    [InlineData("Wingdings", "F050")]
    [InlineData("Wingdings 2", "F051")]
    public async Task Unknown_font_or_character_is_not_guessed_or_discarded(string font, string code)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new SymbolChar { Font = font, Char = code }))));
        stream.Position = 0;
        var error = await Assert.ThrowsAsync<PreparationException>(() => new DocxDocumentParser().ExtractAsync(stream, default));
        Assert.Contains(font, error.Message);
        Assert.Contains(code, error.Message);
        Assert.DoesNotContain("equation", error.Message);
    }

    [Fact]
    public async Task Unwraps_nested_content_controls_in_order_and_preserves_source_locations()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var inner = new SdtBlock(new SdtContentBlock(new Paragraph(new Run(new Text("Stored TOC entry")))));
            var control = new SdtBlock(new SdtContentBlock(new Paragraph(new Run(new Text("Contents"))), inner));
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Before"))), control,
                new Paragraph(new Run(new Text("After")))));
        }
        stream.Position = 0;
        var result = await new DocxDocumentParser().ExtractAsync(stream, default);
        Assert.Equal(["Before", "Contents", "Stored TOC entry", "After"], result.Blocks.Select(x => x.Text));
        Assert.Equal(["word/document.xml#body/0", "word/document.xml#body/1/sdtContent/0",
            "word/document.xml#body/1/sdtContent/1/sdtContent/0", "word/document.xml#body/2"], result.Blocks.Select(x => x.SourceReference));
        Assert.Contains(result.Warnings, x => x.Contains("Content controls"));
    }

    [Fact]
    public async Task Includes_content_control_paragraphs_inside_table_cells()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var control = new SdtBlock(new SdtContentBlock(new Paragraph(new Run(new Text("Controlled text")))));
            var cell = new DocumentFormat.OpenXml.Wordprocessing.TableCell(new TableCellProperties(),
                new Paragraph(new Run(new Text("First"))), control, new Paragraph(new Run(new Text("Last"))));
            doc.AddMainDocumentPart().Document = new Document(new Body(new Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(cell))));
        }
        stream.Position = 0;
        var result = await new DocxDocumentParser().ExtractAsync(stream, default);
        Assert.Equal("First\nControlled text\nLast", result.Blocks[0].Rows![0].Cells[0].Text);
    }

    [Fact]
    public async Task Unknown_symbol_inside_deleted_text_does_not_block_extraction()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Kept")),
                new DeletedRun(new Run(new SymbolChar { Font = "Unknown font", Char = "F050" })))));
        stream.Position = 0;
        var result = await new DocxDocumentParser().ExtractAsync(stream, default);
        Assert.Equal("Kept", result.Blocks[0].Text);
        Assert.Contains(result.Warnings, x => x.Contains("Tracked changes"));
    }

    [Fact]
    public async Task Rejects_unsupported_nested_tables_visibly()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            var nested = new Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                new DocumentFormat.OpenXml.Wordprocessing.TableCell(new Paragraph(new Run(new Text("Nested"))))));
            var outer = new Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                new DocumentFormat.OpenXml.Wordprocessing.TableCell(nested, new Paragraph())));
            main.Document = new Document(new Body(outer));
        }
        stream.Position = 0;
        var error = await Assert.ThrowsAsync<PreparationException>(() => new DocxDocumentParser().ExtractAsync(stream, default));
        Assert.Contains("Nested tables", error.Message);
    }
}
