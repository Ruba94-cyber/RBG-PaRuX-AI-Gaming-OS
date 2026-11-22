using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Windows adapters, startup recovery wiring and the streaming API. Everything uses fakes: no real display or Sunshine is touched.</summary>
public sealed class StreamingModeIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-stream-int-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_dir, "journal.json");
    public StreamingModeIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- fakes ----

    private sealed class FakeModeApi : IDisplayModeApi
    {
        public DisplayModeEntry Now = new(1920, 1080, 60, 32, 0, 0);
        public List<DisplayModeEntry> Modes = new()
        {
            new(1920, 1080, 60, 32, 0, 0), new(1920, 1080, 120, 32, 0, 0), new(2560, 1440, 60, 32, 0, 0), new(3840, 2160, 60, 32, 0, 0)
        };
        public bool RejectTest, RejectApply;
        public readonly List<string> Calls = new();
        public Func<DisplayJournalEntry?>? AtApply; // observes the journal at the moment of the change
        public DisplayModeEntry? Current(string displayId) => Now;
        public IReadOnlyList<DisplayModeEntry> Supported(string displayId) => Modes;
        public bool Test(string displayId, DisplayModeEntry mode) { Calls.Add("test"); return !RejectTest; }
        public bool ApplyTemporary(string displayId, DisplayModeEntry mode)
        {
            Calls.Add("apply");
            AtApply?.Invoke();
            if (RejectApply) return false;
            Now = mode; return true;
        }
        public bool ApplyPermanent(string displayId, DisplayModeEntry mode) { Calls.Add("PERMANENT"); return true; }
    }

    private static GamingDisplayTopologyService Topology(params string[] ids) => new(() =>
        ids.Select(id => new GamingDisplayModeDto(id, true, 0, 0, 1920, 1080, 60)).ToArray());

    private sealed class FakeHost : IStreamingHost
    {
        public SunshineProbe Probe = new(true, false);
        public int Starts, Stops;
        public SunshineProbe ProbeSunshine() => Probe;
        public Task<OwnedProcessHandle> StartSunshineAsync(CancellationToken ct) { Starts++; return Task.FromResult(new OwnedProcessHandle(7, DateTime.UtcNow)); }
        public Task<bool> StopOwnedSunshineAsync(OwnedProcessHandle h, CancellationToken ct) { Stops++; return Task.FromResult(true); }
    }

    private sealed class MemoryLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ScriptedController : IStreamingDisplayController
    {
        public Func<DisplayJournalEntry, RestoreOutcome> OnRestore = _ => RestoreOutcome.Restored;
        public string? Read(string displayId, DisplayChangeKind kind) => null;
        public bool Apply(string displayId, DisplayChangeKind kind, string value) => false;
        public RestoreOutcome Restore(DisplayJournalEntry entry) => OnRestore(entry);
    }

    // ---- display controller ----

    [Fact]
    public void Controller_changes_resolution_temporarily_and_restores_the_exact_previous_mode()
    {
        var api = new FakeModeApi();
        var c = new WindowsStreamingDisplayController(api, Topology("D1"));
        string? previous = c.Read("D1", DisplayChangeKind.Resolution);
        Assert.Equal("1920,1080,60,32,0,0", previous);

        Assert.True(c.Apply("D1", DisplayChangeKind.Resolution, "3840x2160"));
        Assert.Equal(3840, api.Now.Width);
        Assert.DoesNotContain("PERMANENT", api.Calls);

        var entry = new DisplayJournalEntry("e", DisplayChangeKind.Resolution, "D1", previous!, "3840x2160", DateTime.UtcNow, "t");
        Assert.Equal(RestoreOutcome.Restored, c.Restore(entry));
        Assert.Equal(new DisplayModeEntry(1920, 1080, 60, 32, 0, 0), api.Now);
        Assert.DoesNotContain("PERMANENT", api.Calls);
    }

    [Fact]
    public void Controller_refresh_rate_keeps_resolution_and_resolution_prefers_current_refresh()
    {
        var api = new FakeModeApi();
        var c = new WindowsStreamingDisplayController(api, Topology("D1"));
        Assert.True(c.Apply("D1", DisplayChangeKind.RefreshRate, "120"));
        Assert.Equal(new DisplayModeEntry(1920, 1080, 120, 32, 0, 0), api.Now);
        Assert.True(c.Apply("D1", DisplayChangeKind.Resolution, "2560x1440"));
        Assert.Equal(2560, api.Now.Width);
    }

    [Theory]
    [InlineData("1234x999")]   // not listed
    [InlineData("abc")]
    [InlineData("-1920x1080")]
    [InlineData("1920x")]
    public void Controller_rejects_unlisted_or_malformed_resolution_without_calling_windows_apply(string value)
    {
        var api = new FakeModeApi();
        var c = new WindowsStreamingDisplayController(api, Topology("D1"));
        Assert.False(c.Apply("D1", DisplayChangeKind.Resolution, value));
        Assert.DoesNotContain("apply", api.Calls);
    }

    [Fact]
    public void Controller_refuses_unknown_monitor_hdr_and_driver_rejection()
    {
        var api = new FakeModeApi();
        var c = new WindowsStreamingDisplayController(api, Topology("D1"));
        Assert.Null(c.Read("OTHER", DisplayChangeKind.Resolution));
        Assert.False(c.Apply("OTHER", DisplayChangeKind.Resolution, "3840x2160"));
        Assert.Null(c.Read("D1", DisplayChangeKind.Hdr));
        Assert.False(c.Apply("D1", DisplayChangeKind.Hdr, "on"));
        Assert.Equal(RestoreOutcome.Failed, c.Restore(new DisplayJournalEntry("h", DisplayChangeKind.Hdr, "D1", "off", "on", DateTime.UtcNow, "t")));
        api.RejectTest = true;
        Assert.False(c.Apply("D1", DisplayChangeKind.Resolution, "3840x2160"));
        Assert.DoesNotContain("apply", api.Calls);
    }

    [Fact]
    public void Controller_restore_outcomes_for_gone_monitor_unreadable_topology_and_corrupt_token()
    {
        var api = new FakeModeApi();
        var entry = new DisplayJournalEntry("e", DisplayChangeKind.Resolution, "D1", "1920,1080,60,32,0,0", "x", DateTime.UtcNow, "t");
        Assert.Equal(RestoreOutcome.NotApplicable, new WindowsStreamingDisplayController(api, Topology("D2")).Restore(entry));
        var unreadable = new GamingDisplayTopologyService(() => throw new InvalidOperationException("driver busy"));
        Assert.Equal(RestoreOutcome.Failed, new WindowsStreamingDisplayController(api, unreadable).Restore(entry));
        Assert.Equal(RestoreOutcome.Failed, new WindowsStreamingDisplayController(api, Topology("D1"))
            .Restore(entry with { PreviousValue = "garbage" }));
        api.Modes.Clear(); // previous mode no longer listed: keep the restore point
        api.Now = new DisplayModeEntry(3840, 2160, 60, 32, 0, 0);
        Assert.Equal(RestoreOutcome.Failed, new WindowsStreamingDisplayController(api, Topology("D1")).Restore(entry));
    }

    [Fact]
    public async Task Service_writes_the_journal_before_the_display_is_changed_and_removes_it_after_restore()
    {
        var api = new FakeModeApi();
        var journal = new DisplayChangeJournal(JournalPath);
        int pendingAtApply = -1;
        api.AtApply = () => { pendingAtApply = journal.Pending(StreamingModeService.JournalOwner).Count; return null; };
        var svc = new StreamingModeService(new FakeHost(), new WindowsStreamingDisplayController(api, Topology("D1")), journal, TimeSpan.FromSeconds(2));

        StreamingStatus started = await svc.StartAsync(new StreamingStartRequest(true, new StreamingDisplayTarget("D1", DisplayChangeKind.Resolution, "3840x2160"), true), default);
        Assert.Equal(StreamingState.Active, started.State);
        Assert.Equal(1, pendingAtApply);

        StreamingStatus stopped = await svc.StopAsync();
        Assert.Equal(StreamingState.Idle, stopped.State);
        Assert.Empty(journal.Pending());
        Assert.Equal(1920, api.Now.Width);
    }

    // ---- host ----

    [Fact]
    public async Task Host_never_stops_a_process_it_did_not_start_and_probe_reports_installed_and_running()
    {
        var host = new WindowsStreamingHost(() => null, () => true);
        Assert.False(await host.StopOwnedSunshineAsync(new OwnedProcessHandle(Environment.ProcessId, DateTime.UtcNow), default));
        Assert.Equal(new SunshineProbe(true, true), host.ProbeSunshine());
        Assert.Equal(new SunshineProbe(false, false), new WindowsStreamingHost(() => null, () => false).ProbeSunshine());
        Assert.Equal(new SunshineProbe(true, false), new WindowsStreamingHost(() => "C:\\x\\sunshine.exe", () => false).ProbeSunshine());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsStreamingHost(() => null, () => false).StartSunshineAsync(default));
    }

    // ---- startup recovery wiring ----

    private DisplayChangeJournal SeedJournal(int entries)
    {
        var journal = new DisplayChangeJournal(JournalPath);
        for (int i = 0; i < entries; i++)
            journal.Begin(new DisplayJournalEntry("id" + i, DisplayChangeKind.Resolution, "D1", "1920,1080,60,32,0,0", "x", DateTime.UtcNow, StreamingModeService.JournalOwner));
        return journal;
    }

    [Fact]
    public async Task Startup_recovery_restores_journaled_changes_and_logs()
    {
        DisplayChangeJournal journal = SeedJournal(2);
        int restored = 0;
        var svc = new StreamingModeService(new FakeHost(), new ScriptedController { OnRestore = _ => { restored++; return RestoreOutcome.Restored; } }, journal, TimeSpan.FromSeconds(1));
        var log = new MemoryLogger();
        Assert.Equal(StreamingRecoveryOutcome.Clean, await StreamingStartupRecovery.RunAsync(svc, log, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, restored);
        Assert.Empty(journal.Pending());
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("restored 2"));
    }

    [Fact]
    public async Task Startup_recovery_reports_incomplete_when_restore_fails_and_keeps_the_journal()
    {
        DisplayChangeJournal journal = SeedJournal(1);
        var svc = new StreamingModeService(new FakeHost(), new ScriptedController { OnRestore = _ => RestoreOutcome.Failed }, journal, TimeSpan.FromSeconds(1));
        Assert.Equal(StreamingRecoveryOutcome.Incomplete, await StreamingStartupRecovery.RunAsync(svc, new MemoryLogger(), TimeSpan.FromSeconds(5)));
        Assert.Single(journal.Pending());
    }

    [Fact]
    public async Task Startup_recovery_is_bounded_and_never_throws()
    {
        DisplayChangeJournal journal = SeedJournal(1);
        using var release = new ManualResetEventSlim();
        var hang = new StreamingModeService(new FakeHost(), new ScriptedController { OnRestore = _ => { release.Wait(5000); return RestoreOutcome.Restored; } }, journal, TimeSpan.FromSeconds(1));
        var log = new MemoryLogger();
        Assert.Equal(StreamingRecoveryOutcome.TimedOut, await StreamingStartupRecovery.RunAsync(hang, log, TimeSpan.FromMilliseconds(150)));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("did not finish"));
        release.Set();

        var boom = new StreamingModeService(new FakeHost(), new ScriptedController { OnRestore = _ => throw new NotSupportedException("x") }, SeedJournal(1), TimeSpan.FromSeconds(1));
        Assert.Equal(StreamingRecoveryOutcome.Failed, await StreamingStartupRecovery.RunAsync(boom, new MemoryLogger(), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Startup_recovery_with_an_empty_journal_is_clean_and_logged_as_information()
    {
        var svc = new StreamingModeService(new FakeHost(), new ScriptedController(), new DisplayChangeJournal(JournalPath), TimeSpan.FromSeconds(1));
        var log = new MemoryLogger();
        Assert.Equal(StreamingRecoveryOutcome.Clean, await StreamingStartupRecovery.RunAsync(svc, log, TimeSpan.FromSeconds(5)));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Information);
    }

    [Fact]
    public void Program_composes_the_streaming_services_and_recovers_once_at_startup_outside_testing()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PaRuX.Server", "Program.cs"));
        Assert.Contains("AddSingleton<IStreamingHost, WindowsStreamingHost>", src, StringComparison.Ordinal);
        Assert.Contains("new StreamingModeService(", src, StringComparison.Ordinal);
        int testingGuard = src.IndexOf("if (!app.Environment.IsEnvironment(\"Testing\"))", StringComparison.Ordinal);
        int recovery = src.IndexOf("StreamingStartupRecovery.RunAsync(", StringComparison.Ordinal);
        Assert.True(testingGuard >= 0 && recovery > testingGuard, "Streaming recovery must sit inside the non-Testing startup block.");
        Assert.Equal(recovery, src.LastIndexOf("StreamingStartupRecovery.RunAsync(", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "src", "PaRuX.Server")) && Directory.Exists(Path.Combine(dir, "tests"))) return dir;
        throw new DirectoryNotFoundException("repository root");
    }
}

/// <summary>Streaming API contract: auth, consent flags, Gaming hard-off, status shape. Uses a test server with fakes.</summary>
public sealed class StreamingEndpointsContractTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private StreamingModeService _service = null!;
    private int _starts;
    private bool _gaming, _displayPending;
    private string _dir = Path.Combine(Path.GetTempPath(), "parux-stream-api-" + Guid.NewGuid().ToString("N"));

    private sealed class Host : IStreamingHost
    {
        public Action? OnStart;
        public SunshineProbe ProbeSunshine() => new(true, false);
        public Task<OwnedProcessHandle> StartSunshineAsync(CancellationToken ct) { OnStart?.Invoke(); return Task.FromResult(new OwnedProcessHandle(9, DateTime.UtcNow)); }
        public Task<bool> StopOwnedSunshineAsync(OwnedProcessHandle h, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class Display : IStreamingDisplayController
    {
        public string Value = "1920,1080,60,32,0,0";
        public string? Read(string id, DisplayChangeKind k) => Value;
        public bool Apply(string id, DisplayChangeKind k, string v) { Value = v; return true; }
        public RestoreOutcome Restore(DisplayJournalEntry e) { Value = e.PreviousValue; return RestoreOutcome.Restored; }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _service = new StreamingModeService(new Host { OnStart = () => _starts++ }, new Display(),
            new DisplayChangeJournal(Path.Combine(_dir, "j.json")), TimeSpan.FromSeconds(2));
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_service);
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Items["PaRuXOwner"] = context.Request.Headers["X-Owner"].ToString();
            await next();
        });
        StreamingEndpoints.Map(_app, () => _gaming, () => _displayPending);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string owner, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Owner", owner);
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("device:phone")]
    [InlineData("web:session")]
    [InlineData("")]
    public async Task Non_desktop_principals_get_403_on_every_streaming_route(string owner)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, StreamingEndpoints.StatusRoute, owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, owner,
            new { allowStartSunshine = true, allowDisplayChange = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, StreamingEndpoints.StopRoute, owner)).StatusCode);
        Assert.Equal(0, _starts);
        Assert.Equal(StreamingState.Idle, _service.Status().State);
    }

    [Fact]
    public async Task Status_reports_idle_with_the_documented_shape()
    {
        HttpResponseMessage response = await Send(HttpMethod.Get, StreamingEndpoints.StatusRoute, "desktop");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<StreamingStatusDto>();
        Assert.Equal("Idle", dto!.State);
        Assert.False(dto.SunshineOwnedByPaRuX);
        Assert.False(dto.DisplayRestorePending);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"allowStartSunshine\":true}")]
    [InlineData("{\"allowDisplayChange\":true}")]
    public async Task Missing_consent_flags_are_a_400_and_change_nothing(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, StreamingEndpoints.StartRoute)
        { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Owner", "desktop");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
        Assert.Equal(0, _starts);
        Assert.Equal(StreamingState.Idle, _service.Status().State);
    }

    [Theory]
    [InlineData("Hdr", "on")]
    [InlineData("Bogus", "x")]
    [InlineData("1", "x")]
    public async Task Hdr_and_unknown_display_kinds_are_rejected(string kind, string value)
    {
        HttpResponseMessage r = await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, "desktop", new
        { allowStartSunshine = true, allowDisplayChange = true, display = new { displayId = "D1", kind, value } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task A_display_target_without_display_consent_is_a_400()
    {
        HttpResponseMessage r = await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, "desktop", new
        { allowStartSunshine = true, allowDisplayChange = false, display = new { displayId = "D1", kind = "Resolution", value = "3840x2160" } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task Start_is_refused_with_409_while_gaming_or_while_a_display_change_is_pending()
    {
        object body = new { allowStartSunshine = true, allowDisplayChange = false };
        _gaming = true;
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, "desktop", body)).StatusCode);
        _gaming = false; _displayPending = true;
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, "desktop", body)).StatusCode);
        Assert.Equal(0, _starts);
        // Stop stays available while Gaming: it only undoes what PaRuX did.
        _gaming = true;
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, StreamingEndpoints.StopRoute, "desktop")).StatusCode);
    }

    [Fact]
    public async Task Start_with_consent_activates_then_stop_restores_and_a_second_start_is_a_409()
    {
        object body = new { allowStartSunshine = true, allowDisplayChange = true,
            display = new { displayId = "D1", kind = "Resolution", value = "3840x2160" } };
        HttpResponseMessage started = await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, "desktop", body);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var dto = await started.Content.ReadFromJsonAsync<StreamingStatusDto>();
        Assert.Equal("Active", dto!.State);
        Assert.True(dto.SunshineOwnedByPaRuX);
        Assert.True(dto.DisplayRestorePending);
        Assert.Equal(1, _starts);

        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, "desktop", body)).StatusCode);

        HttpResponseMessage stopped = await Send(HttpMethod.Post, StreamingEndpoints.StopRoute, "desktop");
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        var after = await stopped.Content.ReadFromJsonAsync<StreamingStatusDto>();
        Assert.Equal("Idle", after!.State);
        Assert.False(after.DisplayRestorePending);
    }

    [Fact]
    public async Task Sunshine_not_running_without_consent_fails_honestly_with_409_and_starts_nothing()
    {
        // The fake host reports Sunshine installed and not running; without consent PaRuX must not start it.
        HttpResponseMessage r = await Send(HttpMethod.Post, StreamingEndpoints.StartRoute, "desktop",
            new { allowStartSunshine = false, allowDisplayChange = false });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var dto = await r.Content.ReadFromJsonAsync<StreamingStatusDto>();
        Assert.Equal("Failed", dto!.State);
        Assert.Equal(0, _starts);
    }
}

public sealed class StreamingEndpointsSecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public StreamingEndpointsSecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET", "/api/v1/gaming/streaming")]
    [InlineData("POST", "/api/v1/gaming/streaming/start")]
    [InlineData("POST", "/api/v1/gaming/streaming/stop")]
    public async Task Streaming_routes_require_authentication_in_the_real_app(string method, string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(new { allowStartSunshine = false, allowDisplayChange = false });
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
