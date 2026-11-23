using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// VBA macros inside Office files. Real files come from Apache POI's public test corpus (Apache-2.0, downloaded locally, never committed);
/// the expected macro text is the ".vba" export published next to them, i.e. an independent source of truth. Skipped when absent (CI).
/// </summary>
public sealed class VbaMacroTests
{
    private static readonly string Fixtures = Path.Combine(Environment.GetEnvironmentVariable("PARUX_OFFICE_FIXTURES") ?? @"C:\Users\DTS\Desktop\Parux\fixtures", "vba");

    private static ExtractedDocument? Load(string file)
    {
        string path = Path.Combine(Fixtures, file);
        if (!File.Exists(path)) return null;
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        string outDir = Environment.GetEnvironmentVariable("PARUX_CAN_PROBE_OUT") ?? "";
        if (outDir.Length > 0)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "vba_" + file + ".index.txt"), $"extractor: {d.Extractor}; tier: {d.Tier}; note: {d.Note}; segments: {d.Segments.Count}\n\n" +
                string.Join("\n-----\n", d.Segments.Select(s => "[" + s.Position.Describe() + "]\n" + s.Text)));
        }
        return d;
    }

    [Theory]
    [InlineData("SimpleMacro.docm")]
    [InlineData("SimpleMacro.doc")]
    public void Word_macros_are_indexed_with_their_source(string file)
    {
        ExtractedDocument? d = Load(file); if (d == null) return;
        string t = d.FullText;
        Assert.Contains("Sub TestMacro()", t);
        Assert.Contains("ActiveDocument.Paragraphs(1).Range.Text = \"This is a macro word processing document\"", t);
        Assert.Contains("TestMacro", d.Segments[^1].Text + string.Join(" ", d.Segments.Select(s => s.Position.Heading)));
        Assert.Contains("VBA macro", d.Note);
    }

    [Fact]
    public void PowerPoint_package_macros_are_indexed_with_their_source()
    {
        ExtractedDocument? d = Load("SimpleMacro.pptm"); if (d == null) return;
        Assert.Contains("This is a macro slideshow", d.FullText);
        Assert.Contains("Sub TestMacro()", d.FullText);
    }

    [Theory]
    [InlineData("Macro45431.xlsm")]
    public void Excel_macro_workbooks_expose_their_modules(string file)
    {
        ExtractedDocument? d = Load(file); if (d == null) return;
        Assert.Contains("VBA macro project", d.FullText);
        Assert.Contains(d.Segments, s => s.Text.Contains(" macro module ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Macro_text_matches_the_published_export_line_by_line()
    {
        string vba = Path.Combine(Fixtures, "SimpleMacro.doc.vba");
        ExtractedDocument? d = Load("SimpleMacro.docm"); if (d == null || !File.Exists(vba)) return;
        string text = d.FullText;
        foreach (string line in File.ReadAllLines(vba).Select(l => l.TrimEnd()).Where(l => l.Length > 3 && l != "'"))
            Assert.Contains(line.Trim(), text);
    }

    [Fact]
    public void A_document_with_a_corrupt_macro_project_keeps_its_own_text()
    {
        // A Word 97 file whose macro storage cannot be parsed must still be indexed (see LegacyOfficeExtractorTests for the plain case).
        string p = Path.Combine(Fixtures, "..", "doc", "SampleDoc.doc");
        if (!File.Exists(p)) return;
        ExtractedDocument d = StructuredDocumentExtractor.Extract(p);
        Assert.Contains("I am a test document", d.FullText);
        Assert.DoesNotContain("VBA macro", d.Note ?? "");
    }

    [Fact]
    public void The_decompressor_rejects_data_that_is_not_a_compressed_vba_stream() =>
        Assert.Throws<InvalidDataException>(() => VbaProjectReader.Decompress(new byte[] { 0x02, 1, 2, 3 }));
}
