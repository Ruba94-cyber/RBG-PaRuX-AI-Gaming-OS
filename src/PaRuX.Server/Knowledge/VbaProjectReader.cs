using System.IO.Compression;
using System.Text;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Reads the source of VBA macros stored in Office files (MS-OVBA): the "dir" stream lists the modules, every module stream holds its
/// source compressed with the run-length/LZ scheme of section 2.4.1. Source text is only read and indexed, never executed.
/// Works on the OLE project of Office 97-2003 files (".xls", ".doc") and on the "vbaProject.bin" inside ".xlsm/.docm/.pptm" packages.
/// </summary>
internal static class VbaProjectReader
{
    public sealed record Module(string Name, string Source);

    private const int MaxModules = 400;
    private const int MaxDecompressedBytes = 16 * 1024 * 1024;

    /// <summary>All VBA modules of the first VBA project found in the compound file (empty when there is none).</summary>
    public static List<Module> Read(CompoundFile cf)
    {
        var result = new List<Module>();
        foreach (var storage in cf.Entries.Where(e => e.Type == 1 && e.Name.Equals("VBA", StringComparison.OrdinalIgnoreCase)))
        {
            var dirEntry = cf.Child(storage, "dir");
            if (dirEntry == null) continue;
            byte[] dir = Decompress(cf.ReadStream(dirEntry));
            foreach (var (name, streamName, offset, codePage) in ParseDir(dir))
            {
                var stream = cf.Child(storage, streamName);
                if (stream == null) continue;
                byte[] raw = cf.ReadStream(stream);
                if (offset >= raw.Length) continue;
                byte[] src = Decompress(raw.AsSpan((int)offset).ToArray());
                result.Add(new Module(name, Decode(src, codePage)));
                if (result.Count >= MaxModules) break;
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    private static string Decode(byte[] bytes, int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding enc;
        try { enc = Encoding.GetEncoding(codePage); } catch (ArgumentException) { enc = Encoding.GetEncoding(1252); }
        return enc.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>The module list of the decompressed "dir" stream: name, stream name, offset of the compressed source, project code page.</summary>
    private static IEnumerable<(string Name, string Stream, uint Offset, int CodePage)> ParseDir(byte[] d)
    {
        int codePage = 1252, p = 0;
        string name = "", stream = ""; uint offset = 0; bool open = false;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string Text(int at, int len) { try { return Encoding.GetEncoding(codePage).GetString(d, at, len); } catch (ArgumentException) { return Encoding.Latin1.GetString(d, at, len); } }
        while (p + 6 <= d.Length)
        {
            ushort id = BitConverter.ToUInt16(d, p); p += 2;
            if (id == 0x0009) { p += 10; continue; }                                  // PROJECTVERSION has a fixed layout: reserved(4) major(4) minor(2)
            uint size = BitConverter.ToUInt32(d, p); p += 4;
            if (size > d.Length - p) yield break;                                       // lost sync: keep what was read
            switch (id)
            {
                case 0x0003: if (size >= 2) codePage = BitConverter.ToUInt16(d, p); break;
                case 0x0019: name = Text(p, (int)size); open = true; stream = name; offset = 0; break;      // MODULENAME
                case 0x001A: stream = Text(p, (int)size); break;                                            // MODULESTREAMNAME
                case 0x0031: if (size >= 4) offset = BitConverter.ToUInt32(d, p); break;                    // MODULEOFFSET
                case 0x002B: if (open) { yield return (name, stream, offset, codePage); open = false; } break; // module terminator
            }
            p += (int)size;
        }
    }

    /// <summary>MS-OVBA 2.4.1.3 decompression: a signature byte (1) then chunks of up to 4,096 decompressed bytes.</summary>
    internal static byte[] Decompress(byte[] c)
    {
        if (c.Length == 0 || c[0] != 0x01) throw new InvalidDataException("Not a compressed VBA stream.");
        var o = new List<byte>(c.Length * 3);
        int pos = 1;
        while (pos + 2 <= c.Length)
        {
            ushort header = BitConverter.ToUInt16(c, pos);
            int chunkSize = (header & 0x0FFF) + 3;
            bool compressed = (header & 0x8000) != 0;
            int chunkEnd = Math.Min(pos + chunkSize, c.Length), p = pos + 2, chunkStart = o.Count;
            if (!compressed)
            {
                for (; p < chunkEnd && o.Count - chunkStart < 4096; p++) o.Add(c[p]);
            }
            else
            {
                while (p < chunkEnd)
                {
                    byte flags = c[p++];
                    for (int bit = 0; bit < 8 && p < chunkEnd; bit++)
                    {
                        if ((flags & (1 << bit)) == 0) { o.Add(c[p++]); continue; }
                        if (p + 2 > chunkEnd) break;
                        ushort token = BitConverter.ToUInt16(c, p); p += 2;
                        int diff = o.Count - chunkStart;
                        int bitCount = Math.Max(4, diff <= 1 ? 4 : (int)Math.Ceiling(Math.Log2(diff)));
                        int lengthMask = 0xFFFF >> bitCount;
                        int length = (token & lengthMask) + 3;
                        int offset = ((token & ~lengthMask & 0xFFFF) >> (16 - bitCount)) + 1;
                        int from = o.Count - offset;
                        if (from < chunkStart) throw new InvalidDataException("Invalid VBA copy token.");
                        for (int i = 0; i < length; i++) o.Add(o[from + i]);
                    }
                }
            }
            if (o.Count > MaxDecompressedBytes) throw new InvalidDataException("VBA stream is larger than the limit.");
            pos = chunkEnd;
        }
        return o.ToArray();
    }
}

/// <summary>
/// Adds the macros of an Office document to what its normal reader extracted: a document that carries VBA is also a program, and
/// "what does the macro in this workbook do" must be answerable. Macros are only read, never run; the overview names every module so a
/// reader can see at once that the file contains code.
/// </summary>
internal static class MacroAugmenter
{
    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase) { ".xlsm", ".xlam", ".xltm", ".docm", ".dotm", ".pptm", ".potm", ".ppam", ".ppsm" };
    private static readonly HashSet<string> Legacy = new(StringComparer.OrdinalIgnoreCase) { ".xls", ".xlt", ".doc", ".dot" };
    private const long MaxProjectBytes = 32L * 1024 * 1024;

    public static ExtractedDocument Augment(string path, ExtractedDocument doc, ExtractionLimits limits, CancellationToken ct)
    {
        string ext = Path.GetExtension(path);
        if (doc.Tier == DocumentTier.Failed || !(Packages.Contains(ext) || Legacy.Contains(ext))) return doc;
        List<VbaProjectReader.Module> modules;
        try
        {
            CompoundFile? cf = null;
            if (Packages.Contains(ext))
            {
                using ZipArchive zip = ZipFile.OpenRead(path);
                ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Length > MaxProjectBytes) return doc;
                using var ms = new MemoryStream();
                using (Stream s = entry.Open()) s.CopyTo(ms);
                cf = new CompoundFile(ms.ToArray());
            }
            else if (CompoundFile.HasSignature(path)) cf = new CompoundFile(CompoundFile.ReadBounded(path));
            if (cf == null) return doc;
            modules = VbaProjectReader.Read(cf);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return doc;                                             // an unreadable macro project never hides the document's own text
        }
        if (modules.Count == 0) return doc;

        long used = doc.Segments.Sum(s => (long)s.Text.Length);
        var sink = new AutomotiveSink(limits with { MaxCharacters = Math.Max(10_000, limits.MaxCharacters - used) });
        string name = Path.GetFileName(path);
        sink.Add($"{name} contains a VBA macro project with {modules.Count} module(s): {string.Join(", ", modules.Select(m => m.Name))}. " +
                 "The macro source below is indexed as text; it was not run.", name + " macros");
        string[] folders = (Path.GetDirectoryName(path) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        bool cut = false;
        foreach (var m in modules)
        {
            ct.ThrowIfCancellationRequested();
            if (sink.Full) break;
            cut |= UniversalScriptExtractor.Analyze(new StringReader(m.Source), $"{name} macro module {m.Name}", folders, "VBA macro module", limits, ct, sink);
        }
        ExtractedDocument macros = sink.Build(path, doc.Extractor ?? "vba", null);
        var all = doc.Segments.Concat(macros.Segments).ToList();
        string note = (doc.Note == null ? "" : doc.Note + " ") + "Contains VBA macros (" + modules.Count + " module(s)), indexed as source text.";
        return doc with { Segments = all, Tier = DocumentTier.Structured, Truncated = doc.Truncated || cut || macros.Truncated, Note = note };
    }
}
