using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Line diff for the side-by-side review of a game-mod change set. Pure and deterministic:
/// common prefix/suffix are trimmed, the middle is aligned with an LCS table (bounded; a very
/// large rewrite degrades to "all removed, all added" instead of using unbounded memory), and
/// runs of unchanged lines are collapsed to a few lines of context plus one gap row.
/// </summary>
internal static class GameModDiff
{
    internal const int ContextLines = 3;
    private const long MaxLcsCells = 4_000_000;

    internal static (IReadOnlyList<GameModDiffRowDto> Rows, int ChangedLines) Compute(
        string? before, string after, int context = ContextLines)
    {
        string[] a = Split(before ?? "");
        string[] b = Split(after);
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix &&
               a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        var ops = new List<(char Op, int A, int B)>(); // ' ' same, '-' removed (index in a), '+' added (index in b)
        for (int i = 0; i < prefix; i++) ops.Add((' ', i, i));
        AlignMiddle(a, b, prefix, a.Length - suffix, prefix, b.Length - suffix, ops);
        for (int i = suffix; i > 0; i--) ops.Add((' ', a.Length - i, b.Length - i));

        var rows = new List<GameModDiffRowDto>();
        int changed = 0, p = 0;
        while (p < ops.Count)
        {
            if (ops[p].Op == ' ')
            {
                rows.Add(new("same", ops[p].A + 1, a[ops[p].A], ops[p].B + 1, b[ops[p].B]));
                p++;
                continue;
            }
            var removed = new List<int>();
            var added = new List<int>();
            while (p < ops.Count && ops[p].Op != ' ')
            {
                if (ops[p].Op == '-') removed.Add(ops[p].A); else added.Add(ops[p].B);
                p++;
            }
            changed += Math.Max(removed.Count, added.Count);
            int paired = Math.Min(removed.Count, added.Count);
            for (int k = 0; k < paired; k++)
                rows.Add(new("changed", removed[k] + 1, a[removed[k]], added[k] + 1, b[added[k]]));
            for (int k = paired; k < removed.Count; k++)
                rows.Add(new("removed", removed[k] + 1, a[removed[k]], 0, ""));
            for (int k = paired; k < added.Count; k++)
                rows.Add(new("added", 0, "", added[k] + 1, b[added[k]]));
        }
        return (Collapse(rows, context), changed);
    }

    private static string[] Split(string text)
    {
        if (text.Length == 0) return [];
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return normalized.Split('\n');
    }

    private static void AlignMiddle(string[] a, string[] b, int aStart, int aEnd, int bStart, int bEnd,
        List<(char Op, int A, int B)> ops)
    {
        int n = aEnd - aStart, m = bEnd - bStart;
        if (n == 0 || m == 0 || (long)(n + 1) * (m + 1) > MaxLcsCells)
        {
            for (int i = aStart; i < aEnd; i++) ops.Add(('-', i, -1));
            for (int j = bStart; j < bEnd; j++) ops.Add(('+', -1, j));
            return;
        }
        var table = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                table[i, j] = a[aStart + i] == b[bStart + j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[aStart + x] == b[bStart + y]) { ops.Add((' ', aStart + x, bStart + y)); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) { ops.Add(('-', aStart + x, -1)); x++; }
            else { ops.Add(('+', -1, bStart + y)); y++; }
        }
        for (; x < n; x++) ops.Add(('-', aStart + x, -1));
        for (; y < m; y++) ops.Add(('+', -1, bStart + y));
    }

    private static IReadOnlyList<GameModDiffRowDto> Collapse(List<GameModDiffRowDto> rows, int context)
    {
        var keep = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == "same") continue;
            for (int k = Math.Max(0, i - context); k <= Math.Min(rows.Count - 1, i + context); k++) keep[k] = true;
        }
        var result = new List<GameModDiffRowDto>();
        int skipped = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (keep[i])
            {
                if (skipped > 0) result.Add(Gap(skipped));
                skipped = 0;
                result.Add(rows[i]);
            }
            else skipped++;
        }
        if (skipped > 0 && result.Count > 0) result.Add(Gap(skipped));
        return result;
    }

    private static GameModDiffRowDto Gap(int lines) =>
        new("gap", 0, lines.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, "");
}
