using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Tunnel;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RemoteTunnelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-tunnel-tests-" + Guid.NewGuid().ToString("N"));
    public RemoteTunnelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- URL parsing -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-28T10:00:00Z INF |  https://bags-lewis-sustained-fraction.trycloudflare.com                          |", "https://bags-lewis-sustained-fraction.trycloudflare.com")]
    [InlineData("INF Your quick Tunnel has been created! Visit it at (it may take some time to be reachable): https://Quiet-Fox-9.trycloudflare.com", "https://quiet-fox-9.trycloudflare.com")]
    [InlineData("see https://api.trycloudflare.com/tunnel then https://real-one.trycloudflare.com/x", "https://real-one.trycloudflare.com")]
    public void Quick_tunnel_url_is_parsed(string line, string expected)
    {
        Assert.True(TunnelOutputParser.TryParseQuickTunnelUrl(line, out string url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Requesting new quick Tunnel on trycloudflare.com...")]
    [InlineData("https://api.trycloudflare.com")]
    [InlineData("https://evil.example.com/?x=https://a.trycloudflare.com.evil.io")]
    [InlineData("http://plain-http.trycloudflare.com")]
    public void Non_tunnel_lines_are_not_urls(string line) =>
        Assert.False(TunnelOutputParser.TryParseQuickTunnelUrl(line, out _));

    [Fact]
    public void Output_is_classified_and_secrets_are_redacted()
    {
        Assert.Equal(TunnelOutputSignal.Connected, TunnelOutputParser.Classify("INF Registered tunnel connection connIndex=0"));
        Assert.Equal(TunnelOutputSignal.NetworkFailure, TunnelOutputParser.Classify("ERR failed to request quick Tunnel: Post \"https://api.trycloudflare.com/tunnel\": dial tcp: i/o timeout"));
        Assert.Equal(TunnelOutputSignal.TokenRejected, TunnelOutputParser.Classify("ERR Unauthorized: Invalid tunnel secret"));
        string redacted = TunnelOutputParser.Redact("token eyJhIjoiMTIzNDU2Nzg5MDEyMzQ1Njc4OTAxMjM0NTY3ODkwIiwidCI6IjEyMyJ9 end");
        Assert.DoesNotContain("eyJhIjoi", redacted);
    }

    [Theory]
    [InlineData("parux.example.com", "https://parux.example.com")]
    [InlineData("https://Parux.Example.com/", "https://parux.example.com")]
    public void Named_public_url_is_normalized(string input, string expected)
    {
        Assert.True(TunnelOutputParser.TryNormalizePublicUrl(input, out string url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("http://parux.example.com")]
    [InlineData("https://user:pw@parux.example.com")]
    [InlineData("https://192.168.1.4")]
    [InlineData("localhost")]
    public void Unsafe_named_public_url_is_refused(string input) =>
        Assert.False(TunnelOutputParser.TryNormalizePublicUrl(input, out _));

    // ---- hash pin --------------------------------------------------------------------------------------------------

    [Fact]
    public void Pin_matches_the_reviewed_lock_file_and_is_well_formed()
    {
        string root = AppContext.BaseDirectory;
        string? lockPath = null;
        for (DirectoryInfo? d = new(root); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "packaging", "cloudflared.lock.json");
            if (File.Exists(candidate)) { lockPath = candidate; break; }
        }
        Assert.NotNull(lockPath);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(lockPath!));
        JsonElement e = doc.RootElement;
        Assert.Equal(CloudflaredPin.Version, e.GetProperty("version").GetString());
        Assert.Equal(CloudflaredPin.Url, e.GetProperty("url").GetString());
        Assert.Equal(CloudflaredPin.Sha256, e.GetProperty("sha256").GetString());
        Assert.Equal(CloudflaredPin.SizeBytes, e.GetProperty("sizeBytes").GetInt64());
        Assert.True(VerifiedDownload.IsValidSha256(CloudflaredPin.Sha256));
        Assert.StartsWith("https://github.com/cloudflare/cloudflared/releases/download/", CloudflaredPin.Url);
        Assert.Contains("/" + CloudflaredPin.Version + "/", CloudflaredPin.Url);
    }

    [Fact]
    public void Managed_file_that_does_not_match_the_pin_is_refused_before_launch()
    {
        string managedRoot = Path.Combine(_dir, "components");
        var component = new CloudflaredComponent(managedRoot, () => false, () => null, () => false, (_, _, _, _, _) => Task.CompletedTask);
        Directory.CreateDirectory(Path.GetDirectoryName(component.ManagedPath)!);
        File.WriteAllBytes(component.ManagedPath, RandomNumberGenerator.GetBytes(1024));

        Assert.Equal(ComponentOwnership.PARUX_MANAGED, component.Detect().Ownership);
        Assert.Null(component.ResolveForLaunch(out string failure));
        Assert.Contains("pinned SHA-256", failure);
    }

    [Fact]
    public void User_managed_copy_is_used_as_is_and_never_hashed_or_replaced()
    {
        string own = Path.Combine(_dir, "mine", "cloudflared.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        byte[] bytes = RandomNumberGenerator.GetBytes(64);
        File.WriteAllBytes(own, bytes);
        var component = new CloudflaredComponent(Path.Combine(_dir, "components"), () => false, () => own, () => false,
            (_, _, _, _, _) => throw new InvalidOperationException("must not download"));

        Assert.Equal(ComponentOwnership.USER_MANAGED, component.Detect().Ownership);
        Assert.Equal(own, component.ResolveForLaunch(out _));
        Assert.Equal(bytes, File.ReadAllBytes(own));
    }

    [Fact]
    public async Task Install_is_refused_in_corporate_mode_and_downloads_nothing()
    {
        bool downloaded = false;
        var component = new CloudflaredComponent(Path.Combine(_dir, "c"), () => true, () => null, () => false,
            (_, _, _, _, _) => { downloaded = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => component.InstallAsync(CancellationToken.None));
        Assert.False(downloaded);
    }

    [Fact]
    public async Task Verified_download_rejects_bytes_that_do_not_match_the_pin()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(2048);
        using var http = new HttpClient(new FixedHandler(payload));
        string destination = Path.Combine(_dir, "dl", "cloudflared.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VerifiedDownload.DownloadAsync(http, CloudflaredPin.Url, destination, CloudflaredPin.Sha256, payload.Length, CancellationToken.None));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".partial"));

        string good = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await VerifiedDownload.DownloadAsync(http, CloudflaredPin.Url, destination, good, payload.Length, CancellationToken.None);
        Assert.True(File.Exists(destination));
    }

    private sealed class FixedHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }

    // ---- endpoint policy -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/v1/remote/status")]
    [InlineData("POST", "/api/v1/remote/config")]
    [InlineData("POST", "/api/v1/remote/pair/start")]
    [InlineData("DELETE", "/api/v1/remote/devices")]
    [InlineData("DELETE", "/api/v1/remote/devices/abc")]
    [InlineData("GET", "/api/v1/remote/tunnel/status")]
    [InlineData("POST", "/api/v1/remote/tunnel/config")]
    [InlineData("POST", "/api/v1/remote/tunnel/pending/abc/approve")]
    [InlineData("GET", "/api/v1/web/session")]
    [InlineData("POST", "/api/v1/web/login")]
    [InlineData("GET", "/app")]
    [InlineData("GET", "/api/v1/apikeys")]
    [InlineData("POST", "/api/v1/apikeys")]
    [InlineData("POST", "/v1/chat/completions")]
    [InlineData("GET", "/api/tags")]
    [InlineData("GET", "/api/v1/runtime/status")]
    [InlineData("POST", "/api/v1/runtime/install")]
    [InlineData("GET", "/api/v1/gateway/status")]
    [InlineData("PUT", "/api/v1/gateway/settings")]
    [InlineData("POST", "/api/v1/backup/export")]
    [InlineData("POST", "/api/v1/system/shutdown")]
    [InlineData("GET", "/api/v1/pair/poll")]
    public void A_tunnel_request_cannot_reach_owner_routes(string method, string path) =>
        Assert.False(TunnelEndpointPolicy.IsAllowed(method, new PathString(path)));

    [Theory]
    [InlineData("GET", "/api/v1/health")]
    [InlineData("GET", "/api/v1/conversations")]
    [InlineData("POST", "/api/v1/chat/completions")]
    [InlineData("POST", "/api/v1/remote/pair/complete")]
    [InlineData("POST", "/api/v1/remote/pair/poll")]
    [InlineData("GET", "/api/v2/mobile/runtime")]
    public void A_tunnel_request_reaches_the_paired_device_api_and_pairing(string method, string path) =>
        Assert.True(TunnelEndpointPolicy.IsAllowed(method, new PathString(path)));

    // ---- boundary middleware ---------------------------------------------------------------------------------------

    private const int Port = 51999;

    private (RemoteTunnelService Tunnel, RemoteAccessService Remote, FakeLauncher Launcher) Online(bool corporate = false, bool gaming = false)
    {
        var launcher = new FakeLauncher();
        launcher.OnStart = lines =>
        {
            lines("INF |  https://quiet-fox-9.trycloudflare.com  |");
            lines("INF Registered tunnel connection connIndex=0");
        };
        var remote = new RemoteAccessService(Path.Combine(_dir, "remote-access.json"));
        RemoteTunnelService tunnel = Create(launcher, () => corporate, () => gaming);
        tunnel.Broker = new TunnelPairingBroker(remote, () => tunnel.PublicUrl);
        return (tunnel, remote, launcher);
    }

    private RemoteTunnelService Create(FakeLauncher launcher, Func<bool> corporate, Func<bool> gaming, Action<CloudflaredComponent>? _ = null)
    {
        string exe = Path.Combine(_dir, "mine", "cloudflared.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, new byte[] { 1 });
        var component = new CloudflaredComponent(Path.Combine(_dir, "c"), corporate, () => exe, () => false, (_, _, _, _, _) => Task.CompletedTask);
        var settings = new TunnelSettingsStore(_dir, new PlainProtector());
        return new RemoteTunnelService(settings, component, launcher, corporate, gaming, () => Port, autoStart: false,
            delay: (_, _) => Task.CompletedTask, startTimeout: TimeSpan.FromSeconds(1), stableAfter: TimeSpan.FromHours(1));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 1200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "condition not reached");
    }

    private static DefaultHttpContext Context(string method, string path, int localPort = Port, string host = "quiet-fox-9.trycloudflare.com")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Headers.Host = host;
        ctx.Connection.LocalPort = localPort;
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int Status, bool Reached, HttpContext Ctx)> Send(RemoteTunnelService t, RemoteAccessService r, HttpContext ctx)
    {
        bool reached = false;
        var mw = new TunnelBoundaryMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(ctx, t, r);
        return (ctx.Response.StatusCode, reached, ctx);
    }

    [Fact]
    public async Task Requests_on_other_ports_are_untouched()
    {
        var (t, r, _) = Online();
        var result = await Send(t, r, Context("GET", "/api/v1/remote/status", localPort: 51123));
        Assert.True(result.Reached);
        Assert.False(TunnelBoundaryMiddleware.IsTunnel(result.Ctx));
    }

    [Fact]
    public async Task Tunnel_requests_are_refused_while_the_tunnel_is_off()
    {
        var (t, r, _) = Online();
        var result = await Send(t, r, Context("GET", "/api/v1/health"));
        Assert.Equal(503, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task Owner_routes_are_refused_through_the_tunnel_even_with_a_valid_device_token_and_spoofed_headers()
    {
        var (t, r, _) = Online();
        await GoOnline(t);
        PairingCompleteResponse device = PairDirectly(r, t);

        foreach ((string method, string path) in new[]
        {
            ("GET", "/api/v1/remote/status"), ("POST", "/api/v1/remote/config"), ("POST", "/api/v1/remote/pair/start"),
            ("DELETE", "/api/v1/remote/devices"), ("GET", "/app"), ("GET", "/api/v1/apikeys"), ("POST", "/api/v1/remote/tunnel/config")
        })
        {
            HttpContext ctx = Context(method, path);
            ctx.Request.Headers[PaRuXHeaders.DeviceToken] = device.DeviceToken;
            ctx.Request.Headers["Cf-Connecting-Ip"] = "127.0.0.1";
            ctx.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
            ctx.Request.Headers.Authorization = "Bearer desktop";
            var result = await Send(t, r, ctx);
            Assert.Equal(403, result.Status);
            Assert.False(result.Reached);
            Assert.Null(ctx.Items["PaRuXOwner"]);
        }
    }

    [Fact]
    public async Task A_paired_route_works_with_a_valid_token_and_is_stamped_as_a_device_never_desktop()
    {
        var (t, r, _) = Online();
        await GoOnline(t);
        PairingCompleteResponse device = PairDirectly(r, t);

        HttpContext ctx = Context("GET", "/api/v1/conversations");
        ctx.Request.Headers[PaRuXHeaders.DeviceToken] = device.DeviceToken;
        var ok = await Send(t, r, ctx);
        Assert.True(ok.Reached);
        Assert.Equal("device:" + device.DeviceId, ctx.Items["PaRuXOwner"]);

        var noToken = await Send(t, r, Context("GET", "/api/v1/conversations"));
        Assert.Equal(401, noToken.Status);
        Assert.False(noToken.Reached);
    }

    [Fact]
    public async Task Wrong_host_and_browser_origin_are_refused()
    {
        var (t, r, _) = Online();
        await GoOnline(t);
        Assert.Equal(403, (await Send(t, r, Context("GET", "/api/v1/health", host: "localhost"))).Status);
        Assert.Equal(403, (await Send(t, r, Context("GET", "/api/v1/health", host: "other.trycloudflare.com"))).Status);
        HttpContext withOrigin = Context("GET", "/api/v1/health");
        withOrigin.Request.Headers.Origin = "https://evil.example";
        Assert.Equal(403, (await Send(t, r, withOrigin)).Status);
    }

    [Fact]
    public async Task Tunnel_requests_are_never_treated_as_loopback_by_the_marker()
    {
        var (t, r, _) = Online();
        await GoOnline(t);
        var result = await Send(t, r, Context("POST", "/api/v1/remote/pair/poll"));
        Assert.True(TunnelBoundaryMiddleware.IsTunnel(result.Ctx));
        Assert.True(result.Reached);
    }

    private static async Task GoOnline(RemoteTunnelService t)
    {
        RemoteTunnelStatus status = await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        Assert.NotEqual(RemoteTunnelState.Error, status.State);
        await WaitFor(() => t.State == RemoteTunnelState.Online);
    }

    private static PairingCompleteResponse PairDirectly(RemoteAccessService remote, RemoteTunnelService tunnel) =>
        remote.IssueDevice("Test phone", tunnel.PublicUrl);

    // ---- pairing over the tunnel -----------------------------------------------------------------------------------

    [Fact]
    public async Task New_device_over_the_tunnel_needs_the_owner_to_approve_and_gets_its_token_once()
    {
        var (t, r, _) = Online();
        await GoOnline(t);
        string code = r.StartPairing(t.PublicUrl).Code;

        PairingApprovalPending pending = t.Broker.RequestApproval(new PairingCompleteRequest { Code = code, DeviceName = "Pixel" });
        Assert.Equal(TunnelPollOutcome.Pending, t.Broker.Poll(new PairingPollRequest { RequestId = pending.RequestId, PollSecret = pending.PollSecret }, out _));
        Assert.Single(t.Broker.ListPending());
        Assert.Empty(r.GetStatus().Devices);

        Assert.True(t.Broker.Approve(pending.RequestId));
        Assert.Equal(TunnelPollOutcome.Approved, t.Broker.Poll(new PairingPollRequest { RequestId = pending.RequestId, PollSecret = pending.PollSecret }, out PairingCompleteResponse? done));
        Assert.Equal("https://quiet-fox-9.trycloudflare.com", done!.TunnelEndpoint);
        Assert.True(r.TryValidateDeviceToken(done.DeviceToken, out _));
        Assert.Equal(TunnelPollOutcome.Unknown, t.Broker.Poll(new PairingPollRequest { RequestId = pending.RequestId, PollSecret = pending.PollSecret }, out _));
        // The code was single use.
        Assert.Throws<UnauthorizedAccessException>(() => t.Broker.RequestApproval(new PairingCompleteRequest { Code = code, DeviceName = "Again" }));
    }

    [Fact]
    public async Task Denied_and_wrong_secret_never_yield_a_token()
    {
        var (t, r, _) = Online();
        await GoOnline(t);
        PairingApprovalPending pending = t.Broker.RequestApproval(new PairingCompleteRequest { Code = r.StartPairing(t.PublicUrl).Code, DeviceName = "Pixel" });
        Assert.Equal(TunnelPollOutcome.Unknown, t.Broker.Poll(new PairingPollRequest { RequestId = pending.RequestId, PollSecret = "nope" }, out PairingCompleteResponse? leaked));
        Assert.Null(leaked);
        Assert.True(t.Broker.Deny(pending.RequestId));
        Assert.Equal(TunnelPollOutcome.Denied, t.Broker.Poll(new PairingPollRequest { RequestId = pending.RequestId, PollSecret = pending.PollSecret }, out _));
        Assert.Empty(r.GetStatus().Devices);
    }

    [Fact]
    public async Task Guessing_pairing_codes_through_the_tunnel_locks_out_even_the_right_code()
    {
        var (t, r, _) = Online();
        await GoOnline(t);
        string good = r.StartPairing(t.PublicUrl).Code;
        for (int i = 0; i < TunnelPairingGate.FailuresBeforeLockout - 1; i++)
            Assert.Throws<UnauthorizedAccessException>(() => t.Broker.RequestApproval(new PairingCompleteRequest { Code = "0000000" + i, DeviceName = "x" }));
        Assert.Throws<RemoteAccessService.PairingLockedException>(() => t.Broker.RequestApproval(new PairingCompleteRequest { Code = "00000009", DeviceName = "x" }));
        Assert.Throws<RemoteAccessService.PairingLockedException>(() => t.Broker.RequestApproval(new PairingCompleteRequest { Code = good, DeviceName = "x" }));
    }

    [Fact]
    public void Pairing_gate_backs_off_exponentially_and_forgets()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gate = new TunnelPairingGate(() => now);
        TimeSpan? first = null, second = null;
        for (int i = 0; i < 5; i++) first = gate.RecordFailure();
        Assert.Equal(TimeSpan.FromMinutes(2), first);
        Assert.False(gate.TryEnter(out TimeSpan wait));
        Assert.True(wait > TimeSpan.Zero);
        now += TimeSpan.FromMinutes(3);
        Assert.True(gate.TryEnter(out _));
        for (int i = 0; i < 5; i++) second = gate.RecordFailure();
        Assert.Equal(TimeSpan.FromMinutes(4), second);
        now += TimeSpan.FromHours(2);
        for (int i = 0; i < 5; i++) second = gate.RecordFailure();
        Assert.Equal(TimeSpan.FromMinutes(2), second);
    }

    // ---- state machine ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Consent_is_required_and_nothing_starts_without_it()
    {
        var (t, _, launcher) = Online();
        RemoteTunnelStatus status = await t.ConfigureAsync(new RemoteTunnelConfigRequest { Enabled = true }, CancellationToken.None);
        Assert.Equal(RemoteTunnelState.Off, status.State);
        Assert.Equal(RemoteTunnelReasons.ConsentRequired, status.ReasonCode);
        Assert.Equal(RemoteTunnelActions.Consent, status.NextAction);
        Assert.Equal(0, launcher.Started);
    }

    [Fact]
    public async Task Off_to_online_to_off_and_the_process_is_killed()
    {
        var (t, _, launcher) = Online();
        Assert.Equal(RemoteTunnelState.Off, t.GetStatus().State);
        await GoOnline(t);
        Assert.Equal("https://quiet-fox-9.trycloudflare.com", t.GetStatus().PublicUrl);

        RemoteTunnelStatus off = await t.ConfigureAsync(new RemoteTunnelConfigRequest { Enabled = false }, CancellationToken.None);
        Assert.Equal(RemoteTunnelState.Off, off.State);
        Assert.Equal(string.Empty, off.PublicUrl);
        Assert.All(launcher.Processes, p => Assert.True(p.Killed));
        Assert.Equal(string.Empty, t.PublicUrl);
    }

    [Fact]
    public async Task Failing_start_is_retried_a_bounded_number_of_times_then_reports_an_error()
    {
        var (t, _, launcher) = Online();
        launcher.OnStart = lines => lines("ERR failed to request quick Tunnel: dial tcp: i/o timeout");
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        await WaitFor(() => t.State == RemoteTunnelState.Error);
        RemoteTunnelStatus status = t.GetStatus();
        Assert.Equal(RemoteTunnelReasons.NetworkUnreachable, status.ReasonCode);
        Assert.Equal(RemoteTunnelActions.CheckNetwork, status.NextAction);
        Assert.Equal(RemoteTunnelService.MaxAttempts, launcher.Started);
        Assert.All(launcher.Processes, p => Assert.True(p.Killed));
    }

    [Fact]
    public async Task A_process_that_exits_immediately_is_an_error_not_a_fake_online()
    {
        var (t, _, launcher) = Online();
        launcher.OnStart = _ => { };
        launcher.ExitImmediately = true;
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        await WaitFor(() => t.State == RemoteTunnelState.Error);
        Assert.Equal(RemoteTunnelReasons.ProcessExited, t.GetStatus().ReasonCode);
        Assert.Equal(string.Empty, t.PublicUrl);
    }

    [Fact]
    public async Task No_address_in_time_is_a_timeout_error()
    {
        var (t, _, launcher) = Online();
        launcher.OnStart = _ => { };
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        await WaitFor(() => t.State == RemoteTunnelState.Error);
        Assert.Equal(RemoteTunnelReasons.UrlTimeout, t.GetStatus().ReasonCode);
    }

    [Fact]
    public async Task Stopping_while_starting_cancels_and_kills()
    {
        var (t, _, launcher) = Online();
        launcher.OnStart = _ => { };
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        await WaitFor(() => launcher.Started == 1);
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { Enabled = false }, CancellationToken.None);
        Assert.Equal(RemoteTunnelState.Off, t.State);
        Assert.All(launcher.Processes, p => Assert.True(p.Killed));
    }

    [Fact]
    public async Task Corporate_mode_never_allows_the_tunnel()
    {
        var (t, _, launcher) = Online(corporate: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None));
        Assert.Equal(0, launcher.Started);
        RemoteTunnelStatus status = t.GetStatus();
        Assert.False(status.Allowed);
        Assert.Equal(RemoteTunnelReasons.CorporateBlocked, status.ReasonCode);
        Assert.False(t.AcceptsTraffic);
    }

    [Fact]
    public async Task Gaming_mode_blocks_start_and_hard_off_stops_and_disables()
    {
        bool gaming = false;
        var launcher = new FakeLauncher
        {
            OnStart = lines => { lines("https://quiet-fox-9.trycloudflare.com"); lines("Registered tunnel connection"); }
        };
        var remote = new RemoteAccessService(Path.Combine(_dir, "remote-access.json"));
        RemoteTunnelService t = Create(launcher, () => false, () => gaming);
        t.Broker = new TunnelPairingBroker(remote, () => t.PublicUrl);

        gaming = true;
        RemoteTunnelStatus blocked = await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        Assert.Equal(RemoteTunnelReasons.GamingActive, blocked.ReasonCode);
        Assert.Equal(0, launcher.Started);

        gaming = false;
        await GoOnline(t);
        gaming = true;
        Assert.True(t.StopForGaming());
        Assert.Equal(RemoteTunnelState.Off, t.State);
        Assert.False(t.GetStatus().Enabled);
        Assert.Equal(RemoteTunnelReasons.GamingActive, t.GetStatus().ReasonCode);
        Assert.All(launcher.Processes, p => Assert.True(p.Killed));

        gaming = false;
        t.ReopenAfterGaming();
        Assert.Equal(RemoteTunnelState.Off, t.State);
    }

    [Fact]
    public async Task Missing_component_asks_to_install_instead_of_pretending()
    {
        var launcher = new FakeLauncher();
        var component = new CloudflaredComponent(Path.Combine(_dir, "none"), () => false, () => null, () => false, (_, _, _, _, _) => Task.CompletedTask);
        var t = new RemoteTunnelService(new TunnelSettingsStore(_dir, new PlainProtector()), component, launcher, () => false, () => false,
            () => Port, autoStart: false, delay: (_, _) => Task.CompletedTask);
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        await WaitFor(() => t.GetStatus().ReasonCode == RemoteTunnelReasons.ComponentMissing);
        Assert.Equal(RemoteTunnelActions.InstallComponent, t.GetStatus().NextAction);
        Assert.Equal(0, launcher.Started);
    }

    [Fact]
    public async Task Named_tunnel_token_is_stored_protected_and_passed_by_environment_only()
    {
        var (t, _, launcher) = Online();
        launcher.OnStart = lines => lines("INF Registered tunnel connection connIndex=0");
        string token = "eyJhIjoiYWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXoiLCJ0IjoiMTIzIn0=";
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { NamedToken = token, NamedPublicUrl = "parux.example.com" }, CancellationToken.None);
        await GoOnline(t);

        Assert.Equal("https://parux.example.com", t.PublicUrl);
        FakeProcess p = launcher.Processes[0];
        Assert.DoesNotContain(token, string.Join(' ', p.Arguments));
        Assert.Equal(token, p.Environment["TUNNEL_TOKEN"]);
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_dir, "remote-tunnel.json")));
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_dir, "remote-tunnel-token.bin")));
        Assert.DoesNotContain(token, JsonSerializer.Serialize(t.GetStatus()));
        Assert.True(t.GetStatus().HasNamedToken);
    }

    [Fact]
    public async Task Rejected_token_is_final_and_not_retried()
    {
        var (t, _, launcher) = Online();
        launcher.OnStart = lines => lines("ERR Unauthorized: Invalid tunnel secret");
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { NamedToken = "A".PadRight(40, 'b'), NamedPublicUrl = "parux.example.com" }, CancellationToken.None);
        await t.ConfigureAsync(new RemoteTunnelConfigRequest { Consent = true, Enabled = true }, CancellationToken.None);
        await WaitFor(() => t.State == RemoteTunnelState.Error);
        Assert.Equal(RemoteTunnelReasons.TokenRejected, t.GetStatus().ReasonCode);
        Assert.Equal(1, launcher.Started);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private sealed class PlainProtector : ISecretProtector
    {
        public string Description => "test";
        public string Protect(string purpose, string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(purpose + "|" + plaintext)).Replace("e", "E");
        public string? Unprotect(string purpose, string blob) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob.Replace("E", "e"))).Split('|', 2) is [var p, var v] && p == purpose ? v : null;
    }

    internal sealed class FakeProcess : ITunnelProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Killed;
        public IReadOnlyList<string> Arguments = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>();
        public Task<int> Exited => _exit.Task;
        public void Kill() { Killed = true; _exit.TrySetResult(-1); }
        public void Exit() => _exit.TrySetResult(1);
        public void Dispose() { }
    }

    internal sealed class FakeLauncher : ITunnelProcessLauncher
    {
        public Action<Action<string>> OnStart = _ => { };
        public bool ExitImmediately;
        public int Started;
        public List<FakeProcess> Processes = new();

        public ITunnelProcess Start(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, Action<string> onLine)
        {
            Started++;
            var process = new FakeProcess { Arguments = arguments, Environment = environment };
            Processes.Add(process);
            if (ExitImmediately) process.Exit();
            OnStart(onLine);
            return process;
        }
    }
}
