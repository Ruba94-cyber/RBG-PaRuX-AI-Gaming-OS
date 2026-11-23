using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// CAN bus trace readers (Vector ASCII ".asc" and binary ".blf"). A raw trace of hundreds of thousands of frames is useless
/// as free text, so each file is condensed into searchable evidence that always carries its position (line or frame number
/// and time): a summary, one segment per (channel, CAN ID) with timing, byte ranges, distinct payloads and payload changes,
/// plus J1939 PGN/source-address decoding and DM1 active-DTC decoding. Bounded memory and output; limits are reported.
/// Nothing is sent anywhere and the file is only read.
/// </summary>
internal sealed class CanTraceAggregator
{
    private const int MaxDistinctIds = 4000;
    private const int MaxDistinctPayloadsKept = 24;
    private const int MaxChangesKept = 150;
    private const int MaxDm1Kept = 400;

    private readonly record struct Key(int Channel, uint Id, bool Extended, bool Fd);

    private sealed class IdStats
    {
        public long Count, Rx, Tx, Changes, Remote;
        public double FirstT, LastT, PrevT = double.NaN, MinDt = double.PositiveInfinity, MaxDt, SumDt;
        public long FirstPos, LastPos;
        public int DlcMin = int.MaxValue, DlcMax;
        public byte[] ByteMin = new byte[8], ByteMax = new byte[8];
        public ulong[] Seen = new ulong[8 * 4];   // per byte: 256-bit set
        public bool[] ByteUsed = new bool[8];
        public byte[]? Last;
        public readonly Dictionary<string, (double T, long Pos)> Distinct = new();
        public long DistinctOverflow;
        public readonly List<(double T, long Pos, string Data)> Changed = new();
    }

    private sealed class Dm1Event
    {
        public int Sa; public string Lamps = ""; public string Dtcs = "";
        public long Count; public double FirstT, LastT; public long FirstPos, LastPos;
    }

    private readonly Dictionary<Key, IdStats> _ids = new();
    private readonly Dictionary<string, Dm1Event> _dm1 = new();
    private readonly SortedSet<int> _channels = new();
    private readonly string _posUnit;

    public CanTraceAggregator(string positionUnit) => _posUnit = positionUnit;

    public long Frames, ErrorFrames, Unparsed;
    public double FirstT = double.NaN, LastT = double.NaN;
    public long FirstPos, LastPos;
    public bool IdLimitHit;
    public long Dm1Dropped;

    public void Add(int channel, uint id, bool ext, bool fd, bool tx, bool remote, ReadOnlySpan<byte> data, double t, long pos)
    {
        Frames++;
        if (double.IsNaN(FirstT)) { FirstT = t; FirstPos = pos; }
        LastT = t; LastPos = pos;
        _channels.Add(channel);
        var key = new Key(channel, id, ext, fd);
        if (!_ids.TryGetValue(key, out IdStats? s))
        {
            if (_ids.Count >= MaxDistinctIds) { IdLimitHit = true; return; }
            _ids[key] = s = new IdStats { FirstT = t, FirstPos = pos };
        }
        s.Count++; if (tx) s.Tx++; else s.Rx++; if (remote) s.Remote++;
        s.LastT = t; s.LastPos = pos;
        if (!double.IsNaN(s.PrevT))
        {
            double dt = t - s.PrevT;
            if (dt >= 0) { if (dt < s.MinDt) s.MinDt = dt; if (dt > s.MaxDt) s.MaxDt = dt; s.SumDt += dt; }
        }
        s.PrevT = t;
        if (remote) return;
        int n = data.Length;
        if (n < s.DlcMin) s.DlcMin = n; if (n > s.DlcMax) s.DlcMax = n;
        EnsureBytes(s, n);
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            if (!s.ByteUsed[i]) { s.ByteUsed[i] = true; s.ByteMin[i] = b; s.ByteMax[i] = b; }
            else { if (b < s.ByteMin[i]) s.ByteMin[i] = b; if (b > s.ByteMax[i]) s.ByteMax[i] = b; }
            s.Seen[i * 4 + (b >> 6)] |= 1UL << (b & 63);
        }
        bool changed = s.Last == null || !data.SequenceEqual(s.Last);
        if (changed)
        {
            string hex = Hex(data);
            if (s.Last != null) s.Changes++;
            if (!s.Distinct.ContainsKey(hex))
            {
                if (s.Distinct.Count < MaxDistinctPayloadsKept) s.Distinct[hex] = (t, pos); else s.DistinctOverflow++;
            }
            if (s.Changed.Count < MaxChangesKept) s.Changed.Add((t, pos, hex));
            s.Last = data.ToArray();
        }
        if (ext && J1939Pgn(id) == 0xFECA && n >= 6) TrackDm1(id & 0xFF, data, t, pos);
    }

    public void AddError() => ErrorFrames++;

    private static void EnsureBytes(IdStats s, int n)
    {
        if (n <= s.ByteMin.Length) return;
        Array.Resize(ref s.ByteMin, n); Array.Resize(ref s.ByteMax, n); Array.Resize(ref s.ByteUsed, n);
        Array.Resize(ref s.Seen, n * 4);
    }

    internal static string Hex(ReadOnlySpan<byte> d)
    {
        var sb = new StringBuilder(d.Length * 3);
        for (int i = 0; i < d.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(d[i].ToString("X2", CultureInfo.InvariantCulture)); }
        return sb.ToString();
    }

    internal static uint J1939Pgn(uint id)
    {
        uint edp = (id >> 25) & 1, dp = (id >> 24) & 1, pf = (id >> 16) & 0xFF, ps = (id >> 8) & 0xFF;
        uint pgn = (edp << 17) | (dp << 16) | (pf << 8);
        return pf >= 240 ? pgn | ps : pgn;
    }

    private static string Lamp(int v) => v switch { 0 => "off", 1 => "on", 2 => "error", _ => "n/a" };

    private void TrackDm1(uint sa, ReadOnlySpan<byte> d, double t, long pos)
    {
        string lamps = $"protect={Lamp((d[0] >> 6) & 3)}, amber={Lamp((d[0] >> 4) & 3)}, red-stop={Lamp((d[0] >> 2) & 3)}, MIL={Lamp(d[0] & 3)}";
        var dtcs = new List<string>();
        for (int i = 2; i + 3 < d.Length; i += 4)
        {
            if ((d[i] | d[i + 1] | d[i + 2] | d[i + 3]) == 0) continue;
            if (d[i] == 0xFF && d[i + 1] == 0xFF && d[i + 2] == 0xFF && d[i + 3] == 0xFF) continue;
            int spn = d[i] | (d[i + 1] << 8) | (((d[i + 2] >> 5) & 7) << 16);
            int fmi = d[i + 2] & 0x1F;
            int cm = (d[i + 3] >> 7) & 1, oc = d[i + 3] & 0x7F;
            dtcs.Add($"SPN {spn} FMI {fmi} (occurrences {oc}{(cm == 1 ? ", SPN conversion method 1" : "")})");
        }
        string dt = dtcs.Count == 0 ? "no active DTC" : string.Join("; ", dtcs);
        string k = sa + "|" + lamps + "|" + dt;
        if (!_dm1.TryGetValue(k, out Dm1Event? e))
        {
            if (_dm1.Count >= MaxDm1Kept) { Dm1Dropped++; return; }
            _dm1[k] = e = new Dm1Event { Sa = (int)sa, Lamps = lamps, Dtcs = dt, FirstT = t, FirstPos = pos };
        }
        e.Count++; e.LastT = t; e.LastPos = pos;
    }

    private static string T(double t) => t.ToString("0.000000", CultureInfo.InvariantCulture) + " s";

    public ExtractedDocument Build(string path, string extractor, AutomotiveSink sink, string headerBlock, string? note, bool truncated)
    {
        string name = Path.GetFileName(path);
        var sum = new StringBuilder();
        sum.Append("CAN trace file ").AppendLine(name);
        if (headerBlock.Length > 0) sum.AppendLine(headerBlock.TrimEnd());
        sum.Append("Frames read: ").Append(Frames).Append("; error frames: ").Append(ErrorFrames)
           .Append("; distinct (channel, CAN ID) pairs: ").Append(_ids.Count)
           .Append("; channels: ").AppendLine(_channels.Count == 0 ? "none" : string.Join(", ", _channels));
        if (Frames > 0)
            sum.Append("Trace time ").Append(T(FirstT)).Append(" (").Append(_posUnit).Append(' ').Append(FirstPos).Append(") to ")
               .Append(T(LastT)).Append(" (").Append(_posUnit).Append(' ').Append(LastPos).Append("), duration ").AppendLine(T(LastT - FirstT));
        if (Unparsed > 0) sum.Append("Lines/objects not understood and skipped: ").AppendLine(Unparsed.ToString(CultureInfo.InvariantCulture));
        if (IdLimitHit) sum.AppendLine("More than " + MaxDistinctIds + " distinct IDs: later new IDs were not tracked.");
        sum.AppendLine("Top IDs by frame count: " + string.Join("; ", _ids.OrderByDescending(p => p.Value.Count).Take(12)
            .Select(p => IdName(p.Key) + " x" + p.Value.Count)));
        sink.Add(sum.ToString(), "Trace summary " + name);

        foreach (var pair in _ids.OrderBy(p => p.Key.Channel).ThenBy(p => p.Key.Id))
        {
            if (sink.Full) break;
            string heading = $"CAN ID {IdName(pair.Key)} ch{pair.Key.Channel} ({_posUnit}s {pair.Value.FirstPos}-{pair.Value.LastPos})";
            string prefix = $"CAN ID {IdName(pair.Key)} ch{pair.Key.Channel} in {name} (continued, {_posUnit}s {pair.Value.FirstPos}-{pair.Value.LastPos}):";
            AddSmall(sink, IdText(name, pair.Key, pair.Value), heading, prefix);
        }

        if (_dm1.Count > 0 && !sink.Full)
        {
            var sb = new StringBuilder();
            sb.Append("J1939 DM1 (PGN 0xFECA, active diagnostic trouble codes) found in ").AppendLine(name);
            foreach (Dm1Event e in _dm1.Values.OrderBy(x => x.Sa).ThenBy(x => x.FirstT))
                sb.Append("Source address 0x").Append(e.Sa.ToString("X2", CultureInfo.InvariantCulture)).Append(": ").Append(e.Dtcs)
                  .Append(" | lamps: ").Append(e.Lamps).Append(" | seen ").Append(e.Count).Append("x from ").Append(T(e.FirstT))
                  .Append(" (").Append(_posUnit).Append(' ').Append(e.FirstPos).Append(") to ").Append(T(e.LastT))
                  .Append(" (").Append(_posUnit).Append(' ').Append(e.LastPos).AppendLine(")");
            if (Dm1Dropped > 0) sb.AppendLine("More than " + MaxDm1Kept + " distinct DM1 states: later ones were not listed.");
            AddSmall(sink, sb.ToString(), "J1939 DM1 diagnostic trouble codes " + name, "J1939 DM1 (PGN 0xFECA) active diagnostic trouble codes in " + name + " (continued):");
        }

        if (truncated || sink.Truncated) sink.MarkTruncated();
        return sink.Build(path, extractor, Frames == 0 ? (note ?? "No CAN frames were found in this trace.") : note);
    }

    /// <summary>
    /// Retrieval chunks are small, so every segment stays under ~800 characters and the later ones repeat the identifying
    /// prefix: a passage that is retrieved alone still says which file, ID and positions it describes.
    /// </summary>
    private static void AddSmall(AutomotiveSink sink, string text, string heading, string prefix)
    {
        const int Max = 800;
        var cur = new StringBuilder();
        bool first = true;
        foreach (string line in text.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            if (cur.Length > 0 && cur.Length + l.Length + 1 > Max)
            {
                if (!sink.Add(cur.ToString(), heading)) return;
                cur.Clear(); first = false;
            }
            if (cur.Length == 0 && !first) cur.AppendLine(prefix);
            cur.AppendLine(l);
        }
        if (cur.Length > 0) sink.Add(cur.ToString(), heading);
    }

    private static string IdName(Key k) =>
        "0x" + k.Id.ToString(k.Extended ? "X8" : "X3", CultureInfo.InvariantCulture) + (k.Extended ? "x" : "") + (k.Fd ? " FD" : "");

    private string IdText(string file, Key k, IdStats s)
    {
        var sb = new StringBuilder();
        sb.Append("CAN ID ").Append(IdName(k)).Append(k.Extended ? " (extended 29-bit)" : " (standard 11-bit)").Append(" on channel ").Append(k.Channel)
          .Append(" in ").AppendLine(file);
        if (k.Extended)
            sb.Append("J1939: priority ").Append((k.Id >> 26) & 7).Append(", PGN 0x").Append(J1939Pgn(k.Id).ToString("X", CultureInfo.InvariantCulture))
              .Append(" (").Append(J1939Pgn(k.Id)).Append("), source address 0x").Append((k.Id & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
              .Append(((k.Id >> 16) & 0xFF) < 240 ? ", destination address 0x" + ((k.Id >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture) : "").AppendLine();
        sb.Append("Frames: ").Append(s.Count).Append(" (Rx ").Append(s.Rx).Append(", Tx ").Append(s.Tx).Append(s.Remote > 0 ? ", remote " + s.Remote : "").Append(')');
        if (s.DlcMax > 0) sb.Append("; data length ").Append(s.DlcMin == s.DlcMax ? s.DlcMax.ToString(CultureInfo.InvariantCulture) : s.DlcMin + "-" + s.DlcMax).Append(" bytes");
        sb.AppendLine();
        sb.Append("The first frame with this ID is at ").Append(_posUnit).Append(' ').Append(s.FirstPos).Append(" and the last at ").Append(_posUnit).Append(' ').Append(s.LastPos).AppendLine(".");
        sb.Append("First seen ").Append(T(s.FirstT)).Append(" (").Append(_posUnit).Append(' ').Append(s.FirstPos).Append("), last seen ")
          .Append(T(s.LastT)).Append(" (").Append(_posUnit).Append(' ').Append(s.LastPos).AppendLine(")");
        if (s.Count > 1 && !double.IsInfinity(s.MinDt))
            sb.Append("Period: min ").Append((s.MinDt * 1000).ToString("0.###", CultureInfo.InvariantCulture)).Append(" ms, mean ")
              .Append((s.SumDt / (s.Count - 1) * 1000).ToString("0.###", CultureInfo.InvariantCulture)).Append(" ms, max ")
              .Append((s.MaxDt * 1000).ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(" ms");
        if (s.DlcMax > 0)
        {
            sb.Append("Byte ranges (min..max, distinct values): ");
            for (int i = 0; i < s.DlcMax && i < s.ByteUsed.Length; i++)
            {
                if (!s.ByteUsed[i]) continue;
                int distinct = 0; for (int w = 0; w < 4; w++) distinct += System.Numerics.BitOperations.PopCount(s.Seen[i * 4 + w]);
                sb.Append('B').Append(i).Append(' ').Append(s.ByteMin[i].ToString("X2", CultureInfo.InvariantCulture)).Append("..")
                  .Append(s.ByteMax[i].ToString("X2", CultureInfo.InvariantCulture)).Append(" (").Append(distinct).Append(i + 1 < s.DlcMax ? "); " : ")");
            }
            sb.AppendLine();
            sb.Append("Payload changes: ").Append(s.Changes).Append("; distinct payloads listed: ").Append(s.Distinct.Count)
              .AppendLine(s.DistinctOverflow > 0 ? " (+" + s.DistinctOverflow + " more not listed)" : "");
            foreach (var d in s.Distinct)
                sb.Append("  data ").Append(d.Key).Append(" first at ").Append(T(d.Value.T)).Append(" (").Append(_posUnit).Append(' ').Append(d.Value.Pos).AppendLine(")");
            if (s.Changed.Count > 0)
            {
                sb.Append("Payload change log (first ").Append(s.Changed.Count).AppendLine("):");
                foreach (var c in s.Changed)
                    sb.Append("  ").Append(T(c.T)).Append(" (").Append(_posUnit).Append(' ').Append(c.Pos).Append("): ").AppendLine(c.Data);
            }
        }
        return sb.ToString();
    }
}

/// <summary>Vector ASCII trace (".asc"): classic CAN, CAN FD, error frames, hex or decimal base, absolute or relative time.</summary>
internal sealed class AscTraceExtractor : IDocumentExtractor
{
    public string Name => "can-asc";

    public bool CanHandle(string path) =>
        Path.GetExtension(path).Equals(".asc", StringComparison.OrdinalIgnoreCase) && LooksLikeTrace(path);

    /// <summary>True for a Vector ASCII trace header ("date ..." then "base hex|dec"). An armored PGP/ASCII key never matches.</summary>
    internal static bool LooksLikeTrace(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buf = new byte[1024];
            int n = fs.Read(buf, 0, buf.Length);
            string head = Encoding.Latin1.GetString(buf, 0, n);
            if (head.Contains("BEGIN PGP", StringComparison.Ordinal)) return false;
            string trimmed = head.TrimStart();
            return trimmed.StartsWith("date ", StringComparison.OrdinalIgnoreCase) &&
                   (head.Contains("base hex", StringComparison.OrdinalIgnoreCase) || head.Contains("base dec", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        var agg = new CanTraceAggregator("line");
        var sink = new AutomotiveSink(limits);
        var header = new StringBuilder();
        bool hex = true, relative = false, truncated = false, relativeChecked = false;
        double clock = 0;
        long lineNo = 0, chars = 0;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var reader = new StreamReader(fs, Encoding.Latin1, false, 1 << 16);
        byte[] data = new byte[64];
        Span<Range> parts = stackalloc Range[96];
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            if ((lineNo & 1023) == 0) ct.ThrowIfCancellationRequested();
            chars += line.Length + 1;
            if (chars > limits.MaxStructuredFileBytes) { truncated = true; break; }
            ReadOnlySpan<char> s = line.AsSpan().Trim();
            if (s.Length == 0) continue;
            if (!char.IsDigit(s[0]))
            {
                if (s.StartsWith("date ", StringComparison.OrdinalIgnoreCase) || s.StartsWith("base ", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("//", StringComparison.Ordinal) || s.StartsWith("no internal", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("internal", StringComparison.OrdinalIgnoreCase))
                {
                    if (header.Length < 600) header.AppendLine(s.ToString());
                    if (s.StartsWith("base ", StringComparison.OrdinalIgnoreCase))
                    {
                        hex = s.IndexOf("dec", StringComparison.OrdinalIgnoreCase) < 0 || s.IndexOf("hex", StringComparison.OrdinalIgnoreCase) >= 0;
                        relative = s.IndexOf("relative", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                continue;   // Begin/End Triggerblock and other markers carry no frame
            }

            if (relative && !relativeChecked)
            {
                relativeChecked = true;
                if (TimestampsLookCumulative(path))
                {
                    relative = false;
                    header.AppendLine("Note: the header says 'timestamps relative' but the values never decrease, so they are read as time since the start of the trace, not as deltas.");
                }
            }
            // tokens
            int cnt = s.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
            if (cnt < 3) { agg.Unparsed++; continue; }
            if (!double.TryParse(s[parts[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out double raw)) { agg.Unparsed++; continue; }
            double t = relative ? (clock += raw) : raw;

            ReadOnlySpan<char> t1 = s[parts[1]];
            if (t1.Equals("CANFD", StringComparison.OrdinalIgnoreCase))
            {
                // t CANFD ch Rx|Tx id [name] brs esi dlc len data...
                if (cnt < 9 || !int.TryParse(s[parts[2]], out int fch)) { agg.Unparsed++; continue; }
                bool ftx = s[parts[3]].Equals("Tx", StringComparison.OrdinalIgnoreCase);
                if (!ParseId(s[parts[4]], hex, out uint fid, out bool fext)) { agg.Unparsed++; continue; }
                int p = 5;
                if (p < cnt && s[parts[p]].Length > 1 && !char.IsDigit(s[parts[p]][0])) p++;     // optional symbolic name
                p += 3;                                                                          // brs esi dlc
                if (p >= cnt || !int.TryParse(s[parts[p]], out int len)) { agg.Unparsed++; continue; }
                p++;
                int got = FillData(s, parts, p, cnt, Math.Min(len, 64), hex, data);
                agg.Add(fch, fid, fext, true, ftx, false, data.AsSpan(0, got), t, lineNo);
                continue;
            }
            if (!int.TryParse(t1, out int ch)) { agg.Unparsed++; continue; }
            ReadOnlySpan<char> t2 = s[parts[2]];
            if (t2.StartsWith("ErrorFrame", StringComparison.OrdinalIgnoreCase) || t2.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            { agg.AddError(); continue; }
            if (t2.StartsWith("Statistic", StringComparison.OrdinalIgnoreCase)) continue;
            if (cnt < 5 || !ParseId(t2, hex, out uint id, out bool ext)) { agg.Unparsed++; continue; }
            bool tx = s[parts[3]].Equals("Tx", StringComparison.OrdinalIgnoreCase);
            ReadOnlySpan<char> kind = s[parts[4]];
            if (kind.Equals("r", StringComparison.OrdinalIgnoreCase))
            { agg.Add(ch, id, ext, false, tx, true, ReadOnlySpan<byte>.Empty, t, lineNo); continue; }
            if (!kind.Equals("d", StringComparison.OrdinalIgnoreCase) || cnt < 6 || !int.TryParse(s[parts[5]], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int dlc))
            { agg.Unparsed++; continue; }
            int gotBytes = FillData(s, parts, 6, cnt, Math.Min(dlc, 8), hex, data);
            agg.Add(ch, id, ext, false, tx, false, data.AsSpan(0, gotBytes), t, lineNo);
        }

        return agg.Build(path, Name, sink, header.ToString(),
            truncated ? "File larger than the structured-parsing limit: only the first part was read." : null, truncated);
    }

    /// <summary>
    /// A genuine delta trace jumps up and down (inter-frame gaps vary). Hundreds of frames whose time never goes backwards
    /// mean the writer labelled cumulative times as "relative"; summing them would produce an absurd duration.
    /// </summary>
    internal static bool TimestampsLookCumulative(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.Latin1);
            double prev = double.NegativeInfinity; int n = 0; string? l;
            while ((l = reader.ReadLine()) != null && n < 4000)
            {
                ReadOnlySpan<char> s = l.AsSpan().TrimStart();
                if (s.Length == 0 || !char.IsDigit(s[0])) continue;
                int sp = s.IndexOf(' ');
                if (sp <= 0 || !double.TryParse(s[..sp], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
                if (v < prev) return false;
                prev = v; n++;
            }
            return n >= 200;
        }
        catch (Exception) { return false; }
    }

    private static bool ParseId(ReadOnlySpan<char> tok, bool hex, out uint id, out bool ext)
    {
        ext = false;
        if (tok.Length > 0 && (tok[^1] == 'x' || tok[^1] == 'X')) { ext = true; tok = tok[..^1]; }
        bool ok = hex ? uint.TryParse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) : uint.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        if (ok && !ext && id > 0x7FF) ext = true;
        return ok;
    }

    private static int FillData(ReadOnlySpan<char> s, ReadOnlySpan<Range> parts, int from, int cnt, int want, bool hex, byte[] data)
    {
        int n = 0;
        for (int i = from; i < cnt && n < want && n < data.Length; i++)
        {
            ReadOnlySpan<char> tok = s[parts[i]];
            if (!byte.TryParse(tok, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out byte b)) break;
            data[n++] = b;
        }
        return n;
    }
}

/// <summary>Vector binary log (".blf"): zlib log containers holding classic CAN, CAN FD and CAN error objects.</summary>
internal sealed class BlfTraceExtractor : IDocumentExtractor
{
    public string Name => "can-blf";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".blf", StringComparison.OrdinalIgnoreCase);

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        byte[] fh = new byte[8];
        if (fs.Read(fh, 0, 8) < 8 || fh[0] != 'L' || fh[1] != 'O' || fh[2] != 'G' || fh[3] != 'G')
            return StructuredDocumentExtractor.Failed(path, "Not a Vector BLF file (missing LOGG signature); nothing was indexed.", Name);
        uint fileHeaderSize = BitConverter.ToUInt32(fh, 4);
        if (fileHeaderSize < 16 || fileHeaderSize > 4096)
            return StructuredDocumentExtractor.Failed(path, "BLF file header is not valid; nothing was indexed.", Name);
        fs.Position = fileHeaderSize;

        var agg = new CanTraceAggregator("frame");
        var sink = new AutomotiveSink(limits);
        var inner = new MemoryStream();
        long objectNo = 0, unsupported = 0;
        bool truncated = false, corrupt = false;
        long uncompressedTotal = 0;
        byte[] hdr = new byte[16];

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int r = ReadFull(fs, hdr, 16);
            if (r < 16) break;
            if (hdr[0] != 'L' || hdr[1] != 'O' || hdr[2] != 'B' || hdr[3] != 'J') { corrupt = true; break; }
            uint objSize = BitConverter.ToUInt32(hdr, 8), objType = BitConverter.ToUInt32(hdr, 12);
            if (objSize < 16 || objSize > 64 * 1024 * 1024) { corrupt = true; break; }
            byte[] body = new byte[objSize - 16];
            if (ReadFull(fs, body, body.Length) < body.Length) { corrupt = true; break; }
            int pad = (int)(objSize & 3);
            if (pad > 0) fs.Seek(pad, SeekOrigin.Current);
            if (objType != 10 || body.Length < 16) continue;     // only log containers at file level

            ushort method = BitConverter.ToUInt16(body, 0);
            uint declared = BitConverter.ToUInt32(body, 8);
            if (declared > 256u * 1024 * 1024) { corrupt = true; break; }
            byte[] plain;
            try
            {
                if (method == 2)
                {
                    using var zin = new ZLibStream(new MemoryStream(body, 16, body.Length - 16), CompressionMode.Decompress);
                    using var ms = new MemoryStream((int)Math.Min(declared, int.MaxValue));
                    zin.CopyTo(ms);
                    plain = ms.ToArray();
                }
                else if (method == 0) plain = body.AsSpan(16).ToArray();
                else { unsupported++; continue; }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { corrupt = true; break; }

            uncompressedTotal += plain.Length;
            if (uncompressedTotal > limits.MaxStructuredFileBytes) { truncated = true; break; }
            // append, then parse every complete object; a partial one waits for the next container
            inner.Position = inner.Length;
            inner.Write(plain, 0, plain.Length);
            byte[] buf = inner.GetBuffer();
            long len = inner.Length;
            long pos = 0;
            while (len - pos >= 16)
            {
                if (buf[pos] != 'L' || buf[pos + 1] != 'O' || buf[pos + 2] != 'B' || buf[pos + 3] != 'J') { corrupt = true; break; }
                ushort headerSize = BitConverter.ToUInt16(buf, (int)pos + 4);
                uint size = BitConverter.ToUInt32(buf, (int)pos + 8);
                uint type = BitConverter.ToUInt32(buf, (int)pos + 12);
                if (size < 16) { corrupt = true; break; }
                if (len - pos < size) break;
                objectNo++;
                ParseObject(agg, buf.AsSpan((int)pos, (int)size), headerSize, type, objectNo, ref unsupported);
                pos += size + (size & 3);
                if (pos > len) pos = len;
            }
            if (corrupt) break;
            // keep the unconsumed tail
            long rest = len - pos;
            if (rest > 0) Buffer.BlockCopy(buf, (int)pos, buf, 0, (int)rest);
            inner.SetLength(rest);
        }

        string? note = null;
        if (corrupt) note = "The BLF file ended or became unreadable part-way; frames read before that point were indexed.";
        if (unsupported > 0) note = (note == null ? "" : note + " ") + unsupported + " BLF object(s) of unsupported type or compression were skipped.";
        return agg.Build(path, Name, sink, "Vector BLF binary log; positions are frame numbers in file order.", note, truncated);
    }

    private static void ParseObject(CanTraceAggregator agg, ReadOnlySpan<byte> o, int headerSize, uint type, long no, ref long unsupported)
    {
        if (headerSize < 32 || headerSize > o.Length) { if (type is 1 or 2 or 73 or 86 or 100) agg.Unparsed++; return; }
        uint flags = BitConverter.ToUInt32(o[16..]);
        ulong ts = BitConverter.ToUInt64(o[24..]);
        double t = flags == 2 ? ts * 1e-9 : ts * 1e-5;
        ReadOnlySpan<byte> b = o[headerSize..];
        switch (type)
        {
            case 1:      // CAN_MESSAGE
            case 86:     // CAN_MESSAGE2
            {
                if (b.Length < 16) { agg.Unparsed++; return; }
                int ch = BitConverter.ToUInt16(b);
                byte f = b[2], dlc = b[3];
                uint rawId = BitConverter.ToUInt32(b[4..]);
                bool ext = (rawId & 0x80000000u) != 0;
                int n = Math.Min((int)dlc, 8);
                bool remote = (f & 0x80) != 0;
                agg.Add(ch, rawId & 0x1FFFFFFF, ext, false, (f & 1) != 0, remote, remote ? ReadOnlySpan<byte>.Empty : b.Slice(8, n), t, no);
                break;
            }
            case 100:    // CAN_FD_MESSAGE
            {
                if (b.Length < 20 + 64) { agg.Unparsed++; return; }
                int ch = BitConverter.ToUInt16(b);
                byte f = b[2];
                uint rawId = BitConverter.ToUInt32(b[4..]);
                byte fdFlags = b[13], valid = b[14];
                bool ext = (rawId & 0x80000000u) != 0;
                int n = Math.Min((int)valid, 64);
                agg.Add(ch, rawId & 0x1FFFFFFF, ext, (fdFlags & 1) != 0, (f & 1) != 0, false, b.Slice(20, n), t, no);
                break;
            }
            case 2:      // CAN_ERROR
            case 73:     // CAN_ERROR_EXT
                agg.AddError();
                break;
            default:
                if (type is 101 or 103 or 104 or 7 or 9 or 12) unsupported++;   // known frame-like types not decoded
                break;
        }
    }

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
