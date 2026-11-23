using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Language-agnostic reader for scripts and source files whose language PaRuX has no dedicated parser for (in-house scripting languages,
/// PLC / CAPL / MATLAB / AutoHotkey / VBScript / Tcl / assembler / HDL ...). It does not know a grammar; it recognises the shapes nearly
/// every language shares: definitions (function / sub / procedure / class / label / #declare ...), dependencies (include / import / using /
/// file="..." / quoted names of other scripts, libraries and data files) and calls to external objects (Object.Method(...)).
/// Output per file: one overview segment (language hints, definitions, dependencies, external calls, header comment) followed by small
/// blocks that each repeat the file name, the line range and the enclosing definition, so a retrieved block always says where it is.
/// A file that is prose or binary is not claimed and falls through to the plain-text reader.
/// </summary>
internal sealed class UniversalScriptExtractor : IDocumentExtractor
{
    public string Name => "script";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>Extensions that are scripts or source in some language: read as scripts whenever the content is text.</summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".esl", ".eslx", ".vbs", ".vba", ".bas", ".cls", ".frm", ".wsf", ".hta", ".ahk", ".au3", ".tcl", ".tk", ".m", ".mlx", ".can", ".cin", ".capl", ".st", ".scl",
        ".awl", ".ld", ".fbd", ".sfc", ".il", ".asm", ".s", ".inc", ".v", ".sv", ".vhd", ".vhdl", ".proto", ".graphql", ".gql", ".tf", ".hcl", ".nix", ".ex", ".exs",
        ".erl", ".hrl", ".hs", ".ml", ".mli", ".clj", ".cljs", ".groovy", ".jl", ".nim", ".zig", ".sol", ".cob", ".cbl", ".f90", ".f", ".for", ".adb", ".ads", ".pas",
        ".pp", ".lpr", ".dpr", ".ino", ".pde", ".awk", ".sed", ".expect", ".rexx", ".rex", ".jcl", ".abap", ".cfg", ".macro", ".mac", ".scr", ".script", ".prg", ".mq4",
        ".mq5", ".pine", ".lsl", ".gd", ".glsl", ".hlsl", ".wgsl", ".cu", ".cl", ".kv", ".qml", ".do", ".ado", ".sas", ".sps", ".nsi", ".nsh", ".iss", ".wxs", ".rc",
        ".def", ".idl", ".odl", ".y", ".l", ".g4", ".bnf", ".ebnf", ".jsonnet", ".cue", ".dhall", ".bicep", ".p", ".i", ".ys", ".sqlx"
    };

    private static readonly Regex[] Definitions =
    {
        R(@"^\s*(?:(?:public|private|friend|static|export|async|global|local|shared|overrides|overridable)\s+)*(?:function|func|fun|fn|sub|procedure|proc|def|method|macro|routine|subroutine|handler|on)\s+(?<n>[A-Za-z_][\w.:$]*)"),
        R(@"^\s*(?:class|module|struct|interface|enum|namespace|package|object|trait|type|program|unit|library|task|block|function_block|function_block_diagram)\s+(?<n>[A-Za-z_][\w.:]*)"),
        R(@"^\s*#\s*(?:define|macro|declare)\s+(?<n>[A-Za-z_][\w.]*)"),
        R(@"^\s*#\s*(?:pragma\s+)?region\s+(?<n>.+?)\s*$"),
        R(@"^\s*(?<n>(?!default\b|case\b|else\b|otherwise\b)[A-Za-z_]\w*)\s*:\s*(?://.*|'.*|;.*|--.*)?$"),
        R(@"^\s*(?:variables|on\s+(?:start|stop|key|message|timer|sysvar|envvar|preStart|preStop)\b)\s*(?<n>[\w.]*)")
    };

    private static readonly Regex[] Dependencies =
    {
        R(@"\b(?:file|path|src|href|source|library|lib|assembly|dll|include|import)\s*=\s*[""'](?<t>[^""']+)[""']"),
        R(@"^\s*#?\s*(?:include|include_once|import|require|require_once|using|use|load|loadlibrary|source|from|call|run|exec|execute|runscript|dofile|loadfile)\b\s*[(\s]\s*[""'<]?(?<t>[\w./\\:$-]+)")
    };

    /// <summary>A quoted token shaped like a file name (optional folders, a name, a short alphabetic extension), whatever the extension: other scripts, libraries, data, resources.</summary>
    private static readonly Regex QuotedFile = R(@"[""'](?<t>(?:[\w.-]+[/\\])*[\w.-]+\.[A-Za-z][A-Za-z0-9]{0,5})[""']");

    private static readonly Regex ExternalCall = R(@"\b(?<o>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\.(?<m>[A-Za-z_]\w*)\s*\(");

    private static readonly Regex CodeLine = R(
        @"^\s*(?:(?:if|then|else|elseif|elif|endif|end|for|foreach|next|while|wend|do|loop|until|repeat|return|break|continue|goto|call|dim|let|set|var|const|function|sub|def|proc|procedure|begin|try|catch|finally|select|case|switch|import|include|using|print|echo|exit|wait|debug|declare|local|global)\b|#|//|/\*|\*/|'|;|--|rem\b|%|[A-Za-z_][\w.\[\]]*\s*(?:=|:=|\+=|-=|\()|\}|\{|.*[;{}]\s*$)");

    public bool CanHandle(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length == 0 && Path.GetFileName(path).IndexOf('.') >= 0) return false;
        if (!new TextFileExtractor().CanHandle(path)) return false;          // known binaries are never read as text
        bool knownExt = ScriptExtensions.Contains(ext);
        if (!Sniff(path, knownExt ? 1 : 3, out bool isText, out double codeShare)) return false;
        if (!isText) return false;
        return knownExt ? codeShare >= 0.25 : codeShare >= 0.6;
    }

    private static bool Sniff(string path, int minStrongLines, out bool isText, out double codeShare)
    {
        isText = false; codeShare = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] sample = new byte[(int)Math.Min(65536, fs.Length)];
            int read = fs.Read(sample, 0, sample.Length);
            if (read == 0) return false;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding? enc = TextFileExtractor.Detect(sample.AsSpan(0, read), fs.Length > read, out int bom);
            if (enc == null) return true;
            isText = true;
            string text = enc.GetString(sample, bom, read - bom);
            int total = 0, code = 0, strong = 0;
            foreach (string raw in text.Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                if (l.Trim().Length == 0) continue;
                if (++total > 300) break;
                if (l.Length > 400) continue;
                if (CodeLine.IsMatch(l)) code++;
                if (l.Contains('=') || l.Contains('(')) strong++;
            }
            codeShare = total == 0 || strong < minStrongLines ? 0 : (double)code / total;
            return true;
        }
        catch (Exception) { return false; }
    }

    private sealed record Def(string Name, int Line);
    private sealed record Dep(string Target, int Line);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] sample = new byte[(int)Math.Min(65536, fs.Length)];
        int read = fs.Read(sample, 0, sample.Length);
        Encoding enc = TextFileExtractor.Detect(sample.AsSpan(0, read), fs.Length > read, out int bom) ?? Encoding.UTF8;
        fs.Position = bom;
        using var reader = new StreamReader(fs, enc, false, 64 * 1024);

        string name = Path.GetFileName(path);
        var sink = new AutomotiveSink(limits);
        string[] folders = (Path.GetDirectoryName(path) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        bool cut = Analyze(reader, name, folders, "Script/source file", limits, ct, sink);
        return sink.Build(path, Name, cut ? "The file is larger than the character limit: only its first part was read." : null);
    }

    /// <summary>
    /// Reads one script from <paramref name="reader"/> and adds its overview and positioned blocks to <paramref name="sink"/>.
    /// Used for script files and for source recovered from inside other files (VBA macro modules of Office documents).
    /// Returns true when a character limit cut the text.
    /// </summary>
    internal static bool Analyze(TextReader reader, string name, string[] parents, string kind, ExtractionLimits limits, CancellationToken ct, AutomotiveSink sink)
    {
        var defs = new List<Def>(); var deps = new List<Dep>(); var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new List<(int No, string Text, string? Def)>();
        var header = new StringBuilder();
        bool inHeader = true; string? currentDef = null; long chars = 0; int lineNo = 0, commentLines = 0; bool truncated = false;
        string commentMark = "";

        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            lineNo++;
            if ((lineNo & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (raw.Length > 2000) raw = raw[..2000];
            chars += raw.Length + 1;
            if (chars > limits.MaxCharacters) { truncated = true; break; }
            string t = raw.Trim();
            if (t.Length == 0) continue;
            bool isComment = t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("'", StringComparison.Ordinal) || t.StartsWith(";", StringComparison.Ordinal) ||
                             t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || (t.StartsWith('#') && !t.StartsWith("#declare", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("#include", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("#define", StringComparison.OrdinalIgnoreCase)) ||
                             t.StartsWith("%", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal);
            if (isComment)
            {
                commentLines++;
                if (commentMark.Length == 0) commentMark = t.Length >= 2 && (t[..2] is "//" or "/*" or "--") ? t[..2] : t[..1];
                if (inHeader && header.Length < 500) header.Append(t.TrimStart('/', '\'', ';', '-', '#', '%', '*', ' ')).Append(' ');
            }
            else inHeader = false;

            foreach (Regex d in Definitions)
            {
                Match m = d.Match(raw);
                if (m.Success && m.Groups["n"].Length > 0) { currentDef = m.Groups["n"].Value.Trim(); defs.Add(new Def(currentDef, lineNo)); break; }
            }
            if (!isComment)
            {
                foreach (Regex d in Dependencies)
                {
                    foreach (Match m in d.Matches(raw)) AddDep(deps, m.Groups["t"].Value, lineNo);
                }
                foreach (Match m in QuotedFile.Matches(raw)) AddDep(deps, m.Groups["t"].Value.Trim(), lineNo);
                foreach (Match m in ExternalCall.Matches(raw))
                {
                    string key = m.Groups["o"].Value + "." + m.Groups["m"].Value;
                    calls[key] = calls.TryGetValue(key, out int c) ? c + 1 : 1;
                }
            }
            lines.Add((lineNo, raw.TrimEnd(), currentDef));
        }

        // ---- overview -------------------------------------------------------------------------------------------------------------
        var ov = new StringBuilder();
        ov.Append(kind).Append(' ').Append(name).Append(" (").Append(lineNo).Append(" lines; ").Append(commentLines).Append(" comment lines");
        if (commentMark.Length > 0) ov.Append("; comment marker ").Append(commentMark);
        ov.AppendLine(")");
        if (parents.Length > 0) ov.Append("Folder: ").AppendLine(string.Join(" / ", parents.TakeLast(3)));
        if (header.Length > 0) ov.Append("Header comment: ").AppendLine(header.ToString().Trim());
        if (defs.Count > 0)
        {
            ov.Append("Definitions (").Append(defs.Count).Append("): ");
            ov.AppendLine(string.Join("; ", defs.Take(60).Select(d => d.Name + " (line " + d.Line + ")")) + (defs.Count > 60 ? "; +" + (defs.Count - 60) + " more" : ""));
        }
        var uniqueDeps = deps.GroupBy(d => d.Target, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (uniqueDeps.Count > 0)
        {
            ov.Append("Uses / imports / references other files (").Append(uniqueDeps.Count).Append("): ");
            ov.AppendLine(string.Join("; ", uniqueDeps.Take(60).Select(d => d.Target + " (line " + d.Line + ")")) + (uniqueDeps.Count > 60 ? "; +" + (uniqueDeps.Count - 60) + " more" : ""));
        }
        if (calls.Count > 0)
        {
            ov.Append("Calls to external objects/functions: ");
            ov.AppendLine(string.Join("; ", calls.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(40).Select(c => c.Key + " x" + c.Value)));
        }
        sink.Add(ov.ToString(), name + " overview");

        // ---- positioned blocks ----------------------------------------------------------------------------------------------------
        const int Max = 700;
        var cur = new StringBuilder(); int first = 0, last = 0; string? blockDef = null;
        void Flush()
        {
            if (cur.Length == 0) return;
            string where = "lines " + first + "-" + last + (blockDef != null ? ", in " + blockDef : "");
            sink.Add("File " + name + " " + where + ":" + Environment.NewLine + cur.ToString().TrimEnd(), name + " " + where);
            cur.Clear();
        }
        foreach (var (no, text, def) in lines)
        {
            if (sink.Full) break;
            if (cur.Length > 0 && (cur.Length + text.Length + 1 > Max || !string.Equals(def, blockDef, StringComparison.Ordinal))) Flush();
            if (cur.Length == 0) { first = no; blockDef = def; }
            cur.AppendLine(text); last = no;
        }
        Flush();

        if (truncated || sink.Truncated) sink.MarkTruncated();
        return truncated;
    }

    private static void AddDep(List<Dep> deps, string target, int line)
    {
        target = target.Trim().TrimEnd('.', ',', ';', ')');
        if (target.Length < 2 || target.Length > 260) return;
        if (target.Contains("://", StringComparison.Ordinal)) return;                        // web addresses are not project files
        deps.Add(new Dep(target, line));
    }
}
