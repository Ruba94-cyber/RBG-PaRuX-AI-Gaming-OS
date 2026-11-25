using System.Text;

namespace PaRuX.Server.AgentSkills;

/// <summary>
/// Static refusal of anything that is not one read-only SELECT. This is the first of three layers; the
/// connection is also opened read-only and <c>PRAGMA query_only</c> is on, so a bug here still cannot write.
/// The text is tokenised (strings, quoted identifiers and comments are removed) so a keyword that only
/// appears inside a string literal or a comment does not trigger a false refusal and one hidden behind a
/// comment does not slip through.
/// </summary>
public static class SqlStatementGuard
{
    public const int MaxStatementCharacters = 4000;

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "ATTACH", "DETACH", "PRAGMA", "VACUUM",
        "REINDEX", "ANALYZE", "SAVEPOINT", "ROLLBACK", "COMMIT",
        "LOAD_EXTENSION", "READFILE", "WRITEFILE", "FTS3_TOKENIZER", "ZEROBLOB", "RANDOMBLOB"
    };

    /// <summary>Returns null when the statement is acceptable, otherwise the reason it was refused.</summary>
    public static string? Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "A SELECT statement is required.";
        if (sql.Length > MaxStatementCharacters) return "The statement is longer than " + MaxStatementCharacters + " characters.";
        if (sql.Contains('\0')) return "The statement contains a NUL character.";

        string stripped = Strip(sql, out string? error);
        if (error != null) return error;

        stripped = stripped.Trim();
        while (stripped.EndsWith(';')) stripped = stripped[..^1].TrimEnd();
        if (stripped.Length == 0) return "A SELECT statement is required.";
        if (stripped.Contains(';')) return "Only one statement is allowed.";

        string[] words = Words(stripped);
        if (words.Length == 0) return "A SELECT statement is required.";

        int startIndex = 0;
        if (words[0].Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            startIndex = 1;
            if (words.Length >= 3
                && words[1].Equals("QUERY", StringComparison.OrdinalIgnoreCase)
                && words[2].Equals("PLAN", StringComparison.OrdinalIgnoreCase))
                startIndex = 3;
            if (startIndex >= words.Length)
                return "EXPLAIN must be followed by a read-only SELECT statement.";
        }

        string first = words[startIndex];
        if (!first.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("WITH", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("VALUES", StringComparison.OrdinalIgnoreCase))
            return "Only read-only SELECT statements are allowed (this one starts with " + first.ToUpperInvariant() + ").";

        foreach (string word in words)
            if (Forbidden.Contains(word))
                return "The keyword or function " + word.ToUpperInvariant() + " is not allowed in a read-only query.";
        return null;
    }

    /// <summary>Removes comments and the contents of string literals and quoted identifiers, keeping quote marks as placeholders.</summary>
    private static string Strip(string sql, out string? error)
    {
        error = null;
        var sb = new StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { error = "Unterminated comment."; return string.Empty; }
                i = end + 2;
                sb.Append(' ');
            }
            else if (c is '\'' or '"' or '`')
            {
                int j = i + 1;
                bool closed = false;
                while (j < sql.Length)
                {
                    if (sql[j] == c)
                    {
                        if (j + 1 < sql.Length && sql[j + 1] == c) { j += 2; continue; }
                        closed = true; break;
                    }
                    j++;
                }
                if (!closed) { error = "Unterminated quoted text."; return string.Empty; }
                sb.Append(c == '\'' ? " 'x' " : " q ");
                i = j + 1;
            }
            else if (c == '[')
            {
                int end = sql.IndexOf(']', i + 1);
                if (end < 0) { error = "Unterminated bracketed identifier."; return string.Empty; }
                sb.Append(" q ");
                i = end + 1;
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }

    private static string[] Words(string stripped)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (char c in stripped)
        {
            if (char.IsLetterOrDigit(c) || c == '_') current.Append(c);
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words.ToArray();
    }
}
