using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Support apps: policy, store, runner (fake process host) and downloader (fake HTTP). Synthetic files only; nothing is started or downloaded.</summary>
public sealed class SupportAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-support-" + Guid.NewGuid().ToString("N"));
    public SupportAppTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Exe(string name = "helper.exe")
    {
        string p = Path.Combine(_root, "user", name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        if (!File.Exists(p)) File.WriteAllText(p, "MZ");
        return p;
    }

    private SupportAppStore NewStore() => new(Path.Combine(_root, "server", "support-apps.json"));
    private static SupportAppEntry Entry(string id, string path, Func<SupportAppEntry, SupportAppEntry>? edit = null)
    {
        var e = new SupportAppEntry(id, "App " + id, path);
        return edit is null ? e : edit(e);
    }

    // ---- fake host ----

    private sealed class FakeHost : ISupportAppHost
    {
        public Dictionary<string, List<SupportAppProcess>> Running = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Started = new();
        public List<int> Stopped = new();
        public DateTime? ExeWritten;
        public bool StartFails, AppearsAfterStart = true, StopSucceeds = true;
        private int _pid = 100;
        public IReadOnlyList<SupportAppProcess> Find(string name) => Running.TryGetValue(name, out var l) ? l.ToList() : [];
        public SupportAppProcess? Start(string path, string? args, bool minimized)
        {
            if (StartFails) return null;
            Started.Add(path);
            var p = new SupportAppProcess(++_pid, path, DateTime.UtcNow);
            if (AppearsAfterStart) Running[Path.GetFileNameWithoutExtension(path)] = new() { p };
            return p;
        }
        public bool Stop(SupportAppProcess p) { if (!StopSucceeds) return false; Stopped.Add(p.Pid); foreach (var l in Running.Values) l.RemoveAll(x => x.Pid == p.Pid); return true; }
        public DateTime? ExeWriteUtc(string path) => ExeWritten ?? File.GetLastWriteTimeUtc(path);
    }

    private SupportAppRunner Runner(SupportAppStore store, FakeHost host, Func<bool>? gaming = null, List<string>? log = null) =>
        new(store, host, gaming ?? (() => false), (t, ct) => Task.CompletedTask, () => 0, log is null ? null : log.Add);

    // ---- policy ----

    [Fact]
    public void Validation_accepts_a_local_exe_and_refuses_unsafe_entries()
    {
        Assert.Null(SupportAppPolicy.Validate(Entry("a1", Exe())));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", Exe(), e => e with { Args = "x & calc" })));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", Exe(), e => e with { Name = "bad\u0001" })));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("A B", Exe())));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", @"\\server\share\x.exe")));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", "relative.exe")));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", Path.Combine(_root, "missing.exe"))));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", Path.Combine(_root, "user", "..", "user", "helper.exe"))));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", Exe(), e => e with { TimeoutSeconds = 999 })));
        Assert.NotNull(SupportAppPolicy.Validate(Entry("a1", Exe(), e => e with { CatalogId = "unknown-thing" })));
    }

    [Fact]
    public void Ownership_is_managed_only_for_a_catalog_copy_inside_the_managed_folder()
    {
        string managed = Path.Combine(_root, "managed");
        var inside = Entry("nyrna", Path.Combine(managed, "nyrna", "1", "nyrna.exe"), e => e with { CatalogId = "nyrna" });
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, SupportAppPolicy.OwnershipOf(inside, managed));
        Assert.Equal(ComponentOwnership.USER_MANAGED, SupportAppPolicy.OwnershipOf(inside with { CatalogId = null }, managed));
        Assert.Equal(ComponentOwnership.USER_MANAGED, SupportAppPolicy.OwnershipOf(Entry("x", Exe(), e => e with { CatalogId = "nyrna" }), managed));
    }

    [Fact]
    public void Start_decision_matrix()
    {
        var e = Entry("a1", Exe());
        var none = Array.Empty<SupportAppProcess>();
        Assert.Equal(SupportAppStartDecision.SkippedGamingActive, SupportAppPolicy.DecideStart(e, true, null, false, none, null));
        Assert.Equal(SupportAppStartDecision.SkippedDisabled, SupportAppPolicy.DecideStart(e with { Enabled = false }, false, null, false, none, null));
        Assert.Equal(SupportAppStartDecision.SkippedMissing, SupportAppPolicy.DecideStart(e, false, "gone", true, none, null));
        Assert.Equal(SupportAppStartDecision.SkippedInvalid, SupportAppPolicy.DecideStart(e, false, "bad", false, none, null));
        Assert.Equal(SupportAppStartDecision.Start, SupportAppPolicy.DecideStart(e, false, null, false, none, null));
        var old = new SupportAppProcess(1, e.Path, DateTime.UtcNow.AddHours(-2));
        Assert.Equal(SupportAppStartDecision.AlreadyRunning, SupportAppPolicy.DecideStart(e, false, null, false, [old], DateTime.UtcNow));
        Assert.Equal(SupportAppStartDecision.ReplaceStale, SupportAppPolicy.DecideStart(e with { ReplaceIfStale = true }, false, null, false, [old], DateTime.UtcNow));
        var other = new SupportAppProcess(2, @"C:\elsewhere\helper.exe", DateTime.UtcNow.AddHours(-2));
        Assert.Equal(SupportAppStartDecision.AlreadyRunning, SupportAppPolicy.DecideStart(e with { ReplaceIfStale = true }, false, null, false, [other], DateTime.UtcNow));
    }

    // ---- store ----

    [Fact]
    public void Store_is_off_and_empty_by_default_and_round_trips()
    {
        SupportAppStore store = NewStore();
        Assert.False(store.Load().Enabled);
        var (state, error) = store.Save(new SupportAppSaveRequest(true, [Entry("a1", Exe())]));
        Assert.Null(error);
        Assert.True(NewStore().Load().Enabled);
        Assert.Equal("a1", Assert.Single(NewStore().Load().Apps).Id);
        Assert.NotNull(store.Save(new SupportAppSaveRequest(true, [Entry("a1", Exe()), Entry("a1", Exe("b.exe"))])).Error);   // duplicate id
        Assert.NotNull(store.Save(new SupportAppSaveRequest(true, Enumerable.Range(0, 17).Select(i => Entry("a" + i, Exe())).ToList())).Error);
    }

    [Fact]
    public void A_damaged_file_reads_as_off_and_is_not_overwritten_by_a_read()
    {
        SupportAppStore store = NewStore();
        Directory.CreateDirectory(Path.Combine(_root, "server"));
        File.WriteAllText(Path.Combine(_root, "server", "support-apps.json"), "{ not json");
        SupportAppListState s = store.Load();
        Assert.True(s.Damaged); Assert.False(s.Enabled);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(_root, "server", "support-apps.json")));
    }

    [Fact]
    public void An_entry_whose_exe_vanished_is_kept_so_it_can_be_fixed_or_removed()
    {
        SupportAppStore store = NewStore();
        string exe = Exe("gone.exe");
        store.Save(new SupportAppSaveRequest(true, [Entry("a1", exe)]));
        File.Delete(exe);
        SupportAppListState s = NewStore().Load();
        Assert.Single(s.Apps);
        Assert.NotNull(SupportAppPolicy.Validate(s.Apps[0]));
        Assert.Null(store.Save(new SupportAppSaveRequest(true, s.Apps)).Error);
    }

    // ---- runner ----

    [Fact]
    public async Task Off_list_starts_nothing()
    {
        var host = new FakeHost();
        SupportAppStore store = NewStore();
        store.Save(new SupportAppSaveRequest(false, [Entry("a1", Exe())]));
        Assert.Empty(await Runner(store, host).StartAsync(default));
        Assert.Empty(host.Started);
    }

    [Fact]
    public async Task Starts_enabled_entries_measures_each_and_logs_without_paths()
    {
        var host = new FakeHost();
        var log = new List<string>();
        SupportAppStore store = NewStore();
        store.Save(new SupportAppSaveRequest(true, [Entry("a1", Exe()), Entry("a2", Exe("two.exe"), e => e with { StartWithConsole = false })]));
        var results = await Runner(store, host, log: log).StartAsync(default);
        SupportAppResult r = Assert.Single(results);
        Assert.Equal(SupportAppOutcome.Started, r.Outcome);
        Assert.Single(host.Started);
        Assert.All(log, l => Assert.DoesNotContain(_root, l));
    }

    [Fact]
    public async Task Gaming_active_refuses_every_start()
    {
        var host = new FakeHost();
        SupportAppStore store = NewStore();
        store.Save(new SupportAppSaveRequest(true, [Entry("a1", Exe())]));
        var results = await Runner(store, host, () => true).StartAsync(default);
        Assert.Equal(SupportAppOutcome.SkippedGamingActive, Assert.Single(results).Outcome);
        Assert.Empty(host.Started);
    }

    [Fact]
    public async Task A_program_that_is_already_running_is_left_alone_and_a_failed_start_is_reported()
    {
        var host = new FakeHost();
        SupportAppStore store = NewStore();
        string exe = Exe();
        store.Save(new SupportAppSaveRequest(true, [Entry("a1", exe)]));
        host.Running["helper"] = new() { new SupportAppProcess(5, exe, DateTime.UtcNow) };
        Assert.Equal(SupportAppOutcome.AlreadyRunning, Assert.Single(await Runner(store, host).StartAsync(default)).Outcome);
        Assert.Empty(host.Started);

        var failing = new FakeHost { StartFails = true };
        Assert.Equal(SupportAppOutcome.Failed, Assert.Single(await Runner(store, failing).StartAsync(default)).Outcome);
    }

    [Fact]
    public async Task A_program_that_never_appears_times_out_after_the_bounded_wait()
    {
        var host = new FakeHost { AppearsAfterStart = false };
        int delays = 0;
        SupportAppStore store = NewStore();
        store.Save(new SupportAppSaveRequest(true, [Entry("a1", Exe(), e => e with { TimeoutSeconds = 3 })]));
        var runner = new SupportAppRunner(store, host, () => false, (t, ct) => { delays++; return Task.CompletedTask; }, () => 0);
        Assert.Equal(SupportAppOutcome.TimedOut, Assert.Single(await runner.StartAsync(default)).Outcome);
        Assert.Equal(3, delays);
    }

    [Fact]
    public async Task A_stale_copy_is_replaced_only_when_the_entry_asked_for_it()
    {
        var host = new FakeHost { ExeWritten = DateTime.UtcNow };
        SupportAppStore store = NewStore();
        string exe = Exe();
        store.Save(new SupportAppSaveRequest(true, [Entry("a1", exe, e => e with { ReplaceIfStale = true })]));
        host.Running["helper"] = new() { new SupportAppProcess(7, exe, DateTime.UtcNow.AddHours(-3)) };
        var r = Assert.Single(await Runner(store, host).StartAsync(default));
        Assert.Equal(SupportAppOutcome.ReplacedStale, r.Outcome);
        Assert.Contains(7, host.Stopped);
        Assert.Single(host.Started);
    }

    [Fact]
    public async Task Stop_only_ends_what_this_runner_started_and_is_marked_to_stop()
    {
        var host = new FakeHost();
        SupportAppStore store = NewStore();
        string stopMe = Exe("stopme.exe"), keep = Exe("keep.exe"), foreign = Exe("foreign.exe");
        store.Save(new SupportAppSaveRequest(true, [
            Entry("stopme", stopMe, e => e with { StopWithConsole = true }),
            Entry("keep", keep),
            Entry("foreign", foreign, e => e with { StopWithConsole = true, StartWithConsole = false }),
        ]));
        host.Running["foreign"] = new() { new SupportAppProcess(900, foreign, DateTime.UtcNow) };   // the owner's own running copy
        SupportAppRunner runner = Runner(store, host);
        await runner.StartAsync(default);
        var stopped = await runner.StopAsync(default);
        Assert.Equal("stopme", Assert.Single(stopped).Id);
        Assert.DoesNotContain(900, host.Stopped);
    }

    [Fact]
    public async Task Stop_with_Console_stops_each_started_app_once_and_leaves_apps_without_the_option_running()
    {
        var host = new FakeHost();
        SupportAppStore store = NewStore();
        string stopMe = Exe("stopme.exe"), keep = Exe("keep.exe");
        store.Save(new SupportAppSaveRequest(true, [Entry("stopme", stopMe, e => e with { StopWithConsole = true }), Entry("keep", keep)]));
        SupportAppRunner runner = Runner(store, host);
        await runner.StartAsync(default);
        Assert.Equal(2, host.Started.Count);

        Assert.Equal("stopme", Assert.Single(await runner.StopAsync(default)).Id);
        Assert.Empty(await runner.StopAsync(default));          // a second Console end never stops anything twice
        Assert.Single(host.Stopped);
        Assert.Single(host.Find("keep"));                        // the app without the option is still running
        Assert.Empty(host.Find("stopme"));
    }

    [Fact]
    public async Task Stop_with_Console_reports_a_failed_stop_honestly_instead_of_claiming_success()
    {
        var host = new FakeHost { StopSucceeds = false };
        SupportAppStore store = NewStore();
        store.Save(new SupportAppSaveRequest(true, [Entry("stopme", Exe("stopme.exe"), e => e with { StopWithConsole = true })]));
        SupportAppRunner runner = Runner(store, host);
        await runner.StartAsync(default);
        SupportAppResult r = Assert.Single(await runner.StopAsync(default));
        Assert.Equal(SupportAppOutcome.NotStopped, r.Outcome);
        Assert.Empty(host.Stopped);
    }

    [Fact]
    public async Task Switching_Stop_with_Console_off_before_the_Console_ends_is_honoured()
    {
        var host = new FakeHost();
        SupportAppStore store = NewStore();
        string exe = Exe("later.exe");
        store.Save(new SupportAppSaveRequest(true, [Entry("later", exe, e => e with { StopWithConsole = true })]));
        SupportAppRunner runner = Runner(store, host);
        await runner.StartAsync(default);
        store.Save(new SupportAppSaveRequest(true, [Entry("later", exe)]));   // the person turned the option off in Settings
        Assert.Empty(await runner.StopAsync(default));
        Assert.Empty(host.Stopped);
    }

    [Fact]
    public void Settings_explains_Stop_with_Console_in_both_languages_next_to_the_checkbox()
    {
        string src = File.ReadAllText(Path.Combine(RepoRootForSource(), "src", "PaRuX.App", "Views", "SettingsView.LogonStartup.cs"));
        Assert.Contains("Stop when Console ends", src);
        Assert.Contains("Ferma a fine Console", src);
        Assert.Contains("StopWithConsoleHelp", src);              // the explanation text, shown under the options row
        Assert.Contains("only apps PaRuX started itself", src);   // honest scope: a copy that was already running is never stopped
        Assert.Contains("solo le app avviate da PaRuX", src);
    }

    private static string RepoRootForSource()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    // ---- catalog ----

    [Fact]
    public void Catalog_downloads_are_pinned_https_with_hash_size_and_license_and_the_rest_are_locate_only_with_a_reason()
    {
        Assert.NotEmpty(SupportAppCatalog.All);
        foreach (SupportAppCatalogEntry c in SupportAppCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.License));
            Assert.Equal(c.Id, c.Id.ToLowerInvariant());
            Assert.StartsWith("https://", c.ProjectUrl);
            if (c.Download is { } d)
            {
                Assert.Null(SupportAppInstaller.ValidateCatalogDownload(d));
                Assert.StartsWith("https://", d.SourcePage);
            }
            else
            {
                Assert.NotNull(c.NoDownloadReason);
                Assert.NotEqual(c.NoDownloadReason!.En, c.NoDownloadReason.It);
            }
        }
        Assert.Contains(SupportAppCatalog.All, c => c.Download is null);   // unverified sources stay locate-only
    }

    // ---- installer (fake HTTP) ----

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(respond(request)); }
    }

    private static byte[] Zip(params (string Name, string Body)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, b) in files) { var e = z.CreateEntry(n); using var w = new StreamWriter(e.Open()); w.Write(b); }
        return ms.ToArray();
    }

    private (SupportAppInstaller Installer, SupportAppStore Store, FakeHandler Http, string Managed) Installer(byte[] payload, long? pinnedSize = null, string? pinnedSha = null, string relExe = "tool.exe")
    {
        string sha = pinnedSha ?? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var entry = new SupportAppCatalogEntry("nyrna", "Test helper", new("p", "p-it"), "MIT", "https://example.invalid/p",
            new SupportAppDownload("https://example.invalid/tool.zip", "9.9", sha, pinnedSize ?? payload.Length, relExe, "https://example.invalid/rel"), null);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        string managed = Path.Combine(_root, "managed");
        SupportAppStore store = NewStore();
        var installer = new SupportAppInstaller(new HttpClient(handler), managed, store, new FakeHost(), () => false, id => id == "nyrna" ? entry : null);
        return (installer, store, handler, managed);
    }

    [Fact]
    public async Task Install_needs_explicit_consent_to_the_exact_version_and_downloads_nothing_without_it()
    {
        var (installer, _, http, _) = Installer(Zip(("tool.exe", "MZ")));
        Assert.False((await installer.InstallAsync(new SupportAppInstallRequest("nyrna", "9.9", false), default)).Ok);
        Assert.False((await installer.InstallAsync(new SupportAppInstallRequest("nyrna", "1.0", true), default)).Ok);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task Install_verifies_the_hash_extracts_into_the_managed_folder_and_adds_an_entry_that_does_not_start_by_itself()
    {
        var (installer, store, _, managed) = Installer(Zip(("tool.exe", "MZ"), ("data/readme.txt", "hi")));
        SupportAppInstallResult r = await installer.InstallAsync(new SupportAppInstallRequest("nyrna", "9.9", true), default);
        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(managed, "nyrna", "9.9", "tool.exe")));
        SupportAppEntry e = Assert.Single(store.Load().Apps);
        Assert.False(e.StartWithConsole);
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, SupportAppPolicy.OwnershipOf(e, managed));
        Assert.Empty(Directory.GetFileSystemEntries(managed).Where(p => Path.GetFileName(p).StartsWith(".staging")));
    }

    [Fact]
    public async Task A_wrong_hash_a_wrong_size_or_a_missing_exe_keeps_nothing()
    {
        byte[] good = Zip(("tool.exe", "MZ"));
        var (badHash, _, _, m1) = Installer(good, pinnedSha: new string('0', 64));
        Assert.Contains("SHA-256", (await badHash.InstallAsync(new SupportAppInstallRequest("nyrna", "9.9", true), default)).Message);
        Assert.False(Directory.Exists(Path.Combine(m1, "nyrna")));

        var (badSize, _, _, m2) = Installer(good, pinnedSize: good.Length - 1);
        Assert.False((await badSize.InstallAsync(new SupportAppInstallRequest("nyrna", "9.9", true), default)).Ok);
        Assert.False(Directory.Exists(Path.Combine(m2, "nyrna")));

        var (noExe, store, _, m3) = Installer(Zip(("other.exe", "MZ")));
        Assert.False((await noExe.InstallAsync(new SupportAppInstallRequest("nyrna", "9.9", true), default)).Ok);
        Assert.False(Directory.Exists(Path.Combine(m3, "nyrna")));
        Assert.Empty(store.Load().Apps);
    }

    [Fact]
    public async Task A_package_with_a_path_that_escapes_the_folder_is_refused()
    {
        var (installer, _, _, managed) = Installer(Zip(("tool.exe", "MZ"), ("../evil.txt", "x")));
        SupportAppInstallResult r = await installer.InstallAsync(new SupportAppInstallRequest("nyrna", "9.9", true), default);
        Assert.False(r.Ok);
        Assert.False(File.Exists(Path.Combine(managed, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task Install_is_refused_while_gaming_and_for_a_locate_only_entry()
    {
        var entry = SupportAppCatalog.All.First(c => c.Download is null);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var installer = new SupportAppInstaller(new HttpClient(handler), Path.Combine(_root, "managed"), NewStore(), new FakeHost(), () => false);
        Assert.False((await installer.InstallAsync(new SupportAppInstallRequest(entry.Id, "1", true), default)).Ok);
        var gaming = new SupportAppInstaller(new HttpClient(handler), Path.Combine(_root, "managed"), NewStore(), new FakeHost(), () => true);
        Assert.False((await gaming.InstallAsync(new SupportAppInstallRequest("nyrna", "2.27.2", true), default)).Ok);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Remove_deletes_only_the_managed_copy_and_its_list_entry_and_never_a_running_program()
    {
        var (installer, store, _, managed) = Installer(Zip(("tool.exe", "MZ")));
        await installer.InstallAsync(new SupportAppInstallRequest("nyrna", "9.9", true), default);
        string userCopy = Exe("tool.exe");
        Assert.True(installer.Remove("nyrna").Ok);
        Assert.False(Directory.Exists(Path.Combine(managed, "nyrna")));
        Assert.Empty(store.Load().Apps);
        Assert.True(File.Exists(userCopy));   // the owner's own copy is never touched
        Assert.False(installer.Remove("nyrna").Ok);
        Assert.False(installer.Remove("not-in-catalog").Ok);
    }
}
