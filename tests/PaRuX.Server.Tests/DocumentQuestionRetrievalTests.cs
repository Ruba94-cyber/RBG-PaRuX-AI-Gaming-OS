using System.Text;
using PaRuX.Server;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regressions found by asking real questions about CAN logs and a DBC on the Corporate package: short identifiers were not
/// recognised, a file name in the question hijacked the lookup, and dense hexadecimal passages overflowed a 4,096-token window.
/// </summary>
public sealed class DocumentQuestionRetrievalTests
{
    [Theory]
    [InlineData("Che segnali contiene il messaggio ETC7 nel DBC", "ETC7")]
    [InlineData("quali DTC porta il messaggio DM1 del veicolo", "DM1")]
    [InlineData("Quanti frame ha l'ID 0x0CF00400x e a che riga compare", "0x0CF00400x")]
    [InlineData("what is the cycle time of EEC3 in this database", "EEC3")]
    public void Short_automotive_identifiers_are_exact_terms(string query, string expected) =>
        Assert.Contains(expected, KnowledgeIndex.ExactTerms(query));

    [Fact]
    public void A_file_name_inside_a_question_is_scoping_not_an_identifier_lookup()
    {
        var terms = KnowledgeIndex.ExactTerms("Nel file CAN-20260730-123749.asc quanti frame ha l'ID 0x0CF00400x");
        Assert.Contains("0x0CF00400x", terms);
        Assert.DoesNotContain(terms, t => t.EndsWith(".asc", StringComparison.OrdinalIgnoreCase));
        // the bare name of a file is still a lookup
        Assert.Contains("report.pdf", KnowledgeIndex.ExactTerms("report.pdf"));
    }

    [Fact]
    public void Ordinary_questions_still_have_no_exact_terms() =>
        Assert.Empty(KnowledgeIndex.ExactTerms("how do I calibrate the controller"));

    private static KnowledgeHit Hit(string file, string text, long id = 1) =>
        new(id, @"C:\x\" + file, file, text, new DocumentPosition(Heading: "h"), 1.0, "hybrid");

    [Fact]
    public void A_question_naming_a_file_keeps_only_that_files_passages()
    {
        var hits = new[] { Hit("A.asc", "x", 1), Hit("DTC DMS Can Recording.asc", "y", 2), Hit("B.dbc", "z", 3) };
        var kept = DocumentChatPlanner.KeepNamedFiles("nel file DTC DMS Can Recording.asc quali DTC", hits);
        Assert.Equal(2, Assert.Single(kept).ChunkId);
        // no file named, or a name that matches nothing: nothing is filtered out
        Assert.Equal(3, DocumentChatPlanner.KeepNamedFiles("quali DTC", hits).Count);
        Assert.Equal(3, DocumentChatPlanner.KeepNamedFiles("nel file other.asc quali DTC", hits).Count);
    }

    [Fact]
    public void Dense_hex_passages_are_dropped_to_fit_the_window_but_prose_is_not()
    {
        string hex = string.Join(" ", Enumerable.Range(0, 280).Select(i => (i * 7 % 256).ToString("X2")));        // ~840 chars
        string prose = string.Concat(Enumerable.Repeat("the engine coolant temperature sensor reports a plausible value ", 14));   // ~900 chars
        int budgetChars = ContextEngineService.CharactersForContextWindow(4096, 1024);                         // 5,430 on the CPU profile

        var denseHits = Enumerable.Range(1, 6).Select(i => Hit("t.asc", hex, i)).ToList();
        var proseHits = Enumerable.Range(1, 6).Select(i => Hit("t.pdf", prose, i)).ToList();

        var fittedDense = ContextEngineService.FitPassagesToBudget(denseHits, budgetChars);
        var fittedProse = ContextEngineService.FitPassagesToBudget(proseHits, budgetChars);

        Assert.InRange(fittedDense.Count, 1, 3);
        Assert.Equal(6, fittedProse.Count);
        Assert.True(fittedDense.Sum(h => ContextEngineService.EstimatePassageTokens(h.Text)) <= budgetChars / 2.5 + 1);
        Assert.Equal(1, fittedDense[0].ChunkId);                                                               // best-ranked is kept first
    }

    [Fact]
    public void A_single_oversized_passage_is_shortened_not_refused()
    {
        string hex = string.Join(" ", Enumerable.Range(0, 2000).Select(i => (i % 256).ToString("X2")));
        var fitted = ContextEngineService.FitPassagesToBudget(new[] { Hit("t.asc", hex) }, 1_000);
        Assert.Single(fitted);
        Assert.True(fitted[0].Text.Length < hex.Length);
        Assert.True(fitted[0].Text.Length >= 200);
    }

    [Fact]
    public void Can_trace_segments_are_small_and_each_names_its_id_and_file()
    {
        var sb = new StringBuilder("date x\nbase hex  timestamps absolute\n");
        for (int i = 0; i < 400; i++)
            sb.Append((i * 0.01).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" 1 18FE4A03x Rx d 8 ").Append((i & 255).ToString("X2")).Append(" DF 7F FF FD FF FF FF\n");
        string path = Path.Combine(Path.GetTempPath(), "parux-seg-" + Guid.NewGuid().ToString("N") + ".asc");
        try
        {
            File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
            ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
            var idSegments = d.Segments.Where(s => s.Position.Heading!.StartsWith("CAN ID 0x18FE4A03x")).ToList();
            Assert.True(idSegments.Count > 3, "a long change log must be split into several segments");
            Assert.All(idSegments, s =>
            {
                Assert.True(s.Text.Length <= 1000, "segment too large for a small retrieval chunk: " + s.Text.Length);
                Assert.Contains("CAN ID 0x18FE4A03x", s.Text);
                Assert.Contains(Path.GetFileName(path), s.Text);
            });
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}
