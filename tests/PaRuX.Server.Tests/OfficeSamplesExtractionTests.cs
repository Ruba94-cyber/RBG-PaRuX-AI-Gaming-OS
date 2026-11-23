using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The synthetic Excel/Word/PowerPoint/e-mail/PDF files written by tests/corporate/make-office-samples.py. Every fact the Corporate
/// acceptance run asks about must already be present in the extracted text, otherwise no question could ever be answered.
/// Skipped when the sample folder (PARUX_OFFICE_SAMPLES) has not been generated.
/// </summary>
public sealed class OfficeSamplesExtractionTests
{
    private static readonly string Docs = Path.Combine(Environment.GetEnvironmentVariable("PARUX_OFFICE_SAMPLES") ?? @"C:\Users\DTS\Desktop\Parux\office-samples", "docs");

    private static string? Text(string file, string expectedExtractor)
    {
        string path = Path.Combine(Docs, file);
        if (!File.Exists(path)) return null;
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.True(d.Tier is DocumentTier.Structured or DocumentTier.Text, file + " -> " + d.Tier + " " + d.Note);
        string outDir = Environment.GetEnvironmentVariable("PARUX_CAN_PROBE_OUT") ?? "";
        if (outDir.Length > 0)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "office_" + file + ".index.txt"), $"extractor: {d.Extractor}; segments: {d.Segments.Count}\n\n" +
                string.Join("\n-----\n", d.Segments.Select(s => "[" + s.Position.Describe() + "]\n" + s.Text)));
        }
        return d.FullText;
    }

    [Fact]
    public void Excel_rows_sheets_and_values_are_extracted()
    {
        string? t = Text("Ordini_2026.xlsx", "xlsx"); if (t == null) return;
        Assert.Contains("Rossi Meccanica", t); Assert.Contains("4471", t); Assert.Contains("12480", t); Assert.Contains("2026-12-15", t);
        Assert.Contains("In ritardo", t); Assert.Contains("Soglia sconto", t); Assert.Contains("7,5%", t);
    }

    [Fact]
    public void Word_clauses_and_tables_are_extracted()
    {
        string? t = Text("Contratto_Fornitura.docx", "docx"); if (t == null) return;
        Assert.Contains("36 mesi", t); Assert.Contains("2,5%", t); Assert.Contains("massimo del 10%", t);
        Assert.Contains("Foro di Torino", t); Assert.Contains("ZX-200", t); Assert.Contains("38,90", t);
    }

    [Fact]
    public void PowerPoint_slides_and_speaker_notes_are_extracted()
    {
        string? t = Text("Piano_Progetto.pptx", "pptx"); if (t == null) return;
        Assert.Contains("Progetto Aquila", t); Assert.Contains("30 giugno 2027", t);
        Assert.Contains("450.000", t);                       // only present in the speaker notes
    }

    [Fact]
    public void An_email_and_the_text_of_its_attachment_are_extracted()
    {
        string? t = Text("Verbale_Riunione.eml", "eml"); if (t == null) return;
        Assert.Contains("Laura Bianchi", t); Assert.Contains("Marco Verdi", t); Assert.Contains("20 dicembre 2026", t);
        Assert.Contains("25 Nm", t);                          // only present inside the attached workbook
    }

    [Fact]
    public void PDF_pages_are_extracted()
    {
        string? t = Text("Manuale_Tecnico.pdf", "pdf"); if (t == null) return;
        Assert.Contains("E-417", t); Assert.Contains("sovratemperatura", t); Assert.Contains("1,8 bar", t);
    }
}
