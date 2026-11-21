using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

public sealed record WebSourceInfo(
    string Id, string Url, string Mode, int MaxPages, int MaxDepth, string Folder,
    string State, DateTime? LastSyncUtc, int Pages, string? Error, string? StoppedBecause,
    int Written, int Unchanged, int Skipped, int Failed, int KeptEdited, int Removed);

/// <summary>
/// "Add a website as knowledge": a page or a bounded same-site crawl is saved as plain-text files in a folder PaRuX owns,
/// and that folder is an ordinary Local Knowledge source, so indexing, search and citations work as for any folder.
/// Everything is user-initiated (add, re-sync); there is no background polling. Blocked by strict offline mode and by
/// Gaming Mode, with an honest message. Re-sync only rewrites files PaRuX wrote and never touches a copy the person edited.
/// </summary>
public sealed class WebKnowledgeSources
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _manifestDirectory;
    private readonly string _contentRoot;
    private readonly Func<string, string> _registerFolder;     // folder -> source id (the Local Knowledge source)
    private readonly Action<string> _unregister;               // source id
    private readonly Func<string, bool> _startIndex;           // source id -> started
    private readonly Func<bool> _offlineBlocked;
    private readonly Func<bool> _gamingActive;
    private readonly Func<WebCrawlOptions, WebKnowledgeCrawler> _crawlerFactory;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly object _gate = new();

    private sealed class Job
    {
        public CancellationTokenSource Cts = new();
        public Task Task = Task.CompletedTask;
        public string State = "crawling";
        public string? Error;
        public WebCrawlReport? Report;
    }

    public WebKnowledgeSources(
        string manifestDirectory, string contentRoot,
        Func<string, string> registerFolder, Action<string> unregister, Func<string, bool> startIndex,
        Func<bool>? offlineBlocked = null, Func<bool>? gamingActive = null,
        Func<WebCrawlOptions, WebKnowledgeCrawler>? crawlerFactory = null)
    {
        _manifestDirectory = manifestDirectory;
        _contentRoot = contentRoot;
        _registerFolder = registerFolder;
        _unregister = unregister;
        _startIndex = startIndex;
        _offlineBlocked = offlineBlocked ?? (() => OfflinePolicy.IsBlocked());
        _gamingActive = gamingActive ?? GamingHardOffWatch.MarkerActive;
        _crawlerFactory = crawlerFactory ?? (options => new WebKnowledgeCrawler(options, gamingActive: _gamingActive));
    }

    public static string DefaultContentRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PaRuX", "Web knowledge");

    // ------------------------------------------------------------------ add / resync / remove

    /// <summary>Validates, registers the folder as a source and starts the fetch in the background.</summary>
    public WebSourceInfo Begin(string url, string mode, int? maxPages, int? maxDepth)
    {
        EnsureAllowed();
        var crawlerProbe = _crawlerFactory(WebCrawlOptions.SinglePage);
        if (!WebUrl.TryValidate(url, crawlerProbe.AddressRule, out Uri uri, out string error))
            throw new ArgumentException(error);
        bool crawl = string.Equals(mode, "crawl", StringComparison.OrdinalIgnoreCase);
        if (!crawl && !string.Equals(mode, "page", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The mode must be \"page\" or \"crawl\".");

        int pages = crawl ? Math.Clamp(maxPages ?? 25, 1, WebCrawlOptions.HardMaxPages) : 1;
        int depth = crawl ? Math.Clamp(maxDepth ?? 2, 0, WebCrawlOptions.HardMaxDepth) : 0;

        string folder = Path.Combine(_contentRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (SensitivePathPolicy.EvaluateFolder(folder).Verdict == SensitiveVerdict.Blocked)
        {
            TryDeleteEmpty(folder);
            throw new InvalidOperationException("The folder for saved web pages is not allowed as a knowledge source on this PC.");
        }

        string id = _registerFolder(folder);
        var manifest = new WebSourceManifest
        {
            Id = id, Url = WebUrl.Normalize(uri), Mode = crawl ? "crawl" : "page", MaxPages = pages, MaxDepth = depth, Folder = folder
        };
        Save(manifest);
        Launch(manifest);
        return Describe(manifest);
    }

    /// <summary>Fetches the source again. Unchanged pages cost one conditional request; edited copies are kept.</summary>
    public WebSourceInfo? Resync(string id)
    {
        EnsureAllowed();
        WebSourceManifest? manifest = Load(id);
        if (manifest == null) return null;
        if (_jobs.TryGetValue(id, out Job? running) && !running.Task.IsCompleted) return Describe(manifest);
        Launch(manifest);
        return Describe(manifest);
    }

    public IReadOnlyList<WebSourceInfo> List() =>
        ReadAll().Select(Describe).OrderByDescending(i => i.LastSyncUtc ?? DateTime.MinValue).ToList();

    public WebSourceInfo? Get(string id) => Load(id) is { } m ? Describe(m) : null;

    /// <summary>Test seam and shutdown helper: waits for a source's running fetch.</summary>
    public Task WaitAsync(string id) => _jobs.TryGetValue(id, out Job? job) ? job.Task : Task.CompletedTask;

    /// <summary>Stops the fetch, removes the Local Knowledge source and deletes only the files PaRuX wrote (not edited copies).</summary>
    public bool Remove(string id)
    {
        WebSourceManifest? manifest = Load(id);
        if (manifest == null) return false;
        if (_jobs.TryRemove(id, out Job? job))
        {
            try { job.Cts.Cancel(); job.Task.Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }
        _unregister(id);
        foreach (WebPageRecord record in manifest.Pages.Values)
        {
            string path = Path.Combine(manifest.Folder, record.FileName);
            try
            {
                if (File.Exists(path) && Sha(File.ReadAllBytes(path)) == record.WrittenSha256) File.Delete(path);
            }
            catch (IOException) { }
        }
        TryDeleteEmpty(manifest.Folder);
        try { File.Delete(ManifestPath(id)); } catch (IOException) { }
        return true;
    }

    /// <summary>The page address a saved file came from, for showing the URL next to a citation.</summary>
    public string? TryGetUrl(string filePath)
    {
        try
        {
            string full = Path.GetFullPath(filePath);
            foreach (WebSourceManifest manifest in ReadAll())
            {
                string root = Path.GetFullPath(manifest.Folder);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(full);
                WebPageRecord? page = manifest.Pages.Values.FirstOrDefault(p => string.Equals(p.FileName, name, StringComparison.OrdinalIgnoreCase));
                if (page != null) return page.Url;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        return null;
    }

    // ------------------------------------------------------------------ internals

    private void EnsureAllowed()
    {
        if (_offlineBlocked())
            throw new OfflineBlockedException("Strict offline mode is on, so websites cannot be fetched. Nothing was downloaded. Turn strict offline mode off in Document Settings to add a website.");
        if (_gamingActive())
            throw new UnavailableDuringGamingException("Gaming Mode is active, so PaRuX does not fetch websites. Try again when the game closes.");
    }

    private void Launch(WebSourceManifest manifest)
    {
        var job = new Job();
        _jobs[manifest.Id] = job;
        job.Task = Task.Run(async () =>
        {
            try
            {
                WebKnowledgeCrawler crawler = _crawlerFactory(WebCrawlOptions.Crawl(manifest.MaxPages, manifest.MaxDepth));
                if (manifest.Mode == "page") crawler = _crawlerFactory(WebCrawlOptions.SinglePage);
                job.Report = await crawler.CrawlAsync(new Uri(manifest.Url), manifest.Folder, manifest, job.Cts.Token).ConfigureAwait(false);
                lock (_gate) Save(manifest);
                if (job.Report.StoppedBecause == "gaming") { job.State = "stopped"; job.Error = "gaming_active"; return; }
                job.State = "indexing";
                _startIndex(manifest.Id);
                job.State = "ready";
            }
            catch (OperationCanceledException) { job.State = "stopped"; }
            catch (Exception ex) { job.State = "failed"; job.Error = ex.GetType().Name; }
        });
    }

    private WebSourceInfo Describe(WebSourceManifest m)
    {
        _jobs.TryGetValue(m.Id, out Job? job);
        WebCrawlReport? r = job?.Report;
        string state = job?.State ?? (m.LastSyncUtc.HasValue ? "ready" : "not_synced");
        return new WebSourceInfo(m.Id, m.Url, m.Mode, m.MaxPages, m.MaxDepth, m.Folder, state, m.LastSyncUtc, m.Pages.Count,
            job?.Error, r?.StoppedBecause, r?.Written ?? 0, r?.Unchanged ?? 0, r?.Skipped ?? 0, r?.Failed ?? 0, r?.KeptEdited ?? 0, r?.Removed ?? 0);
    }

    private string ManifestPath(string id) => Path.Combine(_manifestDirectory, SafeId(id) + ".json");

    private static string SafeId(string id) => new(id.Where(char.IsLetterOrDigit).ToArray());

    private void Save(WebSourceManifest manifest)
    {
        Directory.CreateDirectory(_manifestDirectory);
        string temp = ManifestPath(manifest.Id) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, Json));
        File.Move(temp, ManifestPath(manifest.Id), overwrite: true);
    }

    private WebSourceManifest? Load(string id)
    {
        try
        {
            string path = ManifestPath(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<WebSourceManifest>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private IEnumerable<WebSourceManifest> ReadAll()
    {
        if (!Directory.Exists(_manifestDirectory)) yield break;
        foreach (string file in Directory.EnumerateFiles(_manifestDirectory, "*.json"))
        {
            WebSourceManifest? m = null;
            try { m = JsonSerializer.Deserialize<WebSourceManifest>(File.ReadAllText(file), Json); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            if (m != null && !string.IsNullOrEmpty(m.Id)) yield return m;
        }
    }

    private static void TryDeleteEmpty(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (IOException) { }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
