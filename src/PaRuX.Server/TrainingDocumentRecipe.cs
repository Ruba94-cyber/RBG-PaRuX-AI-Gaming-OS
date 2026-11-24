using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using UglyToad.PdfPig;

namespace PaRuX.Server;

public sealed class TrainingDocumentRecipeRequest
{
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>Target characters per training row (500 - 8,000).</summary>
    public int ChunkCharacters { get; set; } = 2000;
    /// <summary>Characters repeated between neighbouring rows so a sentence is not cut off from its context.</summary>
    public int OverlapCharacters { get; set; } = 200;
    /// <summary>True (default): return the preview only and write nothing.</summary>
    public bool PreviewOnly { get; set; } = true;
}

public sealed class TrainingDocumentRecipePreview
{
    public string Method { get; set; } = TrainingMethod.ContinuedPretraining;
    public string SourceFormat { get; set; } = string.Empty;
    public int ExtractedCharacters { get; set; }
    public int Rows { get; set; }
    public bool Truncated { get; set; }
    /// <summary>First rows as they would be written (each at most 400 characters shown).</summary>
    public List<string> Preview { get; set; } = new();
    public string? TrainJsonlPath { get; set; }
    public string? Sha256 { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Turns a local PDF / DOCX / text document into a continued-pretraining dataset (<c>{"text": ...}</c> rows) with a
/// preview before anything is written. It teaches a model the document's wording and facts; it does NOT invent
/// question/answer pairs (that needs a model and human review, which PaRuX does not claim here). Scanned PDFs have no
/// text layer and are refused rather than OCR-guessed. Output goes only under PaRuX-managed data, never beside the
/// source.
/// </summary>
public static class TrainingDocumentRecipe
{
    internal const int MaxPdfPages = 400;
    internal const long MaxSourceBytes = 64L * 1024 * 1024;
    internal const int MaxRows = 20_000;
    internal const int MinimumCharacters = 200;
    private const int PreviewRows = 5;
    private const int PreviewChars = 400;

    public static async Task<TrainingDocumentRecipePreview> BuildAsync(
        TrainingDocumentRecipeRequest request, string? outputRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
            throw new FileNotFoundException("Select a real local document first.", request.SourcePath);
        string path = Path.GetFullPath(request.SourcePath);
        if (new FileInfo(path).Length > MaxSourceBytes)
            throw new InvalidDataException("Document recipe inputs are limited to 64 MiB.");
        if (request.ChunkCharacters is < 500 or > 8000) throw new InvalidDataException("chunkCharacters must be between 500 and 8000.");
        if (request.OverlapCharacters < 0 || request.OverlapCharacters > request.ChunkCharacters / 2)
            throw new InvalidDataException("overlapCharacters must be between 0 and half of chunkCharacters.");

        string extension = Path.GetExtension(path).ToLowerInvariant();
        bool truncated = false;
        string text = await Task.Run(() =>
        {
            if (extension == ".pdf") return ExtractPdf(path, ct, out truncated);
            if (!DocumentTextExtractor.CanExtract(path) || extension is ".xlsx" or ".xls" or ".xlsb" or ".pptx")
                throw new InvalidDataException("Document recipes accept PDF, DOCX, TXT, MD, CSV and JSON documents.");
            string extracted = DocumentTextExtractor.Extract(path, ct);
            truncated = extracted.EndsWith("[Document truncated at 120,000 characters]", StringComparison.Ordinal);
            return extracted;
        }, ct).ConfigureAwait(false);

        text = Normalize(text);
        if (text.Length < MinimumCharacters)
            throw new InvalidDataException(extension == ".pdf"
                ? "No usable text was found. A scanned PDF has no text layer; run OCR on it first. Nothing was written."
                : "The document has too little text to train on. Nothing was written.");

        List<string> chunks = Chunk(text, request.ChunkCharacters, request.OverlapCharacters);
        if (chunks.Count > MaxRows) throw new InvalidDataException("The document produces more than 20,000 rows. Split it first.");
        var lines = chunks.Select(c => JsonSerializer.Serialize(new { text = c })).ToList();

        var result = new TrainingDocumentRecipePreview
        {
            SourceFormat = extension.TrimStart('.'),
            ExtractedCharacters = text.Length,
            Rows = lines.Count,
            Truncated = truncated,
            Preview = chunks.Take(PreviewRows).Select(c => c.Length <= PreviewChars ? c : c[..PreviewChars] + "...").ToList(),
            Note = "Continued-pretraining rows only; no question/answer pairs were generated."
                + (truncated ? " The document was longer than the extraction cap and was cut." : string.Empty)
        };
        if (request.PreviewOnly) return result;

        string root = outputRoot ?? Path.Combine(PaRuXDataRoot.Path, "datasets", "training-recipes");
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, Guid.NewGuid().ToString("N") + ".continued-pretraining.jsonl");
        string staging = destination + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(staging, lines, new UTF8Encoding(false), ct).ConfigureAwait(false);
            string hash;
            await using (FileStream stream = File.OpenRead(staging))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            ct.ThrowIfCancellationRequested();
            File.Move(staging, destination);
            result.TrainJsonlPath = destination;
            result.Sha256 = hash;
            return result;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    internal static string ExtractPdf(string path, CancellationToken ct, out bool truncated)
    {
        truncated = false;
        var builder = new StringBuilder();
        try
        {
            using PdfDocument pdf = PdfDocument.Open(path);
            int pages = 0;
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (++pages > MaxPdfPages) { truncated = true; break; }
                builder.AppendLine(page.Text);
                builder.AppendLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("The PDF could not be read (damaged or encrypted).");
        }
        return builder.ToString();
    }

    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        int blank = 0;
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = new string(raw.Where(ch => !char.IsControl(ch) || ch == '\t').ToArray()).Trim();
            if (line.Length == 0) { if (++blank <= 1 && builder.Length > 0) builder.Append('\n'); continue; }
            blank = 0;
            builder.Append(line).Append('\n');
        }
        return builder.ToString().Trim();
    }

    /// <summary>Splits on paragraph, then sentence, then hard boundaries so a row ends on natural text when possible.</summary>
    internal static List<string> Chunk(string text, int size, int overlap)
    {
        var chunks = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(text.Length, start + size);
            if (end < text.Length)
            {
                int window = Math.Max(start + size / 2, start + 1);
                int cut = text.LastIndexOf("\n\n", end - 1, end - window, StringComparison.Ordinal);
                if (cut < window) cut = text.LastIndexOf(". ", end - 1, end - window, StringComparison.Ordinal) + 1;
                if (cut >= window) end = Math.Min(text.Length, cut + 1);
            }
            string chunk = text[start..end].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            if (end >= text.Length) break;
            start = Math.Max(end - overlap, start + 1);
        }
        return chunks;
    }
}
