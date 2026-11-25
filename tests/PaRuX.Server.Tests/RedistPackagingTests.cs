using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Optional vendor payloads of the MSI (ViGEmBus, HidHide, Sunshine): the lock file, its pure policy, the WiX contracts, the
/// helper script's self-test, detection/ownership and the offline Sunshine install. Synthetic data and fakes only: nothing is
/// downloaded, installed, started or read from the machine.
/// </summary>
public sealed class RedistPackagingTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "parux-redist-" + Guid.NewGuid().ToString("N"));
    public RedistPackagingTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    private static string Read(params string[] relative) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
    private static IReadOnlyList<RedistLockComponent> Lock() => RedistLockPolicy.Parse(Read("packaging", "redist-installers.lock.json"));

    // ---------------- lock policy ----------------

    [Fact]
    public void The_committed_lock_is_valid_and_pins_the_three_official_assets()
    {
        IReadOnlyList<RedistLockComponent> all = Lock();
        Assert.Equal(new[] { "hidhide", "sunshine", "vigembus" }, all.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal));
        foreach (RedistLockComponent c in all) Assert.Empty(RedistLockPolicy.Validate(c));
        Assert.All(all, c => Assert.Equal(64, c.Sha256.Length));
        Assert.All(all, c => Assert.Contains("/releases/download/" + c.ReleaseTag + "/", c.AssetUrl));
    }

    [Fact]
    public void Elevated_vendor_installers_need_a_pinned_authenticode_publisher_and_only_documented_silent_switches()
    {
        foreach (RedistLockComponent c in Lock().Where(c => c.Elevated))
        {
            Assert.True(c.SignatureRequired);
            Assert.Matches("^[0-9A-F]{40}$", c.SignatureThumbprint!);
            Assert.StartsWith("CN=Nefarius Software Solutions", c.SignatureSubject);
            Assert.Equal(new[] { "/exenoui", "/qn", "/norestart" }, c.SilentArgs);
        }
        Assert.DoesNotContain(Lock(), c => c.Id == "sunshine" && c.Elevated);   // Sunshine is never executed by the setup
    }

    [Fact]
    public void Licences_are_the_reviewed_ones_and_gpl_carries_a_same_tag_source_offer_with_files_in_the_repo()
    {
        var byId = Lock().ToDictionary(c => c.Id);
        Assert.Equal("BSD-3-Clause", byId["vigembus"].LicenseSpdx);
        Assert.Equal("MIT", byId["hidhide"].LicenseSpdx);
        Assert.Equal("GPL-3.0-only", byId["sunshine"].LicenseSpdx);
        foreach (RedistLockComponent c in byId.Values)
            Assert.True(File.Exists(Path.Combine(RepoRoot(), c.LicenseTextFile.Replace('/', Path.DirectorySeparatorChar))), c.LicenseTextFile);
        RedistLockComponent s = byId["sunshine"];
        Assert.Contains(s.ReleaseTag, s.SourceOfferRepository);
        Assert.Contains(s.ReleaseTag, s.SourceOfferArchive);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), s.SourceOfferTextFile!.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", Read("packaging", "redist-licenses", "LICENSE-Sunshine.txt"));
        Assert.Contains("BSD 3-Clause", Read("packaging", "redist-licenses", "LICENSE-ViGEmBus.txt"));
        Assert.Contains("MIT License", Read("packaging", "redist-licenses", "LICENSE-HidHide.txt"));
        Assert.Contains(s.ReleaseTag, Read("packaging", "redist-licenses", "SOURCE-OFFER-Sunshine.txt"));
    }

    [Fact]
    public void Notices_name_every_component_version_hash_and_the_source_offer()
    {
        string notices = Read("THIRD_PARTY_NOTICES.md");
        foreach (RedistLockComponent c in Lock())
        {
            Assert.Contains(c.Version, notices);
            Assert.Contains(c.Sha256, notices);
            Assert.Contains(c.AssetName, notices);
            Assert.Contains(c.LicenseSpdx, notices);
        }
        Assert.Contains("https://github.com/SunshineStream/Sunshine/tree/v2026.914.233613", notices);
    }

    private static RedistLockComponent Good() => Lock().Single(c => c.Id == "vigembus");

    [Fact]
    public void Validation_rejects_unpinned_unofficial_or_weakened_entries()
    {
        RedistLockComponent g = Good();
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { AssetUrl = g.AssetUrl.Replace("https://", "http://") }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { AssetUrl = "https://github.com/evil/ViGEmBus/releases/download/" + g.ReleaseTag + "/" + g.AssetName }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { AssetUrl = "https://example.com/releases/download/" + g.ReleaseTag + "/" + g.AssetName }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { AssetUrl = "https://github.com/nefarius/ViGEmBus/releases/latest/download/" + g.AssetName }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { ReleaseTag = "v9.9.9" }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { Sha256 = "ABC" }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { Sha256 = new string('A', 64) }));   // upper case is not the canonical pin
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { GithubDigest = "sha256:" + new string('0', 64) }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { SignatureRequired = false }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { SignatureThumbprint = null }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { SilentArgs = new[] { "/qn", "/norestart", "/forcerestart" } }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { SilentArgs = new[] { "/qn" } }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { LicenseSpdx = "Proprietary" }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { MsiFeature = "" }));
        Assert.NotEmpty(RedistLockPolicy.Validate(g with { InstallType = "portable-zip-app-extract" }));   // elevated non-installer
        RedistLockComponent sun = Lock().Single(c => c.Id == "sunshine");
        Assert.NotEmpty(RedistLockPolicy.Validate(sun with { SourceOfferArchive = "https://github.com/SunshineStream/Sunshine/archive/refs/heads/master.tar.gz" }));
        Assert.NotEmpty(RedistLockPolicy.Validate(sun with { SourceOfferRepository = null }));
        Assert.NotEmpty(RedistLockPolicy.Validate(sun with { SilentArgs = new[] { "/qn" } }));   // never executed, so no arguments
    }

    [Fact]
    public void Payload_and_signature_gates_fail_closed()
    {
        RedistLockComponent g = Good();
        Assert.Null(RedistLockPolicy.CheckPayload(g, g.SizeBytes, g.Sha256.ToUpperInvariant()));
        Assert.NotNull(RedistLockPolicy.CheckPayload(g, g.SizeBytes + 1, g.Sha256));
        Assert.NotNull(RedistLockPolicy.CheckPayload(g, g.SizeBytes, new string('0', 64)));
        Assert.Null(RedistLockPolicy.CheckSignature(g, "Valid", g.SignatureSubject + ", O=x", g.SignatureThumbprint!.ToLowerInvariant()));
        Assert.NotNull(RedistLockPolicy.CheckSignature(g, "NotSigned", null, null));
        Assert.NotNull(RedistLockPolicy.CheckSignature(g, "HashMismatch", g.SignatureSubject, g.SignatureThumbprint));
        Assert.NotNull(RedistLockPolicy.CheckSignature(g, "Valid", "CN=Someone Else", g.SignatureThumbprint));
        Assert.NotNull(RedistLockPolicy.CheckSignature(g, "Valid", g.SignatureSubject, new string('A', 40)));
        Assert.Null(RedistLockPolicy.CheckSignature(Lock().Single(c => c.Id == "sunshine"), "NotSigned", null, null));   // a ZIP has no signature to require
    }

    // ---------------- helper script ----------------

    [Fact]
    public void The_elevated_helper_passes_its_own_synthetic_self_test_and_installs_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        string script = Path.Combine(RepoRoot(), "scripts", "redist", "Invoke-PaRuXRedist.ps1");
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Mode SelfTest")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60_000));
        Assert.True(p.ExitCode == 0, output + p.StandardError.ReadToEnd());
        Assert.Contains("REDIST_SELFTEST_OK", output);
    }

    [Fact]
    public void The_helper_never_downloads_never_uses_latest_and_only_runs_what_the_lock_pins()
    {
        string helper = Read("scripts", "redist", "Invoke-PaRuXRedist.ps1");
        Assert.DoesNotContain("Invoke-WebRequest", helper);
        Assert.DoesNotContain("Invoke-RestMethod", helper);
        Assert.DoesNotContain("DownloadFile", helper);
        Assert.DoesNotContain("/latest", helper);
        Assert.Contains("Test-RedistPayload", helper);
        Assert.Contains("Get-AuthenticodeSignature", helper);
        Assert.Contains("SkipExistingNotOurs", helper);             // never touches an installation it did not make
        Assert.Contains("InstalledByPaRuX", helper);                // uninstall only what it recorded
        Assert.Contains("redist-install.log", helper);              // logs under ProgramData\PaRuX\logs
        Assert.DoesNotContain("/forcerestart", helper.Replace("'/forcerestart'", string.Empty).Split("Assert-Eq", 2)[0]);   // never forces a restart outside the self-test
    }

    [Fact]
    public void The_build_scripts_stage_only_from_the_lock_and_offer_NoRedist()
    {
        string stage = Read("scripts", "stage-msi-redist.ps1");
        Assert.Contains("redist-installers.lock.json", stage);
        Assert.Contains("Test-RedistPayload", stage);
        Assert.Contains("Test-RedistSignature", stage);
        Assert.Contains("-NoDownload", stage);
        Assert.DoesNotContain("/latest", stage.Replace("'/latest/'", string.Empty));
        Assert.DoesNotContain("api.github.com", stage);             // no tag resolution at build time: pins only
        string build = Read("scripts", "build-msi.ps1");
        Assert.Contains("[switch]$NoRedist", build);
        Assert.Contains("stage-msi-redist.ps1", build);
        Assert.Contains("PaRuXMsiRedist", build);
        string wixproj = Read("installer", "PaRuX.Setup", "PaRuX.Setup.wixproj");
        Assert.Contains("PaRuXMsiRedist", wixproj);
        Assert.Contains("IncludeRedist=$(PaRuXIncludeRedist)", wixproj);
        Assert.Matches(@"Name=""StagePaRuXRedistPayload""[^>]*'\$\(PaRuXMsiRedist\)' != 'none'", wixproj);
        // Binaries never enter git: the cache and stage folders are ignored.
        Assert.Contains("artifacts/", Read(".gitignore"));
    }

    // ---------------- WiX contracts ----------------

    private static string Wxs() => Read("installer", "PaRuX.Setup", "Package.wxs");
    private static string RedistBlock()
    {
        string wxs = Wxs();
        int start = wxs.IndexOf("<?if $(IncludeRedist) = 1?>", StringComparison.Ordinal);
        int end = wxs.IndexOf("<?endif?>", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "The redist authoring must be wrapped in the IncludeRedist preprocessor condition.");
        return wxs[start..end];
    }

    [Fact]
    public void The_two_optional_features_are_off_by_default_selectable_and_have_explicit_switches()
    {
        string block = RedistBlock();
        MatchCollection features = Regex.Matches(block, @"<Feature Id=""(?<id>\w+)""[^>]*?Level=""(?<level>\d+)""", RegexOptions.Singleline);
        var level = features.ToDictionary(m => m.Groups["id"].Value, m => int.Parse(m.Groups["level"].Value));
        Assert.Equal(1, level["PaRuXMain"]);
        Assert.True(level["PaRuXControllerDrivers"] > 1, "Drivers must not be installed at the default INSTALLLEVEL.");
        Assert.True(level["PaRuXSunshine"] > 1, "Sunshine must not be installed at the default INSTALLLEVEL.");
        Assert.Contains("<Level Value=\"1\" Condition=\"PARUX_ADD_DRIVERS = &quot;1&quot;\"", block);
        Assert.Contains("<Level Value=\"1\" Condition=\"PARUX_ADD_SUNSHINE = &quot;1&quot;\"", block);
        Assert.Contains("Controller drivers (ViGEmBus + HidHide)", block);
        Assert.Contains("Sunshine (streaming host)", block);
        Assert.Contains("<Property Id=\"PARUX_ADD_DRIVERS\" Secure=\"yes\"", block);
        Assert.Contains("<Property Id=\"PARUX_ADD_SUNSHINE\" Secure=\"yes\"", block);
        // Two unchecked boxes on an extra wizard page, wired after the install-folder page.
        Assert.Contains("Property=\"PARUX_ADD_DRIVERS\"", block);
        Assert.Contains("Property=\"PARUX_ADD_SUNSHINE\"", block);
        Assert.DoesNotMatch(@"Id=""(Drivers|Sunshine)Check""[^>]*Checked", block);
    }

    [Fact]
    public void Nothing_redist_related_exists_when_built_with_NoRedist()
    {
        string withoutBlock = Regex.Replace(Wxs(), @"<\?if \$\(IncludeRedist\) = 1\?>.*?<\?endif\?>", string.Empty, RegexOptions.Singleline);
        withoutBlock = Regex.Replace(withoutBlock, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotContain("Redist", withoutBlock.Replace("IncludeRedist", string.Empty));
        Assert.DoesNotContain("Invoke-PaRuXRedist", withoutBlock);
        foreach (Match ca in Regex.Matches(withoutBlock, @"<CustomAction .*?/>", RegexOptions.Singleline))
            Assert.Contains("Id=\"PaRuXLogonCleanup", ca.Value);
        Assert.DoesNotContain("PARUX_ADD_", withoutBlock);
        Assert.DoesNotContain("<Feature", withoutBlock);
    }

    [Fact]
    public void The_driver_custom_actions_are_deferred_elevated_conditioned_on_the_feature_and_never_fail_the_setup()
    {
        string block = RedistBlock();
        foreach (Match ca in Regex.Matches(block, @"<CustomAction .*?/>", RegexOptions.Singleline))
        {
            string t = ca.Value;
            Assert.Contains("Execute=\"deferred\"", t);
            Assert.Contains("Impersonate=\"no\"", t);       // runs elevated (as the installer service), only deferred
            Assert.Contains("Return=\"ignore\"", t);        // a vendor problem never fails or rolls back PaRuX's own setup
            Assert.Contains("Invoke-PaRuXRedist.ps1", t);
            Assert.Contains("-Mode Launch", t);
            Assert.DoesNotContain("-Mode Work", t);
            Assert.DoesNotContain("msiexec", t, StringComparison.OrdinalIgnoreCase);   // a nested MSI would fail with 1618
            Assert.DoesNotContain("Bypass -Command", t);
        }
        Assert.Equal(2, Regex.Matches(block, "<CustomAction ").Count);
        Assert.Contains("<Custom Action=\"PaRuXRedistDriversInstall\" After=\"InstallFiles\" Condition=\"&amp;PaRuXControllerDrivers = 3\" />", block);
        // Uninstall: only on a full removal, never on a major upgrade, and always before the helper is deleted.
        Assert.Contains("<Custom Action=\"PaRuXRedistDriversUninstall\" Before=\"RemoveFiles\" Condition=\"REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE\" />", block);
        // No custom action may exist for Sunshine: no service, firewall rule or process is created by the setup.
        Assert.DoesNotMatch("(?i)CustomAction[^>]*sunshine", block);
        Assert.DoesNotContain("ServiceInstall", block);
        Assert.DoesNotContain("Firewall", block);
    }

    [Fact]
    public void Payload_files_live_in_their_own_feature_and_a_rolled_back_feature_leaves_the_marker_the_helper_requires()
    {
        string block = RedistBlock();
        Assert.Matches(@"ComponentGroup Id=""PaRuXControllerDriversPayload""[^>]*Directory=""REDISTDRIVERSDIR""", block);
        Assert.Matches(@"ComponentGroup Id=""PaRuXSunshinePayload""[^>]*Directory=""REDISTSUNSHINEDIR""", block);
        Assert.Contains(@"Name=""PaRuXControllerDrivers"" Type=""integer"" Value=""1"" KeyPath=""yes""", block);
        Assert.Contains(@"Software\Ruba94-cyber\PaRuX\Features", block);
        string helper = Read("scripts", "redist", "Invoke-PaRuXRedist.ps1");
        Assert.Contains("Test-RedistFeatureSelected", helper);   // reads that very marker before doing anything
        Assert.Contains("CliPathRegistered", helper);            // uninstall helper checks the product is really gone
        // The main harvest must not swallow the optional payload.
        Assert.DoesNotContain("Redist", Regex.Match(Wxs(), @"<ComponentGroup Id=""PaRuXAppFiles"".*?</ComponentGroup>", RegexOptions.Singleline).Value);
    }

    [Fact]
    public void Uninstall_leaves_no_empty_Redist_folder_behind_in_the_setup_or_in_the_helper()
    {
        string block = RedistBlock();
        // Declarative cleanup: every runtime-touched folder has an On="uninstall" RemoveFolder, in an always-installed feature.
        foreach (string dir in new[] { "REDISTDIR", "REDISTDRIVERSDIR", "REDISTSUNSHINEDIR" })
            Assert.Matches($@"<RemoveFolder Id=""\w+"" Directory=""{dir}"" On=""uninstall"" />", block);
        Match baseGroup = Regex.Match(block, @"<ComponentGroup Id=""PaRuXRedistBase"".*?</ComponentGroup>", RegexOptions.Singleline);
        Assert.Contains("PaRuXRedistFolderCleanup", baseGroup.Value);
        // Belt and braces: the post-setup helper removes only EMPTY folders under a folder named Redist, after the uninstall work.
        string helper = Read("scripts", "redist", "Invoke-PaRuXRedist.ps1");
        Assert.Contains("function Remove-RedistEmptyFolders", helper);
        Assert.Contains("Invoke-RedistUninstall $lock; Remove-RedistEmptyFolders $RedistRoot", helper);
        Assert.Contains("-ne 'Redist'", helper);
        Assert.Contains(".Count -eq 0", helper);
        string cleanup = Regex.Match(helper, @"function Remove-RedistEmptyFolders.*?\n}", RegexOptions.Singleline).Value;
        Assert.NotEmpty(cleanup);
        Assert.DoesNotContain("-Recurse -Force -ErrorAction Stop", cleanup);   // removes empty directories only, never a tree
        Assert.DoesNotMatch(@"Remove-Item[^\r\n]*-Recurse", cleanup);    }

    [Fact]
    public void The_lock_feature_ids_match_the_wix_features()
    {
        string block = RedistBlock();
        foreach (RedistLockComponent c in Lock())
            Assert.Contains($"<Feature Id=\"{c.MsiFeature}\"", block);
    }

    // ---------------- catalog, detection, ownership ----------------

    [Fact]
    public void The_support_app_catalog_matches_the_lock_for_sunshine_and_lists_the_two_drivers_as_never_downloaded()
    {
        RedistLockComponent s = Lock().Single(c => c.Id == "sunshine");
        SupportAppCatalogEntry e = SupportAppCatalog.Find("sunshine")!;
        Assert.Equal(s.AssetUrl, e.Download!.Url);
        Assert.Equal(s.Version, e.Download.Version);
        Assert.Equal(s.Sha256, e.Download.Sha256);
        Assert.Equal(s.SizeBytes, e.Download.SizeBytes);
        Assert.Null(SupportAppInstaller.ValidateCatalogDownload(e.Download));
        Assert.Equal("sunshine", e.RedistId);
        foreach (string id in new[] { "vigembus", "hidhide" })
        {
            SupportAppCatalogEntry d = SupportAppCatalog.Find(id)!;
            Assert.Equal(SupportAppKind.Driver, d.Kind);
            Assert.Null(d.Download);                          // PaRuX itself never downloads or installs a driver
            Assert.NotNull(d.NoDownloadReason);
            Assert.Equal(id, d.RedistId);
        }
        Assert.Contains(SupportAppCatalog.All, c => c.Download is null && c.Kind == SupportAppKind.Program);   // XOutput stays locate-only
    }

    private sealed class FakeProbe : IRedistProbe
    {
        public Dictionary<string, RedistProbeResult> Present = new();
        public Dictionary<string, RedistSetupRecord> Records = new();
        public HashSet<string> Bundled = new();
        public RedistProbeResult Probe(string id) => Present.TryGetValue(id, out var r) ? r : new(false, null, null);
        public RedistSetupRecord? ReadSetupRecord(string id) => Records.TryGetValue(id, out var r) ? r : null;
        public bool BundledPayloadAvailable(string id) => Bundled.Contains(id);
    }

    [Fact]
    public void Ownership_follows_who_installed_it_and_a_foreign_copy_is_never_claimed()
    {
        // Installed by the PaRuX setup and present -> PARUX_MANAGED.
        var ours = RedistStatusPolicy.Decide("hidhide", "HidHide", new(true, "p", "1.5"), new(true, "1.5.230.0", false), false, false, false);
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, ours.Ownership);
        Assert.Equal(RedistState.Installed, ours.State);
        // Present, no PaRuX record -> SYSTEM_DETECTED (found by looking) or USER_MANAGED (the owner picked it).
        Assert.Equal(ComponentOwnership.SYSTEM_DETECTED, RedistStatusPolicy.Decide("hidhide", "HidHide", new(true, "p", null), null, false, false, false).Ownership);
        Assert.Equal(ComponentOwnership.USER_MANAGED, RedistStatusPolicy.Decide("sunshine", "Sunshine", new(true, "p", null), null, true, false, false).Ownership);
        // A record from the PaRuX setup alone never turns a foreign present copy into PaRuX's: the record is HKLM state written by the helper only.
        var foreign = RedistStatusPolicy.Decide("vigembus", "ViGEmBus", new(true, "p", null), new(false, null, false), false, false, false);
        Assert.Equal(ComponentOwnership.SYSTEM_DETECTED, foreign.Ownership);
        // Driver installed by the setup but not loaded yet -> restart required, still PARUX_MANAGED.
        var reboot = RedistStatusPolicy.Decide("vigembus", "ViGEmBus", new(false, null, null), new(true, "1.22.0", true), false, false, false);
        Assert.Equal(RedistState.RestartRequired, reboot.State);
        Assert.True(reboot.RestartRequired);
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, reboot.Ownership);
        Assert.Contains("Restart", reboot.Message);
        // Recorded but gone -> an honest "missing", not "installed".
        Assert.Equal(RedistState.MissingAfterInstall, RedistStatusPolicy.Decide("vigembus", "ViGEmBus", new(false, null, null), new(true, "1.22.0", false), false, false, false).State);
        // Nothing anywhere: not installed, no ownership; bundled Sunshine says it can be installed offline.
        var none = RedistStatusPolicy.Decide("sunshine", "Sunshine", new(false, null, null), null, false, false, true);
        Assert.Equal(RedistState.NotInstalled, none.State);
        Assert.Null(none.Ownership);
        Assert.Contains("bundled", none.Message);
        Assert.NotEqual(none.Message, none.MessageIt);
        // Sunshine unpacked by PaRuX into its own folder -> PARUX_MANAGED without any HKLM record.
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, RedistStatusPolicy.Decide("sunshine", "Sunshine", new(true, "p", "1"), null, false, true, false).Ownership);
    }

    [Fact]
    public void Detector_rows_cover_the_three_components_for_the_catalog_and_the_runtime_inventory()
    {
        var probe = new FakeProbe();
        probe.Present["vigembus"] = new(true, @"C:\Windows\System32\drivers\ViGEmBus.sys", "1.17.333.0");
        probe.Records["hidhide"] = new(true, "1.5.230.0", true);
        probe.Bundled.Add("sunshine");
        var list = new SupportAppListState(false, Array.Empty<SupportAppEntry>(), false);
        IReadOnlyList<RedistStatusRow> rows = RedistDetector.BuildRows(probe, list);
        Assert.Equal(new[] { "sunshine", "vigembus", "hidhide" }, rows.Select(r => r.Id));
        Assert.Equal(ComponentOwnership.SYSTEM_DETECTED, rows.Single(r => r.Id == "vigembus").Ownership);   // already there: left alone
        Assert.Equal(RedistState.RestartRequired, rows.Single(r => r.Id == "hidhide").State);
        Assert.True(rows.Single(r => r.Id == "sunshine").BundledPayloadAvailable);

        ComponentStatus[] inventory = ComponentInventoryService.RedistComponents(probe).ToArray();
        Assert.Equal(3, inventory.Length);
        Assert.All(inventory, i => Assert.Empty(i.Actions));   // the Runtime Manager offers no install/remove for these
        Assert.Equal("restart-required", inventory.Single(i => i.Id == "hidhide").Health);
        Assert.True(inventory.Single(i => i.Id == "vigembus").Detected);
        Assert.False(inventory.Single(i => i.Id == "sunshine").Detected);
    }

    // ---------------- offline Sunshine install ----------------

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
    }

    private sealed class NoHost : ISupportAppHost
    {
        public IReadOnlyList<SupportAppProcess> Find(string processName) => Array.Empty<SupportAppProcess>();
        public SupportAppProcess? Start(string path, string? args, bool minimized) => null;
        public bool Stop(SupportAppProcess process) => false;
        public DateTime? ExeWriteUtc(string path) => null;
    }

    private static byte[] Zip(params (string Name, string Body)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, b) in files) { var e = z.CreateEntry(n); using var w = new StreamWriter(e.Open()); w.Write(b); }
        return ms.ToArray();
    }

    private (SupportAppInstaller Installer, CountingHandler Http, string Managed, SupportAppStore Store) BundledInstaller(byte[] zip, byte[] bundledFile)
    {
        string sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var entry = new SupportAppCatalogEntry("sunshine", "Sunshine", new("p", "p-it"), "GPL-3.0", "https://example.invalid/p",
            new SupportAppDownload("https://example.invalid/Sunshine-lite.zip", "1.0", sha, zip.Length, "Sunshine/sunshine.exe", "https://example.invalid/rel"), null,
            SupportAppKind.Program, "sunshine");
        string bundled = Path.Combine(_tmp, "Redist", "Sunshine", "Sunshine-lite.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllBytes(bundled, bundledFile);
        var http = new CountingHandler();
        string managed = Path.Combine(_tmp, "managed");
        var store = new SupportAppStore(Path.Combine(_tmp, "server", "support-apps.json"));
        var installer = new SupportAppInstaller(new HttpClient(http), managed, store, new NoHost(), () => false, id => id == "sunshine" ? entry : null, _ => bundled);
        return (installer, http, managed, store);
    }

    [Fact]
    public async Task Bundled_Sunshine_installs_offline_into_the_per_user_managed_folder_without_starting_anything()
    {
        byte[] zip = Zip(("Sunshine/sunshine.exe", "MZ"), ("Sunshine/scripts/install-service.bat", "rem never run"));
        var (installer, http, managed, store) = BundledInstaller(zip, zip);
        SupportAppInstallResult r = await installer.InstallAsync(new SupportAppInstallRequest("sunshine", "1.0", true), default);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, http.Calls);                                                   // no network at all
        Assert.True(File.Exists(Path.Combine(managed, "sunshine", "1.0", "Sunshine", "sunshine.exe")));
        SupportAppEntry e = Assert.Single(store.Load().Apps);
        Assert.False(e.StartWithConsole);                                              // never started until the owner enables it
        Assert.Equal(ComponentOwnership.PARUX_MANAGED, SupportAppPolicy.OwnershipOf(e, managed));
        Assert.Equal(Path.Combine(managed, "sunshine", "1.0", "Sunshine", "sunshine.exe"), Providers_FindManaged(managed));
    }

    private static string? Providers_FindManaged(string root) =>
        PaRuX.Server.Orchestration.Gaming.Providers.SunshineSessionTriggerProvider.FindManagedExecutable(root);

    [Fact]
    public async Task A_bundled_package_that_does_not_match_the_pin_is_refused_and_never_replaced_by_a_download()
    {
        byte[] zip = Zip(("Sunshine/sunshine.exe", "MZ"));
        byte[] tampered = Zip(("Sunshine/sunshine.exe", "MZ-evil-longer"));
        var (installer, http, managed, store) = BundledInstaller(zip, tampered);
        SupportAppInstallResult r = await installer.InstallAsync(new SupportAppInstallRequest("sunshine", "1.0", true), default);
        Assert.False(r.Ok);
        Assert.Equal(0, http.Calls);
        Assert.False(Directory.Exists(Path.Combine(managed, "sunshine")));
        Assert.Empty(store.Load().Apps);

        // Same size, different bytes: the SHA-256 gate catches it.
        byte[] sameSize = (byte[])zip.Clone();
        sameSize[^30] ^= 0xFF;
        var second = BundledInstaller(zip, sameSize);
        Assert.False((await second.Installer.InstallAsync(new SupportAppInstallRequest("sunshine", "1.0", true), default)).Ok);
        Assert.Empty(second.Store.Load().Apps);
    }

    [Fact]
    public void The_default_bundled_lookup_only_uses_the_fixed_Redist_folder_and_only_for_catalog_redist_entries()
    {
        Assert.Null(SupportAppInstaller.DefaultBundledPackage(SupportAppCatalog.Find("nyrna")!));      // no RedistId
        Assert.Null(SupportAppInstaller.DefaultBundledPackage(SupportAppCatalog.Find("vigembus")!));   // no download at all
        Assert.Null(SupportAppInstaller.DefaultBundledPackage(SupportAppCatalog.Find("sunshine")!));   // not present in the test output folder
    }
}
