using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Minimal, bounds-checked reader for OLE compound files (MS-CFB): the container of Outlook ".msg", Word 97-2003 ".doc" and
/// PowerPoint 97-2003 ".ppt". Read-only; a damaged file raises InvalidDataException, which the registry reports as "could not be read".
/// </summary>
internal sealed class CompoundFile
{
    internal const long MaxFileBytes = 256L * 1024 * 1024;
    internal static byte[] ReadBounded(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("File is larger than the 256 MB limit for legacy Office files.");
        return File.ReadAllBytes(path);
    }

    public sealed class Entry
    {
        public string Name = ""; public int Type; public int Left = -1, Right = -1, Child = -1, Index;
        public uint Start; public long Size;
    }

    private const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF;
    private readonly byte[] _d;
    private readonly int _sectorSize, _miniSize, _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private byte[]? _miniStream;
    public IReadOnlyList<Entry> Entries { get; }
    public Entry Root => Entries[0];

    public static bool HasSignature(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[8];
            return fs.Read(b, 0, 8) == 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0 && b[4] == 0xA1 && b[5] == 0xB1 && b[6] == 0x1A && b[7] == 0xE1;
        }
        catch (Exception) { return false; }
    }

    public CompoundFile(byte[] data)
    {
        _d = data;
        if (data.Length < 512 || data[0] != 0xD0 || data[1] != 0xCF || data[2] != 0x11 || data[3] != 0xE0)
            throw new InvalidDataException("Not an OLE compound file.");
        int sectorShift = U16(30), miniShift = U16(32);
        if (sectorShift is < 7 or > 16 || miniShift is < 4 or > 12) throw new InvalidDataException("Invalid sector size.");
        _sectorSize = 1 << sectorShift; _miniSize = 1 << miniShift;
        int numFat = I32(44); uint dirStart = U32(48); _miniCutoff = I32(56);
        uint miniFatStart = U32(60); int numMiniFat = I32(64); uint difatStart = U32(68); int numDifat = I32(72);
        long maxSectors = Math.Max(1, (data.LongLength - 512) / _sectorSize + 1);
        if (numFat < 0 || numFat > maxSectors || numMiniFat < 0 || numMiniFat > maxSectors || numDifat < 0 || numDifat > maxSectors) throw new InvalidDataException("Invalid FAT size.");

        var fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < numFat; i++) { uint s = U32(76 + i * 4); if (s != FreeSect) fatSectors.Add(s); }
        uint dif = difatStart; int guard = 0;
        while (dif != EndOfChain && dif != FreeSect && guard++ <= numDifat && fatSectors.Count < numFat)
        {
            long off = SectorOffset(dif);
            for (int i = 0; i < _sectorSize / 4 - 1 && fatSectors.Count < numFat; i++) { uint s = U32(off + i * 4); if (s != FreeSect) fatSectors.Add(s); }
            dif = U32(off + _sectorSize - 4);
        }
        var fat = new uint[fatSectors.Count * (_sectorSize / 4)];
        for (int f = 0; f < fatSectors.Count; f++)
        {
            long off = SectorOffset(fatSectors[f]);
            for (int i = 0; i < _sectorSize / 4; i++) fat[f * (_sectorSize / 4) + i] = U32(off + i * 4);
        }
        _fat = fat;

        byte[] dir = ReadChain(dirStart, long.MaxValue);
        var entries = new List<Entry>();
        for (int p = 0; p + 128 <= dir.Length && entries.Count < 200_000; p += 128)
        {
            int nameLen = BitConverter.ToUInt16(dir, p + 64);
            var e = new Entry
            {
                Type = dir[p + 66], Left = BitConverter.ToInt32(dir, p + 68), Right = BitConverter.ToInt32(dir, p + 72), Child = BitConverter.ToInt32(dir, p + 76),
                Start = BitConverter.ToUInt32(dir, p + 116), Size = sectorShift == 9 ? BitConverter.ToUInt32(dir, p + 120) : BitConverter.ToInt64(dir, p + 120), Index = entries.Count
            };
            e.Name = nameLen >= 2 && nameLen <= 64 ? Encoding.Unicode.GetString(dir, p, nameLen - 2) : "";
            entries.Add(e);
        }
        if (entries.Count == 0 || entries[0].Type != 5) throw new InvalidDataException("Missing root entry.");
        Entries = entries;

        byte[] mf = miniFatStart is EndOfChain or FreeSect ? Array.Empty<byte>() : ReadChain(miniFatStart, long.MaxValue);
        _miniFat = new uint[mf.Length / 4];
        for (int i = 0; i < _miniFat.Length; i++) _miniFat[i] = BitConverter.ToUInt32(mf, i * 4);
    }

    private long SectorOffset(uint id)
    {
        long o = ((long)id + 1) * _sectorSize;
        if (o + _sectorSize > _d.LongLength) throw new InvalidDataException("Sector outside the file.");
        return o;
    }
    private int U16(long o) => BitConverter.ToUInt16(_d, (int)o);
    private int I32(long o) => BitConverter.ToInt32(_d, (int)o);
    private uint U32(long o) => o + 4 <= _d.LongLength ? BitConverter.ToUInt32(_d, (int)o) : throw new InvalidDataException("Read outside the file.");

    private byte[] ReadChain(uint start, long size)
    {
        var ms = new MemoryStream();
        uint s = start; int guard = 0, max = _fat.Length + 8;
        while (s != EndOfChain && s != FreeSect && guard++ <= max && ms.Length < size)
        {
            long off = SectorOffset(s);
            int take = (int)Math.Min(_sectorSize, size - ms.Length);
            ms.Write(_d, (int)off, take);
            if (s >= _fat.Length) break;
            s = _fat[s];
        }
        return ms.ToArray();
    }

    private byte[] ReadMini(uint start, long size)
    {
        _miniStream ??= ReadChain(Root.Start, Root.Size);
        var ms = new MemoryStream();
        uint s = start; int guard = 0;
        while (s != EndOfChain && s != FreeSect && guard++ <= _miniFat.Length + 8 && ms.Length < size)
        {
            long off = (long)s * _miniSize;
            if (off + _miniSize > _miniStream.LongLength) throw new InvalidDataException("Mini sector outside the mini stream.");
            ms.Write(_miniStream, (int)off, (int)Math.Min(_miniSize, size - ms.Length));
            if (s >= _miniFat.Length) break;
            s = _miniFat[s];
        }
        return ms.ToArray();
    }

    public byte[] ReadStream(Entry e)
    {
        if (e.Type != 2) return Array.Empty<byte>();
        if (e.Size > int.MaxValue / 2) throw new InvalidDataException("Stream too large.");
        return e.Size < _miniCutoff ? ReadMini(e.Start, e.Size) : ReadChain(e.Start, e.Size);
    }

    /// <summary>Children of a storage (the directory is a red-black tree; every node is visited once, order is not significant).</summary>
    public IEnumerable<Entry> Children(Entry storage)
    {
        var seen = new HashSet<int>(); var stack = new Stack<int>();
        if (storage.Child >= 0) stack.Push(storage.Child);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (i < 0 || i >= Entries.Count || !seen.Add(i)) continue;
            Entry e = Entries[i]; yield return e;
            if (e.Right >= 0) stack.Push(e.Right);
            if (e.Left >= 0) stack.Push(e.Left);
        }
    }

    public Entry? Child(Entry storage, string name) => Children(storage).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Outlook messages (".msg"): headers, recipients, body (plain or HTML), attachments (their text is indexed too) and embedded messages.</summary>
internal sealed class MsgExtractor : IDocumentExtractor
{
    public string Name => "msg";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".msg", StringComparison.OrdinalIgnoreCase);
    private const long MaxAttachmentBytes = 25L * 1024 * 1024;
    private const int MaxAttachments = 40;

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        try { return ExtractCore(path, limits, ct); }
        catch (InvalidDataException ex)
        {
            // A damaged or truncated file stays findable by name; the reason (never document content) is kept as the note.
            return new ExtractedDocument(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false, "Could not be read as " + Name + ": " + ex.Message, Name);
        }
    }

    private ExtractedDocument ExtractCore(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cf = new CompoundFile(CompoundFile.ReadBounded(path));
        var sink = new AutomotiveSink(limits);
        var notes = new List<string>();
        ReadMessage(cf, cf.Root, Path.GetFileName(path), sink, limits, ct, 0, notes);
        return sink.Build(path, Name, notes.Count == 0 ? null : string.Join(" ", notes.Distinct()));
    }

    private static string? Text(CompoundFile cf, CompoundFile.Entry st, string tag, Encoding ansi)
    {
        var u = cf.Child(st, $"__substg1.0_{tag}001F");
        if (u != null) return Clean(Encoding.Unicode.GetString(cf.ReadStream(u)));
        var a = cf.Child(st, $"__substg1.0_{tag}001E");
        return a == null ? null : Clean(ansi.GetString(cf.ReadStream(a)));
    }

    /// <summary>The message declares the code page of its non-Unicode strings (PidTagMessageCodepage / InternetCodepage); Windows-1252 only when it does not.</summary>
    private static Encoding AnsiEncoding(CompoundFile cf, CompoundFile.Entry st)
    {
        var props = cf.Child(st, "__properties_version1.0");
        if (props != null)
        {
            byte[] b = cf.ReadStream(props);
            for (int p = ReferenceEquals(st, cf.Root) ? 32 : 24; p + 16 <= b.Length; p += 16)
            {
                int type = BitConverter.ToUInt16(b, p), id = BitConverter.ToUInt16(b, p + 2);
                if (type == 0x0003 && (id == 0x3FFD || id == 0x3FDE))
                {
                    int cp = BitConverter.ToInt32(b, p + 8);
                    try { if (cp > 0 && cp != 1200 && cp != 65001) return Encoding.GetEncoding(cp); if (cp == 65001) return Encoding.UTF8; } catch (ArgumentException) { }
                }
            }
        }
        return Encoding.GetEncoding(1252);
    }
    private static string Clean(string s) => s.TrimEnd('\0').Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static DateTime? Time(CompoundFile cf, CompoundFile.Entry st, uint tagId)
    {
        var props = cf.Child(st, "__properties_version1.0");
        if (props == null) return null;
        byte[] b = cf.ReadStream(props);
        int header = ReferenceEquals(st, cf.Root) ? 32 : 24;
        for (int p = header; p + 16 <= b.Length; p += 16)
            if (BitConverter.ToUInt16(b, p) == 0x0040 && BitConverter.ToUInt16(b, p + 2) == tagId)
            {
                long ft = BitConverter.ToInt64(b, p + 8);
                if (ft > 0 && ft < 2650467743999999999L) return DateTime.FromFileTimeUtc(ft);
            }
        return null;
    }

    private static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        html = Regex.Replace(html, @"<(br|/p|/div|/tr|/li|/h\d)[^>]*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        html = Regex.Replace(html, @"<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(2));
        html = System.Net.WebUtility.HtmlDecode(html);
        return Regex.Replace(html, @"[ \t]+", " ").Replace(" \n", "\n").Trim();
    }

    private void ReadMessage(CompoundFile cf, CompoundFile.Entry st, string label, AutomotiveSink sink, ExtractionLimits limits, CancellationToken ct, int depth, List<string> notes)
    {
        Encoding ansi = AnsiEncoding(cf, st);
        string subject = Text(cf, st, "0037", ansi) ?? "(no subject)";
        string from = Text(cf, st, "0C1A", ansi) ?? Text(cf, st, "0042", ansi) ?? "";
        string fromAddr = Text(cf, st, "5D01", ansi) ?? Text(cf, st, "0C1F", ansi) ?? "";
        string to = Text(cf, st, "0E04", ansi) ?? "", cc = Text(cf, st, "0E03", ansi) ?? "", bcc = Text(cf, st, "0E02", ansi) ?? "";
        DateTime? sent = Time(cf, st, 0x0039) ?? Time(cf, st, 0x0E06);
        var head = new StringBuilder();
        head.Append("Email message (messaggio e-mail), subject (oggetto): ").AppendLine(subject);
        if (from.Length > 0 || fromAddr.Length > 0) head.Append("From (mittente): ").Append(from).Append(fromAddr.Length > 0 && fromAddr != from ? " <" + fromAddr + ">" : "").AppendLine();
        if (to.Length > 0) head.Append("To (destinatario): ").AppendLine(to);
        if (cc.Length > 0) head.Append("Cc (copia conoscenza): ").AppendLine(cc);
        if (bcc.Length > 0) head.Append("Bcc (copia nascosta): ").AppendLine(bcc);
        if (sent.HasValue) head.Append("Date (data): ").AppendLine(sent.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        string heading = (depth == 0 ? "Email: " : "Embedded email: ") + subject;
        sink.Add(head.ToString(), heading + " > headers");

        string body = Text(cf, st, "1000", ansi) ?? "";
        if (body.Length == 0)
        {
            var html = cf.Child(st, "__substg1.0_10130102");
            if (html != null) body = HtmlToText(Encoding.UTF8.GetString(cf.ReadStream(html)));
        }
        if (body.Length > 0) sink.Add(body, heading + " > body");
        else notes.Add("No readable text body (rich-text-only message).");

        int count = 0;
        foreach (var att in cf.Children(st).Where(c => c.Type == 1 && c.Name.StartsWith("__attach_version1.0_", StringComparison.Ordinal)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaxAttachments) { notes.Add("More than " + MaxAttachments + " attachments: the rest were not read."); break; }
            string name = Text(cf, att, "3707", ansi) ?? Text(cf, att, "3704", ansi) ?? "attachment" + count;
            var embedded = cf.Child(att, "__substg1.0_3701000D");
            if (embedded != null && depth < 2) { ReadMessage(cf, embedded, name, sink, limits, ct, depth + 1, notes); continue; }
            var data = cf.Child(att, "__substg1.0_37010102");
            if (data == null) { sink.Add($"Attachment {name} (no content stored in the message)", heading + " > attachment " + name); continue; }
            if (data.Size > MaxAttachmentBytes) { sink.Add($"Attachment {name} ({data.Size} bytes) is larger than the {MaxAttachmentBytes / (1024 * 1024)} MB limit and was not read.", heading + " > attachment " + name); continue; }
            IndexAttachment(name, cf.ReadStream(data), heading, sink, limits, ct, notes);
        }
    }

    /// <summary>The attachment is extracted with the same readers as any other file (in a private temp folder that is always removed).</summary>
    internal static void IndexAttachment(string name, byte[] bytes, string heading, AutomotiveSink sink, ExtractionLimits limits, CancellationToken ct, List<string> notes)
    {
        string safe = new string(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        if (safe.Length == 0) safe = "attachment.bin";
        string dir = Path.Combine(Path.GetTempPath(), "parux-att-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, safe);
            File.WriteAllBytes(tmp, bytes);
            ExtractedDocument inner = StructuredDocumentExtractor.Extract(tmp, limits, ct);
            if (inner.Tier is DocumentTier.Structured or DocumentTier.Text && inner.Segments.Count > 0)
            {
                foreach (DocumentSegment seg in inner.Segments)
                    if (!sink.Add(seg.Text, heading + " > attachment " + name + (seg.Position.Heading != null ? " > " + seg.Position.Heading : "")))
                        return;
            }
            else
            {
                sink.Add($"Attachment {name} ({bytes.Length} bytes): content not indexed ({inner.Note ?? "unsupported type"}).", heading + " > attachment " + name);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

/// <summary>Word 97-2003 (".doc"): the text of the main document, headers, footnotes and text boxes from the piece table.</summary>
internal sealed class LegacyDocExtractor : IDocumentExtractor
{
    public string Name => "doc97";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase) && CompoundFile.HasSignature(path);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        try { return ExtractCore(path, limits, ct); }
        catch (InvalidDataException ex)
        {
            // A damaged or truncated file stays findable by name; the reason (never document content) is kept as the note.
            return new ExtractedDocument(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false, "Could not be read as " + Name + ": " + ex.Message, Name);
        }
    }

    private ExtractedDocument ExtractCore(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cf = new CompoundFile(CompoundFile.ReadBounded(path));
        var wd = cf.Child(cf.Root, "WordDocument") ?? throw new InvalidDataException("No WordDocument stream.");
        byte[] doc = cf.ReadStream(wd);
        if (doc.Length < 0x1AA || BitConverter.ToUInt16(doc, 0) != 0xA5EC) throw new InvalidDataException("Not a Word binary document.");
        ushort flags = BitConverter.ToUInt16(doc, 0x0A);
        if ((flags & 0x0100) != 0) return StructuredDocumentExtractor.Failed(path, "The document is password protected (encrypted); nothing was indexed.", Name);
        var tableEntry = cf.Child(cf.Root, (flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("No table stream.");
        byte[] tbl = cf.ReadStream(tableEntry);
        uint fcClx = BitConverter.ToUInt32(doc, 0x01A2), lcbClx = BitConverter.ToUInt32(doc, 0x01A6);
        if (lcbClx == 0 || fcClx + lcbClx > tbl.Length) throw new InvalidDataException("Invalid piece table location.");

        int pos = (int)fcClx, end = (int)(fcClx + lcbClx);
        while (pos < end && tbl[pos] == 1) pos += 3 + BitConverter.ToUInt16(tbl, pos + 1);
        if (pos >= end || tbl[pos] != 2) throw new InvalidDataException("No piece table.");
        uint plcLen = BitConverter.ToUInt32(tbl, pos + 1); pos += 5;
        int n = (int)((plcLen - 4) / 12);
        if (n <= 0 || pos + plcLen > tbl.Length) throw new InvalidDataException("Invalid piece table.");

        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            uint cp0 = BitConverter.ToUInt32(tbl, pos + i * 4), cp1 = BitConverter.ToUInt32(tbl, pos + (i + 1) * 4);
            int chars = (int)Math.Min(cp1 - cp0, 50_000_000u);
            uint fcRaw = BitConverter.ToUInt32(tbl, pos + (n + 1) * 4 + i * 8 + 2);
            bool compressed = (fcRaw & 0x40000000) != 0; long fc = fcRaw & 0x3FFFFFFF;
            if (compressed) { fc /= 2; if (fc + chars > doc.Length) chars = (int)Math.Max(0, doc.Length - fc); sb.Append(Encoding.GetEncoding(1252).GetString(doc, (int)fc, chars)); }
            else { if (fc + chars * 2L > doc.Length) chars = (int)Math.Max(0, (doc.Length - fc) / 2); sb.Append(Encoding.Unicode.GetString(doc, (int)fc, chars * 2)); }
            if (sb.Length > limits.MaxCharacters) break;
        }
        string text = Normalise(sb.ToString());
        var sink = new AutomotiveSink(limits);
        string name = Path.GetFileName(path);
        int p = 0, partNo = 0;
        while (p < text.Length)
        {
            int len = Math.Min(1500, text.Length - p);
            if (p + len < text.Length) { int cut = text.LastIndexOf('\n', p + len - 1, len); if (cut > p + 300) len = cut - p + 1; }
            if (!sink.Add(text.Substring(p, len), $"{name} section {++partNo} (characters {p}-{p + len})")) break;
            p += len;
        }
        return sink.Build(path, Name, sink.SegmentCount == 0 ? "No text found in this document." : null);
    }

    /// <summary>Removes field codes (keeps field results), cell/row marks become tabs, other control characters are dropped.</summary>
    internal static string Normalise(string raw)
    {
        var sb = new StringBuilder(raw.Length); var inCode = new Stack<bool>();
        foreach (char c in raw)
        {
            if (c == '\u0013') { inCode.Push(true); continue; }
            if (c == '\u0014') { if (inCode.Count > 0) { inCode.Pop(); inCode.Push(false); } continue; }
            if (c == '\u0015') { if (inCode.Count > 0) inCode.Pop(); continue; }
            if (inCode.Contains(true)) continue;
            switch (c)
            {
                case '\r': case '\u000B': case '\u000C': sb.Append('\n'); break;
                case '\a': sb.Append('\t'); break;
                case '\n': case '\t': sb.Append(c); break;
                default: if (c >= ' ' || c == ' ') sb.Append(c); break;
            }
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }
}
