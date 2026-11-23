using System.Text;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// PowerPoint 97-2003 (".ppt"). A .ppt appends every save to the same stream, so one file holds several revisions of the deck.
/// The live revision is found the way PowerPoint does it: "Current User" points to the newest UserEditAtom, whose persist
/// directory (merged with the older edits it chains to) maps object ids to the offsets of the current Document, slides and notes.
/// Only those objects are read: text of each slide (outline list and shapes) and its speaker notes, in slide order. Master and
/// template text ("Click to edit Master title style") is not indexed. A file whose chain is unreadable falls back to its last revision.
/// </summary>
internal sealed class LegacyPptExtractor : IDocumentExtractor
{
    public string Name => "ppt97";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".ppt", StringComparison.OrdinalIgnoreCase) && CompoundFile.HasSignature(path);

    private const int RtDocument = 0x03E8, RtSlide = 0x03EE, RtNotes = 0x03F0, RtSlideAtom = 0x03EF, RtSlidePersist = 0x03F3,
        RtSlideList = 0x0FF0, RtTextChars = 0x0FA0, RtTextBytes = 0x0FA8, RtUserEdit = 0x0FF5, RtPersistDir = 0x1772;

    private byte[] _d = Array.Empty<byte>();
    private CancellationToken _ct;

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _ct = ct;
        var cf = new CompoundFile(CompoundFile.ReadBounded(path));
        var main = cf.Child(cf.Root, "PowerPoint Document") ?? throw new InvalidDataException("No PowerPoint Document stream.");
        _d = cf.ReadStream(main);
        var cur = cf.Child(cf.Root, "Current User");
        byte[]? currentUser = cur == null ? null : cf.ReadStream(cur);

        var slides = new List<(string Label, List<string> Texts)>();
        bool ok = false;
        try { ok = ReadLiveRevision(currentUser, slides); }
        catch (InvalidDataException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        { return StructuredDocumentExtractor.Failed(path, "The presentation is password protected (encrypted); nothing was indexed.", Name); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException) { ok = false; }
        string? note = null;
        if (!ok || slides.Count == 0)
        {
            slides.Clear(); ReadLastRevisionFallback(slides);
            note = "The edit history of this presentation could not be followed; the text of its last saved revision was read instead.";
        }

        var sink = new AutomotiveSink(limits);
        string name = Path.GetFileName(path);
        foreach (var (label, texts) in slides)
            if (texts.Count > 0 && !sink.Add($"{name} - {label}:\n" + string.Join("\n", texts), $"{name} {label}")) break;
        return sink.Build(path, Name, sink.SegmentCount == 0 ? "No text found in this presentation." : note);
    }

    private bool ReadLiveRevision(byte[]? currentUser, List<(string Label, List<string> Texts)> slides)
    {
        // Current User: record header(8: type 0x0FF6) size(4) headerToken(4) offsetToCurrentEdit(4) ...
        if (currentUser == null || currentUser.Length < 20 || BitConverter.ToUInt16(currentUser, 2) != 0x0FF6) return false;
        if (BitConverter.ToUInt32(currentUser, 12) == 0xF3D1C4DF) throw new InvalidDataException("The presentation is password protected (encrypted).");
        long editOffset = BitConverter.ToUInt32(currentUser, 16);
        var persist = new Dictionary<uint, long>();
        uint docId = 0; int guard = 0; bool first = true;
        while (editOffset > 0 && editOffset + 8 <= _d.Length && guard++ < 512)
        {
            if (BitConverter.ToUInt16(_d, (int)editOffset + 2) != RtUserEdit) return false;
            int b = (int)editOffset + 8;
            uint offLastEdit = BitConverter.ToUInt32(_d, b + 8), offDir = BitConverter.ToUInt32(_d, b + 12), docRef = BitConverter.ToUInt32(_d, b + 16);
            if (first) { docId = docRef; first = false; }
            ReadPersistDirectory((int)offDir, persist);
            editOffset = offLastEdit;
        }
        if (docId == 0 || !persist.TryGetValue(docId, out long docOffset)) return false;

        // Document -> SlideListWithText (instance 0: slides) with one SlidePersistAtom per slide, in slide order
        var slideRefs = new List<(uint PersistId, uint SlideId, List<string> ListText)>();
        var notesBySlideId = new Dictionary<uint, uint>();
        CollectSlideList((int)docOffset, slideRefs, notesBySlideId);
        if (slideRefs.Count == 0) return false;
        int n = 0;
        foreach (var (persistId, slideId, listText) in slideRefs)
        {
            _ct.ThrowIfCancellationRequested();
            n++;
            var texts = new List<string>();
            foreach (string t in listText) AddUnique(texts, t);
            uint notesRef = 0;
            if (persist.TryGetValue(persistId, out long slideOffset))
                foreach (string t in TextsOf((int)slideOffset)) AddUnique(texts, t);
            notesBySlideId.TryGetValue(slideId, out notesRef);   // the notes list is keyed by the slide id of the slide it belongs to
            slides.Add(($"slide {n}", texts));
            if (notesRef != 0 && persist.TryGetValue(notesRef, out long notesOffset))
            {
                var noteTexts = new List<string>();
                foreach (string t in TextsOf((int)notesOffset)) AddUnique(noteTexts, t);
                if (noteTexts.Count > 0) slides.Add(($"speaker notes of slide {n}", noteTexts));
            }
        }
        return true;
    }

    private void ReadPersistDirectory(int offset, Dictionary<uint, long> map)
    {
        if (offset <= 0 || offset + 8 > _d.Length || BitConverter.ToUInt16(_d, offset + 2) != RtPersistDir) return;
        int p = offset + 8, end = (int)Math.Min((long)p + BitConverter.ToUInt32(_d, offset + 4), _d.Length);
        while (p + 4 <= end)
        {
            uint head = BitConverter.ToUInt32(_d, p); p += 4;
            uint id = head & 0xFFFFF; int count = (int)(head >> 20);
            for (int i = 0; i < count && p + 4 <= end; i++, p += 4)
            {
                uint pid = id + (uint)i;
                if (!map.ContainsKey(pid)) map[pid] = BitConverter.ToUInt32(_d, p);   // newer edits are read first and win
            }
        }
    }

    private void CollectSlideList(int docOffset, List<(uint PersistId, uint SlideId, List<string> Texts)> result, Dictionary<uint, uint> notesBySlideId)
    {
        int end = RecordEnd(docOffset);
        int p = docOffset + 8;
        while (p + 8 <= end)
        {
            int verInst = BitConverter.ToUInt16(_d, p), type = BitConverter.ToUInt16(_d, p + 2); long len = BitConverter.ToUInt32(_d, p + 4);
            int recEnd = (int)Math.Min(p + 8 + len, end);
            int listKind = verInst >> 4;
            if (type == RtSlideList && listKind is 0 or 2)
            {
                int q = p + 8; List<string>? current = null; uint persistId = 0, slideId = 0;
                while (q + 8 <= recEnd)
                {
                    int t = BitConverter.ToUInt16(_d, q + 2); long l = BitConverter.ToUInt32(_d, q + 4);
                    int bodyEnd = (int)Math.Min(q + 8 + l, recEnd);
                    if (t == RtSlidePersist && bodyEnd - (q + 8) >= 4)
                    {
                        if (current != null && listKind == 0) result.Add((persistId, slideId, current));
                        persistId = BitConverter.ToUInt32(_d, q + 8);
                        slideId = bodyEnd - (q + 8) >= 16 ? BitConverter.ToUInt32(_d, q + 8 + 12) : 0;
                        if (listKind == 2) notesBySlideId[slideId] = persistId;
                        current = new List<string>();
                    }
                    else if (current != null && t == RtTextChars && bodyEnd - (q + 8) >= 2) AddUnique(current, Clean(Encoding.Unicode.GetString(_d, q + 8, (bodyEnd - (q + 8)) & ~1)));
                    else if (current != null && t == RtTextBytes && bodyEnd > q + 8) AddUnique(current, Clean(Encoding.GetEncoding(1252).GetString(_d, q + 8, bodyEnd - (q + 8))));
                    q = (int)Math.Min(q + 8 + l, int.MaxValue);
                }
                if (current != null && listKind == 0) result.Add((persistId, slideId, current));
            }
            p = recEnd;
        }
    }

    private int RecordEnd(int offset) => (int)Math.Min((long)offset + 8 + BitConverter.ToUInt32(_d, offset + 4), _d.Length);

    /// <summary>Every text atom inside a container, in order (slide shapes, text boxes, notes).</summary>
    private IEnumerable<string> TextsOf(int offset)
    {
        var result = new List<string>();
        Walk(offset + 8, RecordEnd(offset), 0, result);
        return result;
    }

    private void Walk(int start, int end, int depth, List<string> result)
    {
        int p = start;
        while (p + 8 <= end && depth < 24)
        {
            _ct.ThrowIfCancellationRequested();
            int verInst = BitConverter.ToUInt16(_d, p), type = BitConverter.ToUInt16(_d, p + 2); long len = BitConverter.ToUInt32(_d, p + 4);
            int body = p + 8; int bodyEnd = (int)Math.Min(body + len, end);
            if ((verInst & 0xF) == 0xF) Walk(body, bodyEnd, depth + 1, result);
            else if (type == RtTextChars && bodyEnd - body >= 2) AddUnique(result, Clean(Encoding.Unicode.GetString(_d, body, (bodyEnd - body) & ~1)));
            else if (type == RtTextBytes && bodyEnd > body) AddUnique(result, Clean(Encoding.GetEncoding(1252).GetString(_d, body, bodyEnd - body)));
            p = (int)Math.Min(body + len, int.MaxValue);
        }
    }

    private void ReadLastRevisionFallback(List<(string Label, List<string> Texts)> slides)
    {
        // Start at the last top-level Document container: it belongs to the newest save.
        int last = 0;
        for (int p = 0; p + 8 <= _d.Length;)
        {
            int verInst = BitConverter.ToUInt16(_d, p), type = BitConverter.ToUInt16(_d, p + 2); long len = BitConverter.ToUInt32(_d, p + 4);
            if (type == RtDocument && (verInst & 0xF) == 0xF) last = p;
            long next = p + 8 + len; if (next <= p || next > _d.Length) break; p = (int)next;
        }
        var refs = new List<(uint PersistId, uint SlideId, List<string> Texts)>();
        CollectSlideList(last, refs, new Dictionary<uint, uint>());
        int n = 0;
        foreach (var (_, _, texts) in refs) slides.Add(($"slide {++n}", texts));
    }

    private static string Clean(string t) => t.Replace('\r', '\n').Replace('\u000B', '\n').Trim('\0', ' ', '\n');
    private static void AddUnique(List<string> list, string t) { if (t.Length > 0 && !list.Contains(t)) list.Add(t); }
}
