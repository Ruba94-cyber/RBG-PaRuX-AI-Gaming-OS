using System.IO.Compression;
using System.Text;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CanTraceExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-can-" + Guid.NewGuid().ToString("N"));
    public CanTraceExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(string name, string text) { string p = Path.Combine(_dir, name); File.WriteAllText(p, text, Encoding.Latin1); return p; }

    private const string Header = "date ven ago 07 09:29:00.804 2026\nbase hex  timestamps absolute\ninternal events logged\n";

    [Fact]
    public void Asc_trace_is_condensed_per_id_with_line_positions_and_j1939_decoding()
    {
        string asc = Header +
            "// version 7.5.0\nBegin Triggerblock\n" +
            "   0.000000 1 18FE4A03x Rx d 8 FF DF 7F FF FD FF FF FF\n" +      // line 6
            "   0.010000 1 18FE4A03x Rx d 8 FF DF 7F FF FD FF FF FF\n" +
            "   0.020000 1 18FE4A03x Rx d 8 00 DF 7F FF FD FF FF FE\n" +      // changed payload, line 8
            "   0.030000 1 123 Tx d 2 AA BB\n" +
            "   0.040000 1 ErrorFrame\n" +
            "   0.050000 2 0CFF9D00x Rx r\n" +
            "End TriggerBlock\n";
        string path = Write("t.asc", asc);
        Assert.True(AscTraceExtractor.LooksLikeTrace(path));

        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Equal("can-asc", d.Extractor);
        Assert.Equal(DocumentTier.Structured, d.Tier);
        string all = d.FullText;
        Assert.Contains("Frames read: 5", all);
        Assert.Contains("error frames: 1", all);
        Assert.Contains("CAN ID 0x18FE4A03x", all);
        Assert.Contains("PGN 0xFE4A", all);
        Assert.Contains("source address 0x03", all);
        Assert.Contains("Frames: 3", all);
        Assert.Contains("(line 6)", all);                   // first seen position
        Assert.Contains("00 DF 7F FF FD FF FF FE first at 0.020000 s (line 8)", all);
        Assert.Contains("CAN ID 0x123 (standard 11-bit)", all);
        Assert.Contains(d.Segments, s => s.Position.Heading!.Contains("0x18FE4A03x") && s.Position.Heading.Contains("lines 6-8"));
    }

    [Fact]
    public void Asc_relative_timestamps_accumulate_and_dm1_trouble_codes_are_decoded()
    {
        // DM1 from SA 0x00: lamps byte 0x04 -> red-stop on; DTC SPN 0x0123 (291) FMI 5, occurrences 3
        string asc = "date x\nbase hex  timestamps relative\n" +
            "0.5 1 18FECA00x Rx d 8 04 FF 23 01 05 03 FF FF\n" +
            "0.5 1 18FECA00x Rx d 8 00 FF 00 00 00 00 FF FF\n";
        ExtractedDocument d = StructuredDocumentExtractor.Extract(Write("dm1.asc", asc));
        string all = d.FullText;
        Assert.Contains("SPN 291 FMI 5 (occurrences 3)", all);
        Assert.Contains("red-stop=on", all);
        Assert.Contains("no active DTC", all);
        Assert.Contains("1.000000 s", all);                 // 0.5 + 0.5 accumulated
    }

    [Fact]
    public void Asc_extension_is_blocked_for_pgp_keys_but_allowed_for_can_traces()
    {
        string pgp = Write("key.asc", "-----BEGIN PGP PUBLIC KEY BLOCK-----\nabc\n-----END PGP PUBLIC KEY BLOCK-----\n");
        string trace = Write("trace.asc", Header + "0.0 1 100 Rx d 1 01\n");
        Assert.False(AscTraceExtractor.LooksLikeTrace(pgp));
        Assert.Equal(SensitiveVerdict.Blocked, SensitivePathPolicy.EvaluateEntry("key.asc", false, pgp).Verdict);
        Assert.Equal(SensitiveVerdict.Allowed, SensitivePathPolicy.EvaluateEntry("trace.asc", false, trace).Verdict);
        // without a readable file the old conservative rule still applies
        Assert.Equal(SensitiveVerdict.Blocked, SensitivePathPolicy.EvaluateEntry("trace.asc", false).Verdict);
        // a file named .asc whose content mixes a PGP marker is never treated as a trace
        string fake = Write("fake.asc", "date x\nbase hex\n-----BEGIN PGP PRIVATE KEY BLOCK-----\n");
        Assert.Equal(SensitiveVerdict.Blocked, SensitivePathPolicy.EvaluateEntry("fake.asc", false, fake).Verdict);
    }

    [Fact]
    public void Asc_garbage_lines_are_counted_and_never_fatal()
    {
        string path = Write("g.asc", Header + "1.0 x y z\nnot a frame\n2.0 1 100 Rx d 2 01 02\n3.0 1 200 Rx d 4 01\n");
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Contains("Frames read: 2", d.FullText);
        Assert.Contains("not understood and skipped: 1", d.FullText);
    }

    // ---- BLF ----
    private static byte[] CanObject(ushort ch, uint rawId, byte[] data, ulong ts10us, byte flags = 0)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("LOBJ"u8.ToArray()); w.Write((ushort)32); w.Write((ushort)1); w.Write((uint)(32 + 16)); w.Write((uint)1);
        w.Write((uint)1); w.Write((ushort)0); w.Write((ushort)0); w.Write(ts10us);   // flags=1 -> 10 us ticks
        w.Write(ch); w.Write(flags); w.Write((byte)data.Length); w.Write(rawId);
        var d8 = new byte[8]; Array.Copy(data, d8, data.Length); w.Write(d8);
        return ms.ToArray();
    }

    private static byte[] Container(byte[] inner)
    {
        using var comp = new MemoryStream();
        using (var z = new ZLibStream(comp, CompressionLevel.Optimal, true)) z.Write(inner);
        byte[] c = comp.ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        uint size = (uint)(16 + 16 + c.Length);
        w.Write("LOBJ"u8.ToArray()); w.Write((ushort)16); w.Write((ushort)1); w.Write(size); w.Write((uint)10);
        w.Write((ushort)2); w.Write((ushort)0); w.Write(0u); w.Write((uint)inner.Length); w.Write(0u);
        w.Write(c);
        for (int i = 0; i < ((size & 3) != 0 ? 4 - (int)(size & 3) : 0); i++) w.Write((byte)0);
        return ms.ToArray();
    }

    [Fact]
    public void Blf_objects_split_across_containers_are_reassembled()
    {
        byte[] o1 = CanObject(1, 0x80000000u | 0x18FECA00u, new byte[] { 0x04, 0xFF, 0x23, 0x01, 0x05, 0x03, 0xFF, 0xFF }, 100);
        byte[] o2 = CanObject(1, 0x123, new byte[] { 0xAA, 0xBB }, 200, flags: 1);
        byte[] all = o1.Concat(Pad(o1.Length)).Concat(o2).ToArray();
        int cut = o1.Length + 5;                                    // second object straddles two containers
        using var file = new MemoryStream();
        var head = new byte[144]; "LOGG"u8.CopyTo(head); BitConverter.GetBytes(144u).CopyTo(head, 4);
        file.Write(head);
        file.Write(Container(all[..cut])); file.Write(Container(all[cut..]));
        string path = Path.Combine(_dir, "t.blf"); File.WriteAllBytes(path, file.ToArray());

        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Equal("can-blf", d.Extractor);
        string txt = d.FullText;
        Assert.Contains("Frames read: 2", txt);
        Assert.Contains("CAN ID 0x18FECA00x", txt);
        Assert.Contains("SPN 291 FMI 5", txt);
        Assert.Contains("CAN ID 0x123 (standard 11-bit)", txt);
        Assert.Contains("Rx 0, Tx 1", txt);                         // flags bit 0 = Tx
        Assert.Contains("0.002000 s (frame 2)", txt);               // 200 * 10 us
    }

    private static byte[] Pad(int len) => new byte[len & 3 ? 4 - (len & 3) : 0];

    [Fact]
    public void Blf_without_signature_fails_with_a_reason_and_no_content()
    {
        string path = Path.Combine(_dir, "bad.blf"); File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Equal(DocumentTier.Failed, d.Tier);
        Assert.Contains("LOGG", d.Note);
    }

    [Fact]
    public void Blf_truncated_midway_keeps_frames_read_so_far()
    {
        byte[] o1 = CanObject(1, 0x100, new byte[] { 1 }, 1);
        using var file = new MemoryStream();
        var head = new byte[144]; "LOGG"u8.CopyTo(head); BitConverter.GetBytes(144u).CopyTo(head, 4);
        file.Write(head);
        file.Write(Container(o1));
        file.Write("LOBJ"u8.ToArray()); file.Write(new byte[8]);   // cut off header of a further container
        string path = Path.Combine(_dir, "cut.blf"); File.WriteAllBytes(path, file.ToArray());
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Contains("Frames read: 1", d.FullText);
    }

    // ---- real files supplied by the owner (skipped when absent, e.g. on CI) ----
    [Theory]
    [InlineData(@"C:\Users\DTS\Desktop\CAN-20260807-092922.asc")]
    [InlineData(@"C:\Users\DTS\Desktop\CAN-20260730-123749.asc")]
    [InlineData(@"C:\Users\DTS\Desktop\DTC DMS Can Recording.asc")]
    public void Owner_asc_traces_are_indexed_when_present(string path)
    {
        if (!File.Exists(path)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        sw.Stop();
        Assert.Equal(DocumentTier.Structured, d.Tier);
        Assert.True(d.Segments.Count > 1);
        Assert.Equal("can-asc", d.Extractor);
        string outDir = Environment.GetEnvironmentVariable("PARUX_CAN_PROBE_OUT") ?? "";
        if (outDir.Length > 0)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, Path.GetFileName(path) + ".index.txt"),
                $"extract ms: {sw.ElapsedMilliseconds}; segments: {d.Segments.Count}; truncated: {d.Truncated}; note: {d.Note}\n\n" +
                string.Join("\n-----\n", d.Segments.Select(s => "[" + s.Position.Describe() + "]\n" + s.Text)));
        }
    }

    [Fact]
    public void Owner_dbc_is_indexed_when_present()
    {
        const string path = @"C:\Users\DTS\Desktop\DBdaily24full.dbc";
        if (!File.Exists(path)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        sw.Stop();
        Assert.Equal("dbc", d.Extractor);
        Assert.True(d.Segments.Count > 100);
        string outDir = Environment.GetEnvironmentVariable("PARUX_CAN_PROBE_OUT") ?? "";
        if (outDir.Length > 0)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "dbc.summary.txt"),
                $"extract ms: {sw.ElapsedMilliseconds}; segments: {d.Segments.Count}; truncated: {d.Truncated}; note: {d.Note}; chars: {d.FullText.Length}");
        }
    }
}
