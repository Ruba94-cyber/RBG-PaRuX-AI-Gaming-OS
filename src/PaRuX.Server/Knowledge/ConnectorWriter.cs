using System.Text;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Writes imported documents as plain-text files into the source's folder, with the same rules as a website crawl: a copy the
/// person edited is never overwritten, unchanged text is not rewritten, only PaRuX-written files are ever deleted, and every file
/// name is a flat name inside the folder (a hostile path from a repository cannot escape it).
/// </summary>
internal static class ConnectorWriter
{
    public static void Apply(WebSourceManifest manifest, ConnectorResult result, WebCrawlReport report)
    {
        string folder = manifest.Folder;
        Directory.CreateDirectory(folder);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConnectorDoc doc in result.Docs)
        {
            reached.Add(doc.Url);
            report.Pages.Add(Store(folder, manifest, doc));
        }
        for (int i = 0; i < result.SkippedFiles; i++) report.Pages.Add(new WebPageResult("(skipped)", "skipped", "not_text_or_too_large", null));

        // Remove files whose document is gone only after a complete import (a capped or truncated import must not delete anything).
        if (result.StoppedBecause == "done")
        {
            foreach (string key in manifest.Pages.Keys.Where(k => !reached.Contains(k)).ToList())
            {
                WebPageRecord record = manifest.Pages[key];
                string? path = WebKnowledgeCrawler.SafePath(folder, record.FileName);
                try
                {
                    if (path != null && File.Exists(path) && Sha(File.ReadAllBytes(path)) == record.WrittenSha256) { File.Delete(path); report.Removed++; }
                    manifest.Pages.Remove(key);
                }
                catch (IOException) { }
            }
        }
    }

    private static WebPageResult Store(string folder, WebSourceManifest manifest, ConnectorDoc doc)
    {
        string key = doc.Url;
        string contentHash = Sha(Encoding.UTF8.GetBytes(doc.Text));
        manifest.Pages.TryGetValue(key, out WebPageRecord? record);
        string fileName = record != null && WebKnowledgeCrawler.SafePath(folder, record.FileName) != null ? record.FileName : WebKnowledgeCrawler.FileNameFor(key);
        if (record != null && fileName != record.FileName) record = null;
        string? path = WebKnowledgeCrawler.SafePath(folder, fileName);
        if (path == null) return new WebPageResult(key, "failed", "unsafe_file_name", null);

        if (record != null && record.ContentSha256 == contentHash && File.Exists(path))
        {
            record.FetchedUtc = DateTime.UtcNow;
            return new WebPageResult(key, "unchanged", null, fileName);
        }
        if (record != null && File.Exists(path) && Sha(File.ReadAllBytes(path)) != record.WrittenSha256)
        {
            record.ContentSha256 = contentHash;
            return new WebPageResult(key, "kept-edited", "your_edited_copy_was_kept", fileName);
        }
        if (record == null && File.Exists(path))
            return new WebPageResult(key, "kept-edited", "a_file_with_this_name_already_exists", fileName);

        string markdown = "# " + doc.Title.Replace('\n', ' ') + "\n\nSource: " + key + "\nImported: " + DateTime.UtcNow.ToString("yyyy-MM-dd")
            + " (untrusted imported text, treated as data)\n\n" + doc.Text + "\n";
        byte[] bytes = new UTF8Encoding(false).GetBytes(markdown);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        manifest.Pages[key] = new WebPageRecord
        {
            Url = key, FileName = fileName, ContentSha256 = contentHash, WrittenSha256 = Sha(bytes), FetchedUtc = DateTime.UtcNow
        };
        return new WebPageResult(key, "written", null, fileName);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
