using System.Linq;
using System.Text;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Side-by-side diff model used by every change-set review screen.</summary>
public sealed class GameModDiffTests
{
    [Fact]
    public void Identical_text_has_no_changed_lines_and_no_rows_to_review()
    {
        var (rows, changed) = GameModDiff.Compute("a\nb\n", "a\nb\n");
        Assert.Equal(0, changed);
        Assert.Empty(rows);
    }

    [Fact]
    public void A_single_value_change_is_one_changed_row_with_both_sides_and_line_numbers()
    {
        var (rows, changed) = GameModDiff.Compute("[v]\nfov=90\nvsync=1\n", "[v]\nfov=110\nvsync=1\n");
        Assert.Equal(1, changed);
        var row = Assert.Single(rows, r => r.Kind == "changed");
        Assert.Equal((2, "fov=90", 2, "fov=110"), (row.OldLine, row.OldText, row.NewLine, row.NewText));
        Assert.Equal(2, rows.Count(r => r.Kind == "same")); // context above and below
    }

    [Fact]
    public void Added_and_removed_lines_use_one_sided_rows()
    {
        var (rows, changed) = GameModDiff.Compute("a\nb\nc\n", "a\nc\nd\n");
        Assert.Equal(2, changed);
        Assert.Contains(rows, r => r.Kind == "removed" && r.OldText == "b" && r.NewLine == 0);
        Assert.Contains(rows, r => r.Kind == "added" && r.NewText == "d" && r.OldLine == 0);
    }

    [Fact]
    public void A_new_file_is_all_added_lines()
    {
        var (rows, changed) = GameModDiff.Compute(null, "x=1\ny=2\n");
        Assert.Equal(2, changed);
        Assert.All(rows, r => Assert.Equal("added", r.Kind));
    }

    [Fact]
    public void Line_endings_do_not_create_noise()
    {
        var (rows, changed) = GameModDiff.Compute("a\r\nb\r\n", "a\nb\n");
        Assert.Equal(0, changed);
        Assert.Empty(rows);
    }

    [Fact]
    public void Long_unchanged_runs_collapse_into_context_and_one_gap_row()
    {
        string before = string.Join("\n", Enumerable.Range(1, 40).Select(i => "line" + i));
        string after = before.Replace("line20", "LINE20");
        var (rows, changed) = GameModDiff.Compute(before, after);
        Assert.Equal(1, changed);
        Assert.Equal(2, rows.Count(r => r.Kind == "gap"));
        Assert.Equal(7, rows.Count(r => r.Kind != "gap")); // 3 context + changed + 3 context
        Assert.Equal("16", rows.First(r => r.Kind == "gap").OldText); // 16 unchanged lines before the context
    }

    [Fact]
    public void A_huge_rewrite_degrades_to_removed_plus_added_without_unbounded_memory()
    {
        string before = string.Join("\n", Enumerable.Range(0, 3000).Select(i => "a" + i));
        string after = string.Join("\n", Enumerable.Range(0, 3000).Select(i => "b" + i));
        var (rows, changed) = GameModDiff.Compute(before, after);
        Assert.Equal(3000, changed);
        Assert.Equal(3000, rows.Count(r => r.Kind == "changed"));
    }

    [Fact]
    public void Diff_is_deterministic()
    {
        var a = GameModDiff.Compute("1\n2\n3\n4\n", "1\nX\n3\nY\n");
        var b = GameModDiff.Compute("1\n2\n3\n4\n", "1\nX\n3\nY\n");
        Assert.Equal(a.Rows, b.Rows);
    }
}
