using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The Corporate default model (Qwen3-4B) falls back to a smaller GGUF of the same package only when the memory plan refuses it
/// and nobody asked for a model. A fake planner stands in for Windows RAM: no process, no network, no real model is touched.
/// </summary>
public sealed class CorporateSmallModelFallbackTests : IDisposable
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-smallfb-" + Guid.NewGuid().ToString("N"));
    private readonly string _models;

    public CorporateSmallModelFallbackTests()
    {
        _models = Path.Combine(_root, "PaRuXData", "models");
        Directory.CreateDirectory(_models);
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
    }

    public void Dispose()
    {
        CorporateSmallModelFallback.ClearPublished();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Gguf(string name, int sizeKb)
    {
        string path = Path.Combine(_models, name);
        byte[] bytes = new byte[sizeKb * 1024];
        bytes[0] = (byte)'G'; bytes[1] = (byte)'G'; bytes[2] = (byte)'U'; bytes[3] = (byte)'F';
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Manifest(string json)
    {
        string path = Path.Combine(_root, "docs", "MANIFEST.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static readonly Func<string, bool> Usable = _ => true;
    private static readonly Func<string, string?, bool> NoHash = (_, _) => true;

    // ---- manifest -------------------------------------------------------------------------------------------------

    [Fact]
    public void Manifest_lists_the_alternate_model_with_its_pinned_hash()
    {
        string manifest = Manifest("{\"alternateModelIncluded\":true,\"alternateModel\":\"Qwen3-0.6B-Q8_0.gguf\",\"alternateModelSha256\":\"AB12\"}");
        var models = CorporateSmallModelFallback.ReadManifestModels(manifest);
        var one = Assert.Single(models);
        Assert.Equal("Qwen3-0.6B-Q8_0.gguf", one.FileName);
        Assert.Equal("AB12", one.Sha256);
    }

    [Fact]
    public void Manifest_without_an_included_alternate_or_with_unsafe_names_admits_nothing()
    {
        Assert.Empty(CorporateSmallModelFallback.ReadManifestModels(
            Manifest("{\"alternateModelIncluded\":false,\"alternateModel\":\"Qwen3-0.6B-Q8_0.gguf\"}")));
        Assert.Empty(CorporateSmallModelFallback.ReadManifestModels(
            Manifest("{\"alternateModelIncluded\":true,\"alternateModel\":\"..\\\\evil.gguf\"}")));
        Assert.Empty(CorporateSmallModelFallback.ReadManifestModels(
            Manifest("{\"alternateModelIncluded\":true,\"alternateModel\":\"mmproj-x.gguf\"}")));
        Assert.Empty(CorporateSmallModelFallback.ReadManifestModels(Path.Combine(_root, "docs", "missing.json")));
        Assert.Empty(CorporateSmallModelFallback.ReadManifestModels(Manifest("{ not json")));
    }

    [Fact]
    public void Manifest_array_can_list_a_second_small_model()
    {
        string manifest = Manifest("{\"alternateModelIncluded\":true,\"alternateModel\":\"Qwen3-0.6B-Q8_0.gguf\","
            + "\"alternateModels\":[{\"name\":\"Qwen3-1.7B-Q4_K_M.gguf\"},{\"name\":\"Qwen3-0.6B-Q8_0.gguf\"}]}");
        Assert.Equal(new[] { "Qwen3-0.6B-Q8_0.gguf", "Qwen3-1.7B-Q4_K_M.gguf" },
            CorporateSmallModelFallback.ReadManifestModels(manifest).Select(m => m.FileName));
    }

    [Fact]
    public void Manifest_is_found_next_to_the_data_root_of_the_extracted_package()
    {
        string manifest = Manifest("{}");
        Assert.Equal(manifest, CorporateSmallModelFallback.LocateManifest(Path.Combine(_root, "PaRuXData"), Path.Combine(_root, "none")));
    }

    // ---- candidates -----------------------------------------------------------------------------------------------

    [Fact]
    public void Only_installed_manifest_models_smaller_than_the_default_qualify_largest_first()
    {
        string def = Gguf("Qwen3-4B-Q4_K_M.gguf", 400);
        string mid = Gguf("Qwen3-1.7B-Q4_K_M.gguf", 200);
        string small = Gguf("Qwen3-0.6B-Q8_0.gguf", 100);
        Gguf("Imported-by-user-1B.gguf", 150);     // installed, small, but NOT in the package manifest
        Gguf("Qwen3-8B-Q4_K_M.gguf", 800);          // in the manifest but larger than the default
        var manifest = new[]
        {
            new CorporateSmallModelFallback.AdmittedModel("Qwen3-0.6B-Q8_0.gguf", null),
            new CorporateSmallModelFallback.AdmittedModel("Qwen3-1.7B-Q4_K_M.gguf", null),
            new CorporateSmallModelFallback.AdmittedModel("Qwen3-8B-Q4_K_M.gguf", null),
            new CorporateSmallModelFallback.AdmittedModel("Qwen3-4B-Q4_K_M.gguf", null),
            new CorporateSmallModelFallback.AdmittedModel("Not-installed.gguf", null)
        };

        IReadOnlyList<string> alternates = CorporateSmallModelFallback.AdmittedAlternates(def, _models, manifest, Usable, NoHash);

        Assert.Equal(new[] { mid, small }.Select(Path.GetFullPath), alternates);
    }

    [Fact]
    public void A_model_that_fails_its_pinned_hash_or_header_is_not_a_candidate()
    {
        string def = Gguf("Qwen3-4B-Q4_K_M.gguf", 400);
        string small = Gguf("Qwen3-0.6B-Q8_0.gguf", 100);
        string goodSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(small)));
        var good = new[] { new CorporateSmallModelFallback.AdmittedModel("Qwen3-0.6B-Q8_0.gguf", goodSha) };
        var bad = new[] { new CorporateSmallModelFallback.AdmittedModel("Qwen3-0.6B-Q8_0.gguf", new string('0', 64)) };

        Assert.Single(CorporateSmallModelFallback.AdmittedAlternates(def, _models, good, Usable, CorporateSmallModelFallback.VerifyAgainstManifest));
        Assert.Empty(CorporateSmallModelFallback.AdmittedAlternates(def, _models, bad, Usable, CorporateSmallModelFallback.VerifyAgainstManifest));
        Assert.Empty(CorporateSmallModelFallback.AdmittedAlternates(def, _models, good, _ => false, NoHash));
    }

    // ---- choice with a fake planner ---------------------------------------------------------------------------------

    /// <summary>Real planner arithmetic, fake RAM and per-model footprints: 4B needs ~3.1 GB, 1.7B ~1.9 GB, 0.6B ~0.8 GB.</summary>
    private static Func<string, bool> PlannerAdmits(SystemRamSnapshot ram, List<string>? asked = null) => path =>
    {
        asked?.Add(Path.GetFileName(path));
        double needed = Path.GetFileName(path) switch
        {
            "Qwen3-4B-Q4_K_M.gguf" => 3100,
            "Qwen3-1.7B-Q4_K_M.gguf" => 1900,
            _ => 800
        };
        return CorporateCpuMemoryPlanner.Select(ram, 86, 0, _ => needed).Fits;
    };

    [Fact]
    public void Default_refused_by_the_planner_and_small_model_accepted_selects_the_small_one()
    {
        // ~3.3 GB free of 25.8 GB (the acceptance harness): reserve 1.5 GiB, so about 1.8 GiB is usable.
        var ram = new SystemRamSnapshot((ulong)(25.8 * GiB), (ulong)(3.3 * GiB));
        Func<string, bool> admits = PlannerAdmits(ram);
        Assert.False(admits("Qwen3-4B-Q4_K_M.gguf"));          // the default is refused, as in production

        var selection = CorporateSmallModelFallback.Choose(
            new[] { "x/Qwen3-1.7B-Q4_K_M.gguf", "x/Qwen3-0.6B-Q8_0.gguf" }, admits, CancellationToken.None);

        Assert.Equal("x/Qwen3-0.6B-Q8_0.gguf", selection.ModelPath);
        Assert.Equal(2, selection.Tried.Count);                  // 1.7B asked first and refused, then 0.6B
    }

    [Fact]
    public void The_largest_admitted_small_model_wins_and_smaller_ones_are_not_even_asked()
    {
        var ram = new SystemRamSnapshot(16 * GiB, 5 * GiB);
        var asked = new List<string>();
        var selection = CorporateSmallModelFallback.Choose(
            new[] { "Qwen3-1.7B-Q4_K_M.gguf", "Qwen3-0.6B-Q8_0.gguf" }, PlannerAdmits(ram, asked), CancellationToken.None);
        Assert.Equal("Qwen3-1.7B-Q4_K_M.gguf", selection.ModelPath);
        Assert.Equal(new[] { "Qwen3-1.7B-Q4_K_M.gguf" }, asked);
    }

    [Fact]
    public void No_admitted_model_gives_an_honest_refusal_with_guidance_and_nothing_to_launch()
    {
        var ram = new SystemRamSnapshot(8 * GiB, (ulong)(1.2 * GiB));
        var selection = CorporateSmallModelFallback.Choose(
            new[] { "Qwen3-1.7B-Q4_K_M.gguf", "Qwen3-0.6B-Q8_0.gguf" }, PlannerAdmits(ram), CancellationToken.None);
        Assert.Null(selection.ModelPath);

        string refusal = CorporateSmallModelFallback.AppendNoSmallModelFits(
            "Not enough free memory to start the local model. It needs about 3.0 GB; PaRuX can use about 0.2 GB right now.", selection.Tried);
        Assert.StartsWith("Not enough free memory to start the local model. It needs about", refusal);
        Assert.Contains("Qwen3-0.6B-Q8_0", refusal);
        Assert.Contains("none fits either", refusal);
        // With no candidate at all the refusal is exactly the default's own.
        Assert.Equal("same", CorporateSmallModelFallback.AppendNoSmallModelFits("same", Array.Empty<string>()));
    }

    [Fact]
    public void Attempts_are_bounded_and_cancellation_stops_the_choice()
    {
        var asked = new List<string>();
        var many = Enumerable.Range(0, 10).Select(i => "m" + i + ".gguf").ToArray();
        var none = CorporateSmallModelFallback.Choose(many, p => { asked.Add(p); return false; }, CancellationToken.None);
        Assert.Null(none.ModelPath);
        Assert.Equal(CorporateSmallModelFallback.MaxAttempts, asked.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() =>
            CorporateSmallModelFallback.Choose(many, _ => { calls++; return true; }, cts.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Async_choice_has_the_same_bound_and_order()
    {
        var asked = new List<string>();
        var sel = await CorporateSmallModelFallback.ChooseAsync(
            new[] { "a.gguf", "b.gguf", "c.gguf", "d.gguf" },
            p => { asked.Add(p); return Task.FromResult(p == "c.gguf"); }, CancellationToken.None);
        Assert.Equal("c.gguf", sel.ModelPath);
        Assert.Equal(new[] { "a.gguf", "b.gguf", "c.gguf" }, asked);
        var none = await CorporateSmallModelFallback.ChooseAsync(
            new[] { "a.gguf", "b.gguf", "c.gguf", "d.gguf" }, _ => Task.FromResult(false), CancellationToken.None);
        Assert.Null(none.ModelPath);
        Assert.Equal(3, none.Tried.Count);
    }

    [Fact]
    public void A_probe_failure_on_one_candidate_skips_it_without_launching_anything()
    {
        var sel = CorporateSmallModelFallback.Choose(new[] { "bad.gguf", "ok.gguf" },
            p => p == "bad.gguf" ? throw new InvalidDataException("unreadable") : true, CancellationToken.None);
        Assert.Equal("ok.gguf", sel.ModelPath);
    }

    // ---- explicit requests are never replaced -------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]    // a caller named a model
    [InlineData(true, false, true, false)]    // not Corporate
    [InlineData(true, true, false, false)]    // the model is a saved Chat assignment or a user's own file, not the package default
    public void Only_an_unrequested_package_default_is_eligible(bool noModelNamed, bool corporate, bool unassignedDefault, bool expected) =>
        Assert.Equal(expected, CorporateSmallModelFallback.Eligible(noModelNamed, corporate, unassignedDefault));

    [Fact]
    public void Wiring_substitutes_only_for_unnamed_requests_and_never_changes_saved_settings()
    {
        string process = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        string manager = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        // The only caller that opts in is the one with no model named; every other caller keeps the default of false.
        Assert.Contains("allowSmallFallback: modelPathOverride == null", process, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(process, "allowSmallFallback: "));
        // The role path opts in only for the unassigned package default and never when persisting an assignment.
        Assert.Contains("CorporateSmallModelFallback.IsUnassignedPackageDefault(path)", manager, StringComparison.Ordinal);
        Assert.Contains("allowSmallFallback && normalizedRole == \"standard\" && !persistRoleAssignment", manager, StringComparison.Ordinal);
        // A fallback model is launched through the same planner: the process manager re-plans every alternate before any launch.
        Assert.Contains("CorporateCpuMemoryPlanner.ForModel(alt, ram, altCeiling)", process, StringComparison.Ordinal);
        Assert.Contains("CorporateSmallModelFallback.Choose(", process, StringComparison.Ordinal);
        // Neither fallback code path writes a saved role/profile.
        string helper = Read("src", "PaRuX.Server", "CorporateSmallModelFallback.cs");
        Assert.DoesNotContain("SetRoleOverride", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRoleOverride", SliceBetween(process, "bool smallFallbackEligible", "string? portRefusal"), StringComparison.Ordinal);
    }

    [Fact]
    public void Fallback_code_makes_no_network_or_process_calls()
    {
        string helper = Read("src", "PaRuX.Server", "CorporateSmallModelFallback.cs");
        foreach (string forbidden in new[] { "HttpClient", "System.Net", "Socket", "Process.Start", "WebRequest", "Download" })
            Assert.DoesNotContain(forbidden, helper, StringComparison.Ordinal);
    }

    // ---- remembered choice --------------------------------------------------------------------------------------------

    [Fact]
    public void Remembered_choice_survives_until_free_memory_grows_significantly()
    {
        var memory = new CorporateSmallModelFallback.Memory();
        memory.Remember("4b.gguf", "06b.gguf", availableMib: 1800);
        var entry = memory.Get("4b.gguf")!;
        Assert.Null(memory.Get("another-default.gguf"));

        Assert.True(memory.ShouldKeepFallback(entry, 1800, defaultFitsNow: false));      // nothing changed
        Assert.True(memory.ShouldKeepFallback(entry, 2500, defaultFitsNow: true));       // grew only 700 MiB: not significant, even if it would fit
        Assert.False(memory.ShouldKeepFallback(entry, 3000, defaultFitsNow: true));      // grew >= 1 GiB and the default fits: look again
        Assert.True(memory.ShouldKeepFallback(entry, 3000, defaultFitsNow: false));      // grew but the default still does not fit: keep, no reload
        Assert.Equal(3000, memory.Get("4b.gguf")!.AvailableMib);                          // baseline moved up
        Assert.True(memory.ShouldKeepFallback(memory.Get("4b.gguf")!, 3500, defaultFitsNow: true)); // only 500 MiB beyond the new baseline

        memory.Forget();
        Assert.Null(memory.Get("4b.gguf"));
    }

    [Fact]
    public void Repeated_requests_on_a_busy_pc_never_ask_for_a_reload()
    {
        var memory = new CorporateSmallModelFallback.Memory();
        memory.Remember("4b.gguf", "06b.gguf", 1800);
        int reloads = 0;
        foreach (double sampledAvailableMib in new double[] { 1800, 1750, 1900, 1850, 2100, 1800 })
            if (!memory.ShouldKeepFallback(memory.Get("4b.gguf")!, sampledAvailableMib, defaultFitsNow: false)) reloads++;
        Assert.Equal(0, reloads);
    }

    // ---- observable message -------------------------------------------------------------------------------------------

    [Fact]
    public void Notice_is_bilingual_names_both_models_and_is_only_shown_while_the_small_model_serves()
    {
        string def = Path.Combine(_models, "Qwen3-4B-Q4_K_M.gguf");
        string small = Path.Combine(_models, "Qwen3-0.6B-Q8_0.gguf");
        CorporateFallbackNotice notice = CorporateSmallModelFallback.BuildNotice(def, small, 3100, 1800);

        Assert.StartsWith("Memoria insufficiente per Qwen3-4B-Q4_K_M: uso Qwen3-0.6B-Q8_0.", notice.Italian, StringComparison.Ordinal);
        Assert.StartsWith("Not enough memory for Qwen3-4B-Q4_K_M: using Qwen3-0.6B-Q8_0.", notice.English, StringComparison.Ordinal);
        Assert.Contains("3.0 GB", notice.English, StringComparison.Ordinal);
        Assert.Contains(notice.Italian, notice.Combined, StringComparison.Ordinal);
        Assert.Contains(notice.English, notice.Combined, StringComparison.Ordinal);

        Assert.Null(CorporateSmallModelFallback.CurrentFor(small));
        CorporateSmallModelFallback.Publish(notice);
        Assert.Same(notice, CorporateSmallModelFallback.CurrentFor(small));
        Assert.Null(CorporateSmallModelFallback.CurrentFor(def));                // the default model is not "a fallback"
        Assert.Null(CorporateSmallModelFallback.CurrentFor(null));
        CorporateSmallModelFallback.ClearPublished();
        Assert.Null(CorporateSmallModelFallback.CurrentFor(small));
    }

    [Fact]
    public void Status_diagnostics_and_chat_route_event_carry_the_notice()
    {
        string manager = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        string chat = Read("src", "PaRuX.Server", "ChatProxyService.cs");
        string router = Read("src", "PaRuX.Server", "SmartRouterService.cs");
        Assert.Contains("result.CorporateMemoryFallbackNoticeIt = smallModelNotice.Italian", manager, StringComparison.Ordinal);
        Assert.Contains("status.CorporateMemoryFallbackNoticeIt + \" / \" + status.CorporateMemoryFallbackNotice", manager, StringComparison.Ordinal);
        Assert.Contains("CorporateSmallModelFallback.CurrentFor(modelRoute.ModelPath)?.Combined", chat, StringComparison.Ordinal);
        Assert.Contains("RecordCorporateAutoMemoryFallback(corporateAllCandidates, selected, loaded)", router, StringComparison.Ordinal);
    }

    [Fact]
    public void Acceptance_harness_expects_the_small_model_at_low_memory_and_checks_the_notice()
    {
        string harness = Read("tests", "corporate", "measure-corporate-lite.ps1");
        Assert.Contains("Qwen3-0.6B*", harness, StringComparison.Ordinal);
        Assert.Contains("corporateMemoryFallbackNotice", harness, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static int CountOf(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string SliceBetween(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        int b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "source markers moved: " + from);
        return text[a..b];
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln")))
            dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(path).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
