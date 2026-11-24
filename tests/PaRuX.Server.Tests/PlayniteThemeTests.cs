using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Playnite;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PlayniteThemeFileTests
{
    private const string Folder = "Aniki_ReMake_bb8728bd-ac83-4324-88b1-ee5c586527d1";
    private const string Asset = Folder + "_3_6_0.pthm";
    private const string Url = "https://github.com/Mike-Aniki/Aniki-ReMake/releases/download/v3.6.0/" + Asset;

    private static string Release(string? digest, string url = Url, string name = Asset, bool prerelease = false) =>
        "{\"tag_name\":\"v3.6.0\",\"prerelease\":" + (prerelease ? "true" : "false") + ",\"draft\":false,\"assets\":[" +
        "{\"name\":\"" + name + "\",\"size\":380465812,\"digest\":" + (digest is null ? "null" : "\"" + digest + "\"") + ",\"browser_download_url\":\"" + url + "\"}]}";

    [Fact]
    public void Parser_picks_the_theme_package_with_its_published_checksum_and_folder_name()
    {
        PlayniteThemePlanDto p = PlayniteThemeReleaseParser.Parse(Release("sha256:" + new string('b', 64)));
        Assert.True(p.Available);
        Assert.Equal("v3.6.0", p.Version);
        Assert.Equal(Folder, p.ThemeFolderName);
        Assert.Equal(new string('b', 64), p.Sha256);
        Assert.Equal("MIT", p.License);
        Assert.Equal("https://github.com/Mike-Aniki/Aniki-ReMake", p.SourceUrl);
    }

    [Fact]
    public void Parser_reports_a_missing_checksum_as_unverifiable_and_refuses_other_hosts_repos_and_prereleases()
    {
        Assert.NotNull(PlayniteThemeReleaseParser.Parse(Release(null)).Message);
        Assert.False(PlayniteThemeReleaseParser.Parse(Release(null, url: "https://evil.example/" + Asset)).Available);
        Assert.False(PlayniteThemeReleaseParser.Parse(Release(null, url: "https://github.com/Other/Repo/releases/download/v1/" + Asset)).Available);
        Assert.False(PlayniteThemeReleaseParser.Parse(Release(null, url: "http://github.com/Mike-Aniki/Aniki-ReMake/releases/download/v3.6.0/" + Asset)).Available);
        Assert.False(PlayniteThemeReleaseParser.Parse(Release(null, name: "Other.pthm", url: Url.Replace(Asset, "Other.pthm"))).Available);
        Assert.False(PlayniteThemeReleaseParser.Parse(Release(null, prerelease: true)).Available);
        Assert.False(PlayniteThemeReleaseParser.Parse("{ nonsense").Available);
    }

    [Theory]
    [InlineData("Views/Main.xaml", true)]
    [InlineData("theme.yaml", true)]
    [InlineData("../evil.txt", false)]
    [InlineData("a/../../evil.txt", false)]
    [InlineData("a\\..\\evil.txt", false)]
    [InlineData("/abs.txt", false)]
    [InlineData("\\abs.txt", false)]
    [InlineData("C:/x.txt", false)]
    [InlineData("file.txt:stream", false)]
    [InlineData("trailing./x", false)]
    [InlineData("", false)]
    public void Entry_names_are_checked_for_traversal_and_windows_tricks(string name, bool safe) =>
        Assert.Equal(safe, PlayniteThemeArchive.IsSafeEntryName(name));

    [Fact]
    public void Config_splice_changes_only_the_top_level_Theme_value_and_keeps_bom_and_formatting()
    {
        string text = "{\r\n  \"Monitor\": 2,\r\n  \"Filter\": { \"Theme\": \"nested-must-stay\" },\r\n  \"Theme\"  :   \"Old_Theme\",\r\n  \"Rows\": 2\r\n}";
        byte[] original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        byte[]? changed = PlayniteThemeConfig.WithTheme(original, Folder);
        Assert.NotNull(changed);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, changed!.Take(3).ToArray());
        string result = Encoding.UTF8.GetString(changed, 3, changed.Length - 3);
        Assert.Equal(text.Replace("\"Old_Theme\"", "\"" + Folder + "\""), result);
        Assert.Contains("nested-must-stay", result);
    }

    [Fact]
    public void Config_splice_refuses_missing_theme_unsafe_names_and_garbage()
    {
        Assert.Null(PlayniteThemeConfig.WithTheme(Encoding.UTF8.GetBytes("{\"Rows\":2}"), Folder));
        Assert.Null(PlayniteThemeConfig.WithTheme(Encoding.UTF8.GetBytes("{\"Theme\":\"a\"}"), "..\\evil"));
        Assert.Null(PlayniteThemeConfig.WithTheme(Encoding.UTF8.GetBytes("{\"Theme\":\"a\"}"), "x\",\"Evil\":\"1"));
        Assert.Null(PlayniteThemeConfig.WithTheme(Encoding.UTF8.GetBytes("not json"), Folder));
        Assert.Null(PlayniteThemeConfig.WithTheme(Encoding.UTF8.GetBytes("{\"Theme\":5}"), Folder));
    }
}

public sealed class PlayniteThemeServiceTests : IDisposable
{
    private const string Folder = "Aniki_ReMake_bb8728bd-ac83-4324-88b1-ee5c586527d1";
    private const string Asset = Folder + "_3_6_0.pthm";
    private const string Url = "https://github.com/Mike-Aniki/Aniki-ReMake/releases/download/v3.6.0/" + Asset;
    private const string MitText =
        "MIT License\n\nCopyright (c) Example Author\n\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated " +
        "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge.\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-pl-theme-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEnv _env = new();
    private string Data => Path.Combine(_root, "Roaming", "Playnite");
    private string Themes => Path.Combine(Data, "Themes", "Fullscreen");
    private string Config => Path.Combine(Data, "fullscreenConfig.json");
    private readonly PlayniteThemeStore _store;

    public PlayniteThemeServiceTests()
    {
        Directory.CreateDirectory(Themes);
        _env.AppData = Path.Combine(_root, "Roaming");
        _env.LocalAppData = Path.Combine(_root, "Local");
        _store = new PlayniteThemeStore(Path.Combine(_root, "state", "theme.json"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class Handler : HttpMessageHandler
    {
        public string Release = "";
        public byte[] Payload = Array.Empty<byte>();
        public string? License = MitText;
        public int Downloads;
        public bool LicenseFetched;
        public string DownloadHost = "github.com";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri uri = request.RequestUri!;
            if (uri.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release), RequestMessage = request });
            if (uri.Host == "raw.githubusercontent.com")
            {
                LicenseFetched = true;
                return Task.FromResult(License is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(License), RequestMessage = request });
            }
            Downloads++;
            var redirected = new HttpRequestMessage(HttpMethod.Get, "https://" + DownloadHost + uri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload), RequestMessage = redirected });
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] MakeTheme(Action<ZipArchive>? extra = null, bool withLicense = false, string? yaml = null, string version = "3.6.0")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(text); }
            Add("theme.yaml", yaml ?? "ThemeApiVersion: 2.10.0\nMode: Fullscreen\nId: " + Folder + "\nName: Aniki ReMake\nAuthor: Mike Aniki\nVersion: " + version);
            Add("Readme.txt", "readme");
            Add("Views/Main.xaml", "<Grid/>");
            if (withLicense) Add("LICENSE", MitText);
            extra?.Invoke(zip);
        }
        return ms.ToArray();
    }

    private (PlayniteThemeService S, Handler H) Make(byte[] payload, string? digest = "same", Func<string, long?>? free = null)
    {
        string d = digest == "same" ? Sha(payload) : digest ?? "";
        var h = new Handler
        {
            Payload = payload,
            Release = "{\"tag_name\":\"v3.6.0\",\"prerelease\":false,\"draft\":false,\"assets\":[{\"name\":\"" + Asset + "\",\"size\":" + payload.Length +
                ",\"digest\":" + (digest is null ? "null" : "\"sha256:" + d + "\"") + ",\"browser_download_url\":\"" + Url + "\"}]}"
        };
        var svc = new PlayniteThemeService(new HttpClient(h), _env, _store, () => null, Path.Combine(_root, "work"), Path.Combine(_root, "cfgbackup"), free ?? (_ => long.MaxValue));
        return (svc, h);
    }

    private static PlayniteThemeInstallRequest Req(byte[] payload, bool replace = false, string? sha = "same", bool unverified = false) => new()
    { Consent = true, Version = "v3.6.0", Sha256 = sha == "same" ? Sha(payload) : sha, ReplaceExisting = replace, AcceptUnverified = unverified };

    private static async Task<PlayniteThemeInstallStatusDto> Install(PlayniteThemeService s, PlayniteThemeInstallRequest r)
    {
        await s.InstallAsync(r, CancellationToken.None);
        return s.InstallStatus;
    }

    private string[] Leftovers() => Directory.Exists(Themes)
        ? Directory.GetDirectories(Themes).Select(Path.GetFileName).Where(n => n!.StartsWith(".parux-staging-")).ToArray()!
        : Array.Empty<string>();

    private void WriteConfig(string theme, bool bom = true)
    {
        string text = "{\r\n  \"Monitor\": 2,\r\n  \"Theme\": \"" + theme + "\",\r\n  \"Rows\": 2\r\n}";
        byte[] bytes = (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        File.WriteAllBytes(Config, bytes);
    }

    // ---- install ----

    [Fact]
    public async Task Install_stages_swaps_atomically_keeps_the_license_and_becomes_parux_managed()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg);
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg));
        Assert.Equal("completed", st.Stage);
        string target = Path.Combine(Themes, Folder);
        Assert.True(File.Exists(Path.Combine(target, "theme.yaml")));
        Assert.True(File.Exists(Path.Combine(target, "Views", "Main.xaml")));
        Assert.True(h.LicenseFetched);
        string license = File.ReadAllText(Path.Combine(target, "LICENSE-Aniki-ReMake-MIT.txt"));
        Assert.Contains("MIT License", license);
        Assert.Contains("https://github.com/Mike-Aniki/Aniki-ReMake", license);
        Assert.Empty(Leftovers());
        Assert.False(File.Exists(Path.Combine(_root, "work", Asset)));

        PlayniteThemeComponentDto c = svc.Detect();
        Assert.True(c.Detected);
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, c.Ownership);
        Assert.Equal("3.6.0", c.Version);
        Assert.Equal("LICENSE-Aniki-ReMake-MIT.txt", c.LicenseFile);
        Assert.Contains("select", c.Actions);
    }

    [Fact]
    public async Task Install_keeps_the_archive_license_and_does_not_fetch_one()
    {
        byte[] pkg = MakeTheme(withLicense: true);
        var (svc, h) = Make(pkg);
        Assert.Equal("completed", (await Install(svc, Req(pkg))).Stage);
        Assert.False(h.LicenseFetched);
        Assert.Equal("LICENSE", svc.Detect().LicenseFile);
    }

    [Fact]
    public async Task Hash_mismatch_installs_nothing_and_deletes_the_download()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg, digest: new string('0', 64));
        // The release publishes a hash that does not match the bytes served; the owner confirmed that published hash.
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg, sha: new string('0', 64)));
        Assert.Equal("failed", st.Stage);
        Assert.Contains("SHA-256", st.Error);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
        Assert.Empty(Leftovers());
        Assert.False(File.Exists(Path.Combine(_root, "work", Asset)));
        Assert.False(h.LicenseFetched);
    }

    [Fact]
    public async Task A_checksum_different_from_the_one_the_owner_confirmed_stops_before_any_download()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg);
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg, sha: new string('1', 64)));
        Assert.Equal("failed", st.Stage);
        Assert.Equal(0, h.Downloads);
    }

    [Fact]
    public async Task A_release_without_checksum_needs_explicit_acceptance()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg, digest: null);
        Assert.Equal("failed", (await Install(svc, Req(pkg, sha: null))).Stage);
        Assert.Equal(0, h.Downloads);
        Assert.Equal("completed", (await Install(svc, Req(pkg, sha: null, unverified: true))).Stage);
    }

    [Fact]
    public async Task A_truncated_zip_with_a_matching_hash_is_refused_and_leaves_nothing()
    {
        byte[] full = MakeTheme(z => { using var w = new StreamWriter(z.CreateEntry("Big.bin").Open()); w.Write(new string('x', 200_000)); });
        byte[] partial = full.Take(full.Length / 2).ToArray();
        var (svc, _) = Make(partial);
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(partial));
        Assert.Equal("failed", st.Stage);
        Assert.Contains("damaged", st.Error);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_download_that_ends_early_is_refused()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg);
        h.Payload = pkg.Take(pkg.Length - 10).ToArray();   // the release announced the full size
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg));
        Assert.Equal("failed", st.Stage);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Views/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("/rooted.txt")]
    public async Task Traversal_or_absolute_entries_are_refused_and_nothing_is_written_outside(string entry)
    {
        byte[] pkg = MakeTheme(z => { using var w = new StreamWriter(z.CreateEntry(entry).Open()); w.Write("evil"); });
        var (svc, _) = Make(pkg);
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg));
        Assert.Equal("failed", st.Stage);
        Assert.Contains("unsafe", st.Error);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
        Assert.False(File.Exists(Path.Combine(Themes, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Data, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Duplicate_entries_and_a_wrong_or_non_fullscreen_package_are_refused()
    {
        byte[] dup = MakeTheme(z => { using var w = new StreamWriter(z.CreateEntry("readme.txt").Open()); w.Write("dup"); });
        var (svc, _) = Make(dup);
        Assert.Equal("failed", (await Install(svc, Req(dup))).Stage);

        byte[] desktop = MakeTheme(yaml: "Mode: Desktop\nId: " + Folder + "\nName: Aniki ReMake\nVersion: 3");
        (svc, _) = Make(desktop);
        Assert.Contains("Fullscreen", (await Install(svc, Req(desktop))).Error);

        byte[] other = MakeTheme(yaml: "Mode: Fullscreen\nId: SomethingElse\nName: Other\nVersion: 3");
        (svc, _) = Make(other);
        Assert.Contains("not the Aniki ReMake", (await Install(svc, Req(other))).Error);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
    }

    [Fact]
    public async Task A_redirect_to_an_untrusted_host_is_refused()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg);
        h.DownloadHost = "evil.example";
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg));
        Assert.Equal("failed", st.Stage);
        Assert.Contains("untrusted", st.Error);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
    }

    [Fact]
    public async Task A_license_that_cannot_be_obtained_or_is_not_mit_installs_nothing()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg);
        h.License = null;
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg));
        Assert.Equal("failed", st.Stage);
        Assert.Contains("license", st.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
        Assert.Empty(Leftovers());

        h.License = new string('A', 400);
        Assert.Equal("failed", (await Install(svc, Req(pkg))).Stage);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));
    }

    [Fact]
    public async Task Playnite_running_blocks_the_install_before_anything_is_downloaded()
    {
        byte[] pkg = MakeTheme();
        var (svc, h) = Make(pkg);
        _env.Running.Add(new PlayniteRunningProcess(5, @"C:\P\Playnite.FullscreenApp.exe"));
        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg));
        Assert.Equal("failed", st.Stage);
        Assert.Contains("running", st.Error);
        Assert.Equal(0, h.Downloads);
        PlayniteThemePlanDto plan = await svc.ResolveAsync(CancellationToken.None);
        Assert.True(plan.PlayniteRunning);
    }

    [Fact]
    public async Task Missing_playnite_data_folder_and_low_disk_space_are_refused()
    {
        byte[] pkg = MakeTheme();
        var (low, _) = Make(pkg, free: _ => 1024);
        Assert.Contains("disk space", (await Install(low, Req(pkg))).Error);

        Directory.Delete(Data, true);
        var (svc, _) = Make(pkg);
        PlayniteThemePlanDto plan = await svc.ResolveAsync(CancellationToken.None);
        Assert.False(plan.Available);
        Assert.False(Directory.Exists(Data));   // never created by PaRuX
    }

    [Fact]
    public void Begin_needs_consent_a_version_and_one_install_at_a_time()
    {
        var (svc, _) = Make(MakeTheme());
        Assert.NotNull(svc.Begin(new PlayniteThemeInstallRequest { Consent = false, Version = "v3.6.0" }));
        Assert.NotNull(svc.Begin(new PlayniteThemeInstallRequest { Consent = true, Version = "" }));
    }

    // ---- existing folders, backup and rollback ----

    private void MakeUserTheme(string marker = "user")
    {
        string dir = Path.Combine(Themes, Folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "theme.yaml"), "Mode: Fullscreen\nId: " + Folder + "\nName: Aniki ReMake\nVersion: 3.1.1");
        File.WriteAllText(Path.Combine(dir, "mine.txt"), marker);
    }

    [Fact]
    public async Task An_existing_theme_folder_is_user_managed_and_is_never_overwritten_without_explicit_consent()
    {
        MakeUserTheme();
        byte[] pkg = MakeTheme();
        var (svc, _) = Make(pkg);
        PlayniteThemeComponentDto before = svc.Detect();
        Assert.True(before.Detected);
        Assert.Equal(ComponentOwnership.USER_MANAGED, before.Ownership);
        Assert.Equal("3.1.1", before.Version);

        PlayniteThemePlanDto plan = await svc.ResolveAsync(CancellationToken.None);
        Assert.True(plan.ExistingFolder);
        Assert.Equal("3.1.1", plan.ExistingVersion);

        PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg, replace: false));
        Assert.Equal("failed", st.Stage);
        Assert.Contains("does not overwrite", st.Error);
        Assert.Equal("user", File.ReadAllText(Path.Combine(Themes, Folder, "mine.txt")));
        Assert.Equal(ComponentOwnership.USER_MANAGED, svc.Detect().Ownership);
    }

    [Fact]
    public async Task Replacing_with_consent_keeps_a_backup_and_rollback_puts_the_original_back()
    {
        MakeUserTheme();
        byte[] pkg = MakeTheme();
        var (svc, _) = Make(pkg);
        Assert.Equal("completed", (await Install(svc, Req(pkg, replace: true))).Stage);
        Assert.False(File.Exists(Path.Combine(Themes, Folder, "mine.txt")));
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, svc.Detect().Ownership);
        string backupRoot = Assert.Single(Directory.GetDirectories(Themes, ".parux-backup-*"));
        Assert.Equal("user", File.ReadAllText(Path.Combine(backupRoot, Folder, "mine.txt")));

        Assert.False(svc.Rollback(consent: false).Ok);
        PlayniteThemeActionResultDto rolled = svc.Rollback(consent: true);
        Assert.True(rolled.Ok);
        Assert.Equal("rolled-back", rolled.Outcome);
        Assert.Equal("user", File.ReadAllText(Path.Combine(Themes, Folder, "mine.txt")));
        Assert.Empty(Directory.GetDirectories(Themes, ".parux-backup-*"));
        Assert.Equal(ComponentOwnership.USER_MANAGED, svc.Detect().Ownership);
    }

    [Fact]
    public async Task Rollback_refuses_when_the_original_backup_is_missing_instead_of_deleting_the_replacement()
    {
        MakeUserTheme("irreplaceable-user-data");
        byte[] package = MakeTheme();
        var (svc, _) = Make(package);
        Assert.Equal("completed", (await Install(svc, Req(package, replace: true))).Stage);
        string backup = Assert.Single(Directory.GetDirectories(Themes, ".parux-backup-*"));
        string rescued = Path.Combine(_root, "backup-rescued-outside-themes");
        Directory.Move(backup, rescued);

        PlayniteThemeActionResultDto refused = svc.Rollback(consent: true);
        Assert.False(refused.Ok);
        Assert.Contains("backup", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(Themes, Folder)));
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, svc.Detect().Ownership);
        Assert.Equal("irreplaceable-user-data", File.ReadAllText(Path.Combine(rescued, Folder, "mine.txt")));

        // When the backup is returned, the ordinary reversible path still works.
        Directory.Move(rescued, backup);
        Assert.Equal("rolled-back", svc.Rollback(consent: true).Outcome);
        Assert.Equal("irreplaceable-user-data", File.ReadAllText(Path.Combine(Themes, Folder, "mine.txt")));
    }

    [Fact]
    public async Task Rollback_never_removes_a_theme_when_backup_journal_points_outside_its_profile()
    {
        MakeUserTheme("owner-file");
        byte[] package = MakeTheme();
        var (svc, _) = Make(package);
        Assert.Equal("completed", (await Install(svc, Req(package, replace: true))).Stage);
        string backup = Assert.Single(Directory.GetDirectories(Themes, ".parux-backup-*"));
        Assert.True(_store.Update(s => s.BackupFolder = Path.Combine(_root, "untrusted-backup")));

        PlayniteThemeActionResultDto refused = svc.Rollback(consent: true);
        Assert.False(refused.Ok);
        Assert.True(Directory.Exists(Path.Combine(Themes, Folder)));
        Assert.Equal("owner-file", File.ReadAllText(Path.Combine(backup, Folder, "mine.txt")));
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, svc.Detect().Ownership);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_failed_ownership_journal_restores_the_files_and_preserves_existing_backups(bool replaceExisting)
    {
        if (replaceExisting) MakeUserTheme("original-user-content");
        byte[] package = MakeTheme();
        var (svc, _) = Make(package);
        Assert.Equal("completed", (await Install(svc, Req(package, replace: replaceExisting))).Stage);
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, svc.Detect().Ownership);

        // Deliberately make the persisted journal path a DIRECTORY so File.Move
        // cannot replace it. This synthetic fault does not change global ACLs.
        string journal = Path.Combine(_root, "state", "theme.json");
        File.Delete(journal);
        Directory.CreateDirectory(journal);
        PlayniteThemeActionResultDto result = svc.Rollback(consent: true);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(Path.Combine(Themes, Folder)));
        Assert.True(File.Exists(Path.Combine(Themes, Folder, "theme.yaml")));
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, svc.Detect().Ownership);
        // Even on a failed atomic File.Move, PaRuX does not retain stale journal
        // temporaries which may contain user-specific local installation paths.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "state"), "theme.json.*.tmp"));

        if (replaceExisting)
        {
            string backup = Assert.Single(Directory.GetDirectories(Themes, ".parux-backup-*"));
            Assert.Equal("original-user-content", File.ReadAllText(Path.Combine(backup, Folder, "mine.txt")));
            Assert.False(File.Exists(Path.Combine(Themes, Folder, "mine.txt")));
        }
        else
        {
            Assert.Empty(Directory.GetDirectories(Themes, ".parux-backup-*"));
        }
    }

    [Fact]
    public async Task A_busy_existing_folder_makes_the_swap_fail_and_the_original_stays_intact()
    {
        MakeUserTheme();
        byte[] pkg = MakeTheme();
        var (svc, _) = Make(pkg);
        using (new FileStream(Path.Combine(Themes, Folder, "mine.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            PlayniteThemeInstallStatusDto st = await Install(svc, Req(pkg, replace: true));
            Assert.Equal("failed", st.Stage);
        }
        Assert.Equal("user", File.ReadAllText(Path.Combine(Themes, Folder, "mine.txt")));
        Assert.True(File.Exists(Path.Combine(Themes, Folder, "theme.yaml")));
        Assert.Empty(Leftovers());
        Assert.Equal(ComponentOwnership.USER_MANAGED, svc.Detect().Ownership);
    }

    [Fact]
    public async Task Rollback_of_a_fresh_install_removes_only_what_parux_installed_and_never_a_user_theme()
    {
        byte[] pkg = MakeTheme();
        var (svc, _) = Make(pkg);
        Assert.Equal("completed", (await Install(svc, Req(pkg))).Stage);
        PlayniteThemeActionResultDto removed = svc.Rollback(true);
        Assert.Equal("removed", removed.Outcome);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));

        MakeUserTheme();
        PlayniteThemeActionResultDto refused = svc.Rollback(true);
        Assert.False(refused.Ok);
        Assert.True(Directory.Exists(Path.Combine(Themes, Folder)));
    }

    [Fact]
    public async Task Rollback_is_refused_while_playnite_runs_or_the_theme_is_selected()
    {
        byte[] pkg = MakeTheme();
        var (svc, _) = Make(pkg);
        await Install(svc, Req(pkg));
        _env.Running.Add(new PlayniteRunningProcess(5, @"C:\P\Playnite.DesktopApp.exe"));
        Assert.False(svc.Rollback(true).Ok);
        _env.Running.Clear();
        WriteConfig(Folder);
        Assert.False(svc.Rollback(true).Ok);
        Assert.True(Directory.Exists(Path.Combine(Themes, Folder)));
    }

    // ---- selection ----

    private async Task<PlayniteThemeService> Installed()
    {
        byte[] pkg = MakeTheme();
        var (svc, _) = Make(pkg);
        await Install(svc, Req(pkg));
        return svc;
    }

    [Fact]
    public async Task Select_needs_consent_changes_only_the_theme_value_backs_up_and_can_be_restored()
    {
        PlayniteThemeService svc = await Installed();
        WriteConfig("Solaris_b6e50d04");
        byte[] before = File.ReadAllBytes(Config);

        Assert.False(svc.Select(consent: false).Ok);
        Assert.Equal(before, File.ReadAllBytes(Config));

        PlayniteThemeActionResultDto sel = svc.Select(consent: true);
        Assert.True(sel.Ok);
        Assert.Equal("selected", sel.Outcome);
        Assert.Equal(before, File.ReadAllBytes(sel.BackupFile!));
        string after = Encoding.UTF8.GetString(File.ReadAllBytes(Config));
        Assert.Equal(Encoding.UTF8.GetString(before).Replace("Solaris_b6e50d04", Folder), after);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(Config).Take(3).ToArray());

        PlayniteThemeComponentDto c = svc.Detect();
        Assert.True(c.Selected);
        Assert.True(c.CanRestoreSelection);
        Assert.Equal("Solaris_b6e50d04", c.PreviousTheme);
        Assert.Equal("already-selected", svc.Select(true).Outcome);

        PlayniteThemeActionResultDto back = svc.RestoreSelection(true);
        Assert.True(back.Ok);
        Assert.Equal(before, File.ReadAllBytes(Config));
        Assert.False(svc.Detect().CanRestoreSelection);
    }

    [Fact]
    public async Task Select_never_edits_the_config_while_playnite_runs_and_tells_the_user_what_to_do()
    {
        PlayniteThemeService svc = await Installed();
        WriteConfig("Other");
        byte[] before = File.ReadAllBytes(Config);
        _env.Running.Add(new PlayniteRunningProcess(7, @"C:\P\Playnite.FullscreenApp.exe"));
        PlayniteThemeActionResultDto r = svc.Select(true);
        Assert.False(r.Ok);
        Assert.Contains("Playnite is running", r.Message);
        Assert.Contains("Settings", r.Message);
        Assert.Equal(before, File.ReadAllBytes(Config));
        Assert.False(svc.RestoreSelection(true).Ok);
    }

    [Fact]
    public async Task Select_refuses_an_unreadable_config_or_a_theme_that_is_not_installed_and_creates_no_file()
    {
        PlayniteThemeService svc = await Installed();
        Assert.False(svc.Select(true).Ok);   // no fullscreenConfig.json
        Assert.False(File.Exists(Config));
        File.WriteAllText(Config, "{ not json");
        Assert.False(svc.Select(true).Ok);
        Assert.Equal("{ not json", File.ReadAllText(Config));
        File.WriteAllText(Config, "{\"Rows\":2}");
        Assert.False(svc.Select(true).Ok);

        Directory.Delete(Path.Combine(Themes, Folder), true);
        WriteConfig("x");
        Assert.False(svc.Select(true).Ok);
    }

    [Fact]
    public async Task Restore_leaves_a_theme_alone_that_the_user_changed_after_parux_selected_it()
    {
        PlayniteThemeService svc = await Installed();
        WriteConfig("Old");
        Assert.True(svc.Select(true).Ok);
        WriteConfig("UserPickedThis");
        byte[] before = File.ReadAllBytes(Config);
        Assert.False(svc.RestoreSelection(true).Ok);
        Assert.Equal(before, File.ReadAllBytes(Config));
    }

    // ---- detect / locate / skip ----

    [Fact]
    public void Nothing_installed_offers_install_locate_and_skip_and_only_other_aniki_variants_are_ignored()
    {
        Directory.CreateDirectory(Path.Combine(Themes, "Aniki_Lite"));
        File.WriteAllText(Path.Combine(Themes, "Aniki_Lite", "theme.yaml"), "Mode: Fullscreen\nId: Aniki_Lite\nName: Aniki Lite\nVersion: 1");
        var (svc, _) = Make(MakeTheme());
        PlayniteThemeComponentDto c = svc.Detect();
        Assert.False(c.Detected);
        Assert.Equal(new[] { "install", "locate", "skip" }, c.Actions);
        Assert.True(svc.SetSkipped(true));
        Assert.True(svc.Detect().Skipped);
    }

    [Fact]
    public void Locate_accepts_a_theme_folder_or_a_playnite_data_folder_and_never_copies_or_changes_anything()
    {
        string portable = Path.Combine(_root, "Portable", "Playnite");
        string themeDir = Path.Combine(portable, "Themes", "Fullscreen", Folder);
        Directory.CreateDirectory(themeDir);
        File.WriteAllText(Path.Combine(themeDir, "theme.yaml"), "Mode: Fullscreen\nId: " + Folder + "\nName: Aniki ReMake\nVersion: 3.5");
        var (svc, _) = Make(MakeTheme());

        Assert.NotNull(svc.Locate(themeDir).Component);
        PlayniteThemeComponentDto c = svc.Detect();
        Assert.True(c.Detected);
        Assert.Equal(portable, c.PlayniteDataFolder);
        Assert.Equal(ComponentOwnership.USER_MANAGED, c.Ownership);
        Assert.Equal("3.5", c.Version);
        Assert.False(Directory.Exists(Path.Combine(Themes, Folder)));

        Assert.NotNull(svc.Locate(portable).Component);
        Assert.Null(svc.Locate(Path.Combine(_root, "nowhere")).Component);
        Assert.Null(svc.Locate("relative\\path").Component);
        Assert.Null(svc.Locate("").Component);
    }

    [Fact]
    public void Aniki_helper_probe_reads_only_a_bounded_extension_manifest_with_the_official_guid()
    {
        string extensions = Path.Combine(Data, "Extensions");
        string extension = Path.Combine(extensions, "AnikiHelper_96a983a3-3f13-4dce-a474-4052b718bb52");
        Directory.CreateDirectory(extension);
        Assert.False(PlayniteThemeService.FindAnikiHelperOnDisk(Data).Found);

        string manifest = Path.Combine(extension, "extension.yaml");
        File.WriteAllText(manifest, "Name: Aniki Helper\nId: 00000000-0000-0000-0000-000000000001\nVersion: 0.0.1");
        Assert.False(PlayniteThemeService.FindAnikiHelperOnDisk(Data).Found);

        File.WriteAllText(manifest, "Name: Aniki Helper\nId: 96a983a3-3f13-4dce-a474-4052b718bb52\nVersion: 1.3.6");
        var found = PlayniteThemeService.FindAnikiHelperOnDisk(Data);
        Assert.True(found.Found);
        Assert.Equal("1.3.6", found.Version);
        Assert.True(Make(MakeTheme()).S.Detect().AnikiHelperFoundOnDisk);
    }

    [Fact]
    public async Task A_second_Playnite_profile_with_the_same_Aniki_id_is_never_mistaken_for_a_managed_theme()
    {
        byte[] pkg = MakeTheme();
        var (svc, _) = Make(pkg);
        Assert.Equal("completed", (await Install(svc, Req(pkg))).Stage);

        string otherData = Path.Combine(_root, "OtherPortable", "Playnite");
        string userFolder = Path.Combine(otherData, "Themes", "Fullscreen", Folder);
        Directory.CreateDirectory(userFolder);
        File.WriteAllText(Path.Combine(userFolder, "theme.yaml"),
            "ThemeApiVersion: 2.10.0\nMode: Fullscreen\nId: " + Folder + "\nName: Aniki ReMake\nVersion: 3.6.0");
        File.WriteAllText(Path.Combine(userFolder, "important-user-file.txt"), "do not delete");

        Assert.NotNull(svc.Locate(otherData).Component);
        var result = svc.Detect();
        Assert.Equal(ComponentOwnership.USER_MANAGED, result.Ownership);
        Assert.False(result.CanRollback);
        Assert.False(svc.Rollback(consent: true).Ok);
        Assert.Equal("do not delete", File.ReadAllText(Path.Combine(userFolder, "important-user-file.txt")));
    }

    [Fact]
    public async Task A_second_Playnite_profile_cannot_inherit_Aniki_selection_restore_rights()
    {
        PlayniteThemeService svc = await Installed();
        WriteConfig("OldOwnerTheme");
        Assert.True(svc.Select(consent: true).Ok);
        Assert.True(svc.Detect().CanRestoreSelection);

        string otherData = Path.Combine(_root, "AnotherPlaynite");
        string userFolder = Path.Combine(otherData, "Themes", "Fullscreen", Folder);
        Directory.CreateDirectory(userFolder);
        File.WriteAllText(Path.Combine(userFolder, "theme.yaml"),
            "Mode: Fullscreen\nId: " + Folder + "\nName: Aniki ReMake\nVersion: 3.6.0");
        string otherConfig = Path.Combine(otherData, "fullscreenConfig.json");
        File.WriteAllText(otherConfig, "{\"Theme\":\"" + Folder + "\"}");
        byte[] previousUserBytes = File.ReadAllBytes(otherConfig);

        Assert.NotNull(svc.Locate(otherData).Component);
        Assert.False(svc.Detect().CanRestoreSelection);
        Assert.False(svc.RestoreSelection(consent: true).Ok);
        Assert.Equal(previousUserBytes, File.ReadAllBytes(otherConfig));
    }
}
