using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PaRuX.Server.AgentSkills;

public sealed class SqlQueryResult
{
    public string Source { get; init; } = string.Empty;
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; } = Array.Empty<IReadOnlyList<string?>>();
    public bool Truncated { get; init; }
    public string? TruncatedReason { get; init; }
}

/// <summary>
/// Read-only SQL over files the owner attached (see <see cref="AttachedDataSources"/>). Defence in depth:
/// <see cref="SqlStatementGuard"/> refuses anything but one SELECT, SQLite files are opened read-only with
/// <c>PRAGMA query_only</c>, CSV files are loaded into a private in-memory database that is also query_only, and
/// every run is bounded by a row cap, a cell cap, an output-size cap and a deadline enforced inside SQLite
/// (so a runaway join stops, not just the caller's wait). Cancellation uses the same interrupt.
/// </summary>
public sealed class ReadOnlySqlQueryService
{
    public const int DefaultMaxRows = 200;
    public const int HardMaxRows = 1000;
    public const int MaxCellCharacters = 200;
    public const int MaxOutputCharacters = 16_000;
    public const int MaxCsvRows = 100_000;
    public const int MaxCsvColumns = 256;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly AttachedDataSources _sources;
    public ReadOnlySqlQueryService(AttachedDataSources sources) => _sources = sources;

    public AttachedDataSources Sources => _sources;

    /// <summary>Tables and columns of one attached source (a CSV is exposed as the single table "data").</summary>
    public string DescribeSchema(string sourceName, CancellationToken ct)
    {
        string path = _sources.Resolve(sourceName, out string kind);
        using SqliteConnection connection = Open(path, kind, ct);
        var sb = new StringBuilder();
        var tables = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT 200";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        foreach (string table in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name, type FROM pragma_table_info($t)";
            command.Parameters.AddWithValue("$t", table);
            using SqliteDataReader reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read() && columns.Count < 200) columns.Add(reader.GetString(0) + " " + reader.GetString(1));
            sb.Append(table).Append('(').Append(string.Join(", ", columns)).AppendLine(")");
        }
        return sb.Length == 0 ? "No tables found." : sb.ToString().TrimEnd();
    }

    public SqlQueryResult Query(string sourceName, string sql, int maxRows, TimeSpan? timeout, CancellationToken ct)
    {
        string? refusal = SqlStatementGuard.Validate(sql);
        if (refusal != null) throw new InvalidDataException(refusal);
        string path = _sources.Resolve(sourceName, out string kind);
        int cap = Math.Clamp(maxRows <= 0 ? DefaultMaxRows : maxRows, 1, HardMaxRows);
        TimeSpan limit = timeout ?? DefaultTimeout;
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(60)) limit = DefaultTimeout;
        DateTime deadline = DateTime.UtcNow + limit;

        using SqliteConnection connection = Open(path, kind, ct);
        bool timedOut = false;
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ =>
        {
            if (ct.IsCancellationRequested) return 1;
            if (DateTime.UtcNow >= deadline) { timedOut = true; return 1; }
            return 0;
        }, null);

        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            using SqliteDataReader reader = command.ExecuteReader();
            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            var rows = new List<IReadOnlyList<string?>>();
            int characters = columns.Sum(c => c.Length);
            bool truncated = false;
            string? reason = null;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= cap) { truncated = true; reason = "row cap of " + cap + " reached"; break; }
                var cells = new List<string?>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string? cell = Cell(reader, i);
                    characters += cell?.Length ?? 4;
                    cells.Add(cell);
                }
                if (characters > MaxOutputCharacters && rows.Count > 0) { truncated = true; reason = "output size cap reached"; break; }
                rows.Add(cells);
            }
            return new SqlQueryResult { Source = sourceName, Columns = columns, Rows = rows, Truncated = truncated, TruncatedReason = reason };
        }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (SqliteException) when (timedOut) { throw new TimeoutException("The query exceeded the " + (int)limit.TotalSeconds + " second limit and was stopped."); }
        catch (SqliteException ex) { throw new InvalidDataException("SQL error: " + ex.Message); }
    }

    public static string ToJson(SqlQueryResult result) => JsonSerializer.Serialize(new
    {
        source = result.Source,
        columns = result.Columns,
        rows = result.Rows,
        rowCount = result.Rows.Count,
        truncated = result.Truncated,
        truncatedReason = result.TruncatedReason
    });

    private static string? Cell(SqliteDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return null;
        object value = reader.GetValue(i);
        string text = value switch
        {
            byte[] blob => "<blob " + blob.Length + " bytes>",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
        return text.Length > MaxCellCharacters ? text[..MaxCellCharacters] + "..." : text;
    }

    private static SqliteConnection Open(string path, string kind, CancellationToken ct)
    {
        SqliteConnection connection;
        if (kind == "sqlite")
        {
            if (!HasSqliteHeader(path)) throw new InvalidDataException("The file is not a SQLite database.");
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString());
            connection.Open();
        }
        else
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ":memory:", Pooling = false
            }.ToString());
            connection.Open();
            try { LoadCsv(connection, path, ct); }
            catch { connection.Dispose(); throw; }
        }
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = ON";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private static bool HasSqliteHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] header = new byte[16];
            return stream.Read(header, 0, 16) == 16 && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
        }
        catch (IOException) { return false; }
    }

    private static void LoadCsv(SqliteConnection connection, string path, CancellationToken ct)
    {
        char delimiter = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : '\0';
        List<List<string>> records = CsvText.Parse(File.ReadAllText(path, Encoding.UTF8), ref delimiter, MaxCsvRows + 1, MaxCsvColumns, ct);
        if (records.Count == 0) throw new InvalidDataException("The CSV file is empty.");
        List<string> header = records[0];
        List<string> names = CsvText.ColumnNames(header);
        List<List<string>> data = records.Skip(1).ToList();
        string[] types = new string[names.Count];
        for (int c = 0; c < names.Count; c++) types[c] = CsvText.InferType(data, c);

        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TABLE data (" + string.Join(", ", names.Select((n, i) => "\"" + n + "\" " + types[i])) + ")";
            create.ExecuteNonQuery();
        }
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO data VALUES (" + string.Join(", ", names.Select((_, i) => "$p" + i)) + ")";
            var parameters = names.Select((_, i) => insert.Parameters.Add("$p" + i, SqliteType.Text)).ToArray();
            foreach (List<string> row in data)
            {
                ct.ThrowIfCancellationRequested();
                for (int c = 0; c < names.Count; c++)
                {
                    string? cell = c < row.Count ? row[c] : null;
                    parameters[c].Value = CsvText.Convert(cell, types[c]) ?? (object)DBNull.Value;
                }
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }
}

/// <summary>Small RFC 4180 reader plus column-name and type inference helpers (kept separate so they are unit-testable).</summary>
internal static class CsvText
{
    public static List<List<string>> Parse(string text, ref char delimiter, int maxRecords, int maxColumns, CancellationToken ct)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (delimiter == '\0')
        {
            string firstLine = text.Split('\n', 2)[0];
            delimiter = new[] { ',', ';', '\t', '|' }.OrderByDescending(d => firstLine.Count(ch => ch == d)).First();
        }
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) { quoted = true; any = true; }
            else if (c == delimiter) { Push(); }
            else if (c == '\r') { }
            else if (c == '\n') { Push(); EndRow(); }
            else { field.Append(c); any = true; }
        }
        if (field.Length > 0 || row.Count > 0 || any) { Push(); EndRow(); }
        return records;

        void Push()
        {
            if (row.Count >= maxColumns) throw new InvalidDataException("The CSV file has more than " + maxColumns + " columns.");
            row.Add(field.ToString()); field.Clear(); any = false;
        }
        void EndRow()
        {
            if (row.Count == 1 && row[0].Length == 0) { row = new List<string>(); return; }
            records.Add(row); row = new List<string>();
            if ((records.Count & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (records.Count > maxRecords) throw new InvalidDataException("The CSV file has more than " + (maxRecords - 1) + " rows.");
        }
    }

    public static List<string> ColumnNames(List<string> header)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Count; i++)
        {
            var clean = new StringBuilder();
            foreach (char c in header[i].Trim())
                clean.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            string name = clean.ToString().Trim('_');
            if (name.Length == 0) name = "c" + (i + 1);
            if (name.Length > 64) name = name[..64];
            string unique = name;
            for (int n = 2; !seen.Add(unique); n++) unique = name + "_" + n;
            names.Add(unique);
        }
        return names;
    }

    public static string InferType(List<List<string>> data, int column)
    {
        bool allInt = true, allReal = true, any = false;
        foreach (List<string> row in data)
        {
            if (column >= row.Count || string.IsNullOrWhiteSpace(row[column])) continue;
            any = true;
            string v = row[column].Trim();
            if (allInt && !long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) allInt = false;
            if (!allInt && allReal && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) allReal = false;
            if (!allInt && !allReal) break;
        }
        return !any ? "TEXT" : allInt ? "INTEGER" : allReal ? "REAL" : "TEXT";
    }

    public static object? Convert(string? cell, string type)
    {
        if (cell == null || (cell.Length == 0 && type != "TEXT")) return null;
        string v = cell.Trim();
        return type switch
        {
            "INTEGER" => long.Parse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            "REAL" => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => cell
        };
    }
}
