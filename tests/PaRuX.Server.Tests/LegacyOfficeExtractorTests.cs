using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Outlook .msg, Word 97-2003 .doc and PowerPoint 97-2003 .ppt. Real files come from Apache POI's public test corpus (Apache-2.0),
/// downloaded to a local folder (PARUX_OFFICE_FIXTURES) and never committed; each test is skipped when its file is absent (CI).
/// Expected contents were read from the files with an independent parser (olefile), not from PaRuX.
/// </summary>
public sealed class LegacyOfficeExtractorTests
{
    private static readonly string Fixtures = Environment.GetEnvironmentVariable("PARUX_OFFICE_FIXTURES") ?? @"C:\Users\DTS\Desktop\Parux\fixtures";

    private static ExtractedDocument? Load(string relative, string extractor)
    {
        string path = Path.Combine(Fixtures, relative);
        if (!File.Exists(path)) return null;
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Equal(extractor, d.Extractor);
        Assert.Equal(DocumentTier.Structured, d.Tier);
        Assert.True(d.Segments.Count > 0, "no segments for " + relative);
        string outDir = Environment.GetEnvironmentVariable("PARUX_CAN_PROBE_OUT") ?? "";
        if (outDir.Length > 0)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, relative.Replace('\\', '_').Replace('/', '_') + ".index.txt"),
                $"segments: {d.Segments.Count}; truncated: {d.Truncated}; note: {d.Note}\n\n" + string.Join("\n-----\n", d.Segments.Select(s => "[" + s.Position.Describe() + "]\n" + s.Text)));
        }
        return d;
    }

    [Fact]
    public void Word_97_text_pages_and_fonts_description_are_read()
    {
        ExtractedDocument? d = Load(@"doc\SampleDoc.doc", "doc97"); if (d == null) return;
        Assert.Contains("I am a test document", d.FullText);
        Assert.Contains("This is page two", d.FullText);
        Assert.Contains("It’s Arial Black in 16 point", d.FullText);
    }

    [Fact]
    public void Word_97_with_tabs_between_cells_is_read()
    {
        ExtractedDocument? d = Load(@"doc\cap_hello.doc", "doc97"); if (d == null) return;
        Assert.Contains("One paragraph is ok", d.FullText);
        Assert.Contains("Second paragraph is skipped", d.FullText);
    }

    [Fact]
    public void A_long_Word_97_document_is_split_into_positioned_sections()
    {
        ExtractedDocument? d = Load(@"doc\Bug53380_3.doc", "doc97"); if (d == null) return;
        Assert.True(d.Segments.Count > 10);
        Assert.Contains("MEDICINES CONTROL AGENCY", d.FullText);
        Assert.All(d.Segments, s => Assert.Contains("section", s.Position.Heading));
    }

    [Fact]
    public void PowerPoint_97_follows_the_live_revision_and_attaches_speaker_notes_to_their_slide()
    {
        ExtractedDocument? d = Load(@"ppt\basic_test_ppt_file.ppt", "ppt97"); if (d == null) return;
        Assert.Null(d.Note);                                                  // the edit history was followed, no fallback
        string t = d.FullText;
        Assert.Contains("This is a test title", t);
        Assert.Contains("This is the title on page 2", t);
        Assert.Contains("speaker notes of slide 1", t);
        Assert.Contains("These are the notes for page 1", t);
        Assert.Contains("These are the notes on page two", t);
        Assert.DoesNotContain("Click to edit Master", t);                    // template text is not content
        Assert.Equal(2, d.Segments.Count(s => s.Position.Heading!.Contains(" slide ") && !s.Position.Heading.Contains("notes")));
    }

    [Fact]
    public void A_larger_PowerPoint_97_deck_keeps_slide_order()
    {
        ExtractedDocument? d = Load(@"ppt\ParagraphStylesShorterThanCharStyles.ppt", "ppt97"); if (d == null) return;
        Assert.Contains("ROMANCE: AN ANALYSIS", d.Segments[0].Text);
        Assert.Contains("AGENDA", d.Segments[1].Text);
    }

    [Fact]
    public void Outlook_message_headers_and_body_are_read()
    {
        ExtractedDocument? d = Load(@"msg\simple_test_msg.msg", "msg"); if (d == null) return;
        string t = d.FullText;
        Assert.Contains("Email message: test message", t);
        Assert.Contains("From: Travis Ferguson <travis@overwrittenstack.com>", t);
        Assert.Contains("To: travis@overwrittenstack.com", t);
        Assert.Contains("Date: 2007-07-06", t);
        Assert.Contains("This is a test message.", t);
    }

    [Fact]
    public void Outlook_attachments_are_indexed_with_their_own_text_and_a_traceable_heading()
    {
        ExtractedDocument? d = Load(@"msg\attachment_test_msg.msg", "msg"); if (d == null) return;
        Assert.Contains(d.Segments, s => s.Position.Heading!.Contains("attachment pj1.txt") && s.Text.Contains("école"));
        Assert.Contains(d.Segments, s => s.Position.Heading!.Contains("attachment test-unicode.doc") && s.Text.Contains("copyright"));
    }

    [Fact]
    public void Outlook_text_stored_in_a_legacy_code_page_is_decoded_with_the_codepage_the_message_declares()
    {
        ExtractedDocument? d = Load(@"msg\chinese-traditional.msg", "msg"); if (d == null) return;
        Assert.Contains("格式測試", d.FullText);
        Assert.Contains("中文測試", d.FullText);
    }

    [Fact]
    public void Files_that_only_carry_the_extension_are_not_misread()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-legacy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in new[] { "fake.doc", "fake.ppt", "fake.msg" })
            {
                string p = Path.Combine(dir, name); File.WriteAllText(p, "plain text pretending to be binary");
                ExtractedDocument d = StructuredDocumentExtractor.Extract(p);
                Assert.NotEqual(DocumentTier.Structured, d.Tier);
            }
            // a truncated compound file is reported, never crashes the indexer
            string cut = Path.Combine(dir, "cut.msg"); File.WriteAllBytes(cut, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 });
            Assert.Equal(DocumentTier.MetadataOnly, StructuredDocumentExtractor.Extract(cut).Tier);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
