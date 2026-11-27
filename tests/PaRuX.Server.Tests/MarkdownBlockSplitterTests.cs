using System.Collections.Generic;
using System.Linq;
using System.Text;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MarkdownBlockSplitterTests
{
    private const string Sample =
        "# Title\n\nIntro paragraph with **bold** text\nspanning two lines.\n\n" +
        "- one\n- two\n  - nested\n1. first\n2. second\n\n" +
        "| Name | Qty |\n|:-----|----:|\n| a | 1 |\n| b | 2 |\n\n" +
        "```csharp\nvar x = 1;\n\nvar y = 2;\n```\n\n" +
        "Para before table\n| H1 | H2 |\n| --- | --- |\n| c | d |\n\n" +
        "## Section\r\nWindows line endings\r\nstill paragraph\r\n\r\n" +
        "```\nunclosed fence that runs to the end\nline 2";

    private static string Describe(IEnumerable<MarkdownBlock> blocks) =>
        string.Join("||", blocks.Select(b => $"{b.Kind}@{b.StartOffset}:{b.Language}:{b.Closed}:" +
            (b.Table != null ? string.Join(";", b.Table.Header) + "/" + b.Table.Rows.Count : string.Join("\n", b.Lines))));

    [Fact]
    public void Splits_expected_block_kinds_in_order()
    {
        var kinds = MarkdownBlockSplitter.Split(Sample).Select(b => b.Kind).ToArray();
        Assert.Equal(new[]
        {
            MarkdownBlockKind.Heading, MarkdownBlockKind.Paragraph, MarkdownBlockKind.List, MarkdownBlockKind.Table,
            MarkdownBlockKind.Code, MarkdownBlockKind.Paragraph, MarkdownBlockKind.Table, MarkdownBlockKind.Heading,
            MarkdownBlockKind.Paragraph, MarkdownBlockKind.Code
        }, kinds);
    }

    [Fact]
    public void Open_code_fence_is_one_unclosed_block_and_closes_when_fence_arrives()
    {
        var open = MarkdownBlockSplitter.Split("text\n\n```py\nprint(1)\nprint(2)");
        Assert.Equal(2, open.Count);
        Assert.Equal(MarkdownBlockKind.Code, open[1].Kind);
        Assert.False(open[1].Closed);
        Assert.Equal("py", open[1].Language);
        Assert.Equal(2, open[1].Lines.Count);

        var closed = MarkdownBlockSplitter.Split("text\n\n```py\nprint(1)\nprint(2)\n```\nafter");
        Assert.True(closed[1].Closed);
        Assert.Equal(MarkdownBlockKind.Paragraph, closed[2].Kind);
    }

    [Fact]
    public void Table_row_growth_keeps_a_single_table_block_and_paragraph_line_becomes_table_retroactively()
    {
        var two = MarkdownBlockSplitter.Split("| A | B |\n| --- | --- |\n| 1 | 2 |");
        var three = MarkdownBlockSplitter.Split("| A | B |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |");
        Assert.Single(two);
        Assert.Single(three);
        Assert.Equal(2, three[0].Table!.Rows.Count);

        // Header row alone is plain paragraph text until the separator arrives.
        Assert.Equal(MarkdownBlockKind.Paragraph, MarkdownBlockSplitter.Split("intro\n| A | B |")[0].Kind);
        var after = MarkdownBlockSplitter.Split("intro\n| A | B |\n| --- | --- |");
        Assert.Equal(new[] { MarkdownBlockKind.Paragraph, MarkdownBlockKind.Table }, after.Select(b => b.Kind).ToArray());
    }

    [Fact]
    public void Consecutive_list_items_form_one_block()
    {
        var blocks = MarkdownBlockSplitter.Split("- a\n- b\n3. c\ntext");
        Assert.Equal(new[] { MarkdownBlockKind.List, MarkdownBlockKind.Paragraph }, blocks.Select(b => b.Kind).ToArray());
        Assert.Equal(3, blocks[0].Lines.Count);
    }

    [Fact]
    public void Stable_prefix_blocks_never_change_as_text_grows_char_by_char()
    {
        // For every prefix of the sample, every block before the open tail (last block, plus the one before it when the last is a paragraph) must already exist unchanged in
        // the split of every longer prefix (that is what makes keeping their UI elements safe).
        List<MarkdownBlock>? previous = null;
        for (int len = 0; len <= Sample.Length; len++)
        {
            var current = MarkdownBlockSplitter.Split(Sample.Substring(0, len));
            int open = previous != null && previous.Count > 0 && previous[^1].Kind == MarkdownBlockKind.Paragraph ? 2 : 1;
            if (previous != null && previous.Count > open)
            {
                string stableBefore = Describe(previous.Take(previous.Count - open));
                string sameInCurrent = Describe(current.Take(previous.Count - open));
                Assert.True(stableBefore == sameInCurrent, $"stable blocks changed at length {len}");
            }
            previous = current;
        }
    }

    [Fact]
    public void Incremental_splitter_matches_full_split_for_every_prefix_and_chunking()
    {
        foreach (int chunk in new[] { 1, 3, 17 })
        {
            var inc = new MarkdownIncrementalSplitter();
            var assembled = new List<MarkdownBlock>();
            for (int len = chunk; ; len += chunk)
            {
                if (len > Sample.Length) len = Sample.Length;
                string text = Sample.Substring(0, len);
                var update = inc.Apply(text);
                if (update.Reset) assembled.Clear();
                assembled = assembled.Take(update.KeptStableBlocks).Concat(update.Blocks).ToList();
                Assert.True(Describe(MarkdownBlockSplitter.Split(text)) == Describe(assembled),
                    $"incremental != full at length {len}, chunk {chunk}");
                if (len == Sample.Length) break;
            }
        }
    }

    [Fact]
    public void Non_append_edit_resets_the_stable_prefix()
    {
        var inc = new MarkdownIncrementalSplitter();
        inc.Apply("# One\n\npara a\n\npara b");
        var update = inc.Apply("# Changed\n\nother");
        Assert.True(update.Reset);
        Assert.Equal(0, update.KeptStableBlocks);
    }

    [Fact]
    public void Simulated_6k_token_stream_parses_in_roughly_linear_time()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 120; i++)
        {
            sb.Append("## Part ").Append(i).Append("\n\nSome explanatory paragraph number ").Append(i).Append(" with several words in it.\n\n");
            sb.Append("| k | v |\n| --- | --- |\n| a | ").Append(i).Append(" |\n| b | 2 |\n\n");
            sb.Append("```cs\nint v").Append(i).Append(" = ").Append(i).Append(";\nConsole.WriteLine(v").Append(i).Append(");\n```\n\n");
            sb.Append("- item one\n- item two\n\n");
        }
        string full = sb.ToString();
        int totalLines = full.Count(c => c == '\n') + 1;

        // ~6k tokens: one update per ~4 chars.
        var inc = new MarkdownIncrementalSplitter();
        long naiveLines = 0;
        int updates = 0;
        for (int len = 4; ; len += 4)
        {
            if (len > full.Length) len = full.Length;
            inc.Apply(full.Substring(0, len));
            naiveLines += full.Substring(0, len).Count(c => c == '\n') + 1;   // what a full re-split per update costs
            updates++;
            if (len == full.Length) break;
        }

        Assert.True(updates > 1500, "stream should be long enough to matter");
        // Linear: each line is scanned a small constant number of times (open tail re-scanned per update, not whole text).
        Assert.True(inc.TotalLinesScanned < totalLines * 40L, $"scanned {inc.TotalLinesScanned} lines for {totalLines} lines of text");
        // And dramatically below the quadratic full re-split.
        Assert.True(inc.TotalLinesScanned * 10 < naiveLines, $"incremental {inc.TotalLinesScanned} vs naive {naiveLines}");
    }
}
