namespace PaRuX.Server.AgentSkills;

public sealed record AttachedDataSource(string Name, string Kind, long SizeBytes);

/// <summary>
/// The only files the read-only SQL skill can see: SQLite and CSV/TSV files the desktop owner attached on purpose.
/// Attaching copies the file into one PaRuX-owned folder, so the model can never name a path, only a plain file
/// name from this list. A name is resolved inside that folder and refused when it is not a plain name, is a
/// reparse point/symlink, or does not have a supported extension.
/// </summary>
public sealed class AttachedDataSources
{
    public const long MaxSqliteBytes = 512L * 1024 * 1024;
    public const long MaxCsvBytes = 10L * 1024 * 1024;
    private static readonly string[] SqliteExtensions = { ".sqlite", ".sqlite3", ".db" };
    private static readonly string[] CsvExtensions = { ".csv", ".tsv" };

    private readonly string _root;
    private readonly object _gate = new();

    public AttachedDataSources() : this(Path.Combine(ServerState.StateDirectory, "agent-data")) { }
    public AttachedDataSources(string root) => _root = Path.GetFullPath(root);

    public string Root => _root;

    public static string? KindOf(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (SqliteExtensions.Contains(ext)) return "sqlite";
        if (CsvExtensions.Contains(ext)) return "csv";
        return null;
    }

    public IReadOnlyList<AttachedDataSource> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<AttachedDataSource>();
        var result = new List<AttachedDataSource>();
        foreach (string file in Directory.EnumerateFiles(_root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(200))
        {
            string name = Path.GetFileName(file);
            string? kind = KindOf(name);
            var info = new FileInfo(file);
            if (kind == null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            result.Add(new AttachedDataSource(name, kind, info.Length));
        }
        return result;
    }

    /// <summary>Resolves a plain file name to its full path, or throws <see cref="InvalidDataException"/> with a reason a person can read.</summary>
    public string Resolve(string? name, out string kind)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (!PaRuXPathSafety.IsSimpleFileName(trimmed))
            throw new InvalidDataException("The data source must be the plain file name of an attached file, not a path.");
        kind = KindOf(trimmed) ?? throw new InvalidDataException("Only attached SQLite (.sqlite, .sqlite3, .db) and CSV/TSV files can be queried.");
        string full = Path.GetFullPath(Path.Combine(_root, trimmed));
        if (!string.Equals(Path.GetDirectoryName(full), _root.TrimEnd(Path.DirectorySeparatorChar), PaRuXPathSafety.PathComparison))
            throw new InvalidDataException("The data source is outside the attached data folder.");
        if (!File.Exists(full)) throw new InvalidDataException("No attached data source is named '" + trimmed + "'.");
        var info = new FileInfo(full);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Linked files cannot be queried.");
        long limit = kind == "sqlite" ? MaxSqliteBytes : MaxCsvBytes;
        if (info.Length > limit) throw new InvalidDataException("The data source is larger than the " + (limit / 1024 / 1024) + " MB limit.");
        return full;
    }

    /// <summary>Copies a file the owner chose into the attached folder (never overwriting) and returns the stored name.</summary>
    public AttachedDataSource Attach(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathRooted(sourcePath))
            throw new InvalidDataException("An absolute file path is required.");
        string full = Path.GetFullPath(sourcePath);
        var info = new FileInfo(full);
        if (!info.Exists) throw new InvalidDataException("That file does not exist.");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Linked files cannot be attached.");
        string name = Path.GetFileName(full);
        string kind = KindOf(name) ?? throw new InvalidDataException("Only SQLite (.sqlite, .sqlite3, .db) and CSV/TSV files can be attached.");
        long limit = kind == "sqlite" ? MaxSqliteBytes : MaxCsvBytes;
        if (info.Length > limit) throw new InvalidDataException("The file is larger than the " + (limit / 1024 / 1024) + " MB limit.");
        if (!PaRuXPathSafety.IsSimpleFileName(name)) throw new InvalidDataException("The file name is not supported.");

        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            string baseName = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int n = 1; n <= 1000; n++)
            {
                string stored = baseName + (n == 1 ? string.Empty : " (" + n + ")") + ext;
                string target = Path.Combine(_root, stored);
                try
                {
                    using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                    return new AttachedDataSource(stored, kind, new FileInfo(target).Length);
                }
                catch (IOException) when (File.Exists(target)) { }
            }
            throw new IOException("No available file name.");
        }
    }

    public bool Remove(string name)
    {
        string full;
        try { full = Resolve(name, out _); } catch (InvalidDataException) { return false; }
        lock (_gate) { File.Delete(full); }
        return true;
    }
}
