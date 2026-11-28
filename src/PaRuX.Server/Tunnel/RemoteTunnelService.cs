using PaRuX.Contracts;

namespace PaRuX.Server.Tunnel;

/// <summary>The loopback port that only cloudflared talks to. Set once at startup (0 = no listener, e.g. Corporate).</summary>
internal static class TunnelListener
{
    internal static int Port { get; set; }
}

/// <summary>
/// Owns the optional Internet tunnel: Off / Starting / Online (public URL) / Error (friendly reason + next action).
/// Off by default, needs recorded consent, never runs in Corporate mode, and is stopped (and switched off) by Gaming
/// hard-off. Start is bounded (3 attempts, 45 s each), cancellable, and the child is killed on stop, on Core exit and,
/// through the kill-on-close job, when Core dies.
/// </summary>
internal sealed class RemoteTunnelService : IHostedService, IGamingHardOffParticipant, IDisposable
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    private readonly object _gate = new();
    private readonly TunnelSettingsStore _settings;
    private readonly CloudflaredComponent _component;
    private readonly ITunnelProcessLauncher _launcher;
    private readonly Func<bool> _corporate;
    private readonly Func<bool> _gaming;
    private readonly Func<int> _listenerPort;
    private readonly bool _autoStart;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _stableAfter;
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;

    private RemoteTunnelState _state = RemoteTunnelState.Off;
    private string _publicUrl = string.Empty;
    private string _reason = RemoteTunnelReasons.None;
    private string _action = RemoteTunnelActions.None;
    private string _message = string.Empty;
    private int _attempt;

    internal TunnelPairingBroker Broker { get; set; } = null!;

    internal int ListenerPort => _listenerPort();

    internal RemoteTunnelService(TunnelSettingsStore settings, CloudflaredComponent component, ITunnelProcessLauncher launcher,
        Func<bool> corporate, Func<bool> gaming, Func<int>? listenerPort = null, bool autoStart = true,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? startTimeout = null, TimeSpan? stableAfter = null)
    {
        _settings = settings;
        _component = component;
        _launcher = launcher;
        _corporate = corporate;
        _gaming = gaming;
        _listenerPort = listenerPort ?? (() => TunnelListener.Port);
        _autoStart = autoStart;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _startTimeout = startTimeout ?? StartTimeout;
        _stableAfter = stableAfter ?? TimeSpan.FromSeconds(60);
    }

    // ---- observable state -------------------------------------------------------------------------------------------

    internal RemoteTunnelState State { get { lock (_gate) return _state; } }

    /// <summary>The public https address while Online, else empty. Pairing QR codes and the Host check read this.</summary>
    internal string PublicUrl { get { lock (_gate) return _state == RemoteTunnelState.Online ? _publicUrl : string.Empty; } }

    /// <summary>True while a tunnel request may be served: the process is up or being brought up.</summary>
    internal bool AcceptsTraffic { get { lock (_gate) return _state is RemoteTunnelState.Online or RemoteTunnelState.Starting; } }

    /// <summary>Host header the tunnel may be reached under: only the current public host.</summary>
    internal bool IsAcceptedHost(string? hostHeader)
    {
        string url;
        lock (_gate) url = _publicUrl;
        if (string.IsNullOrEmpty(url) || !HostHeaderPolicy.TryGetHostName(hostHeader, out string name)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Host.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    internal RemoteTunnelStatus GetStatus()
    {
        TunnelSettingsStore.Document doc = _settings.Load();
        CloudflaredDetection found = _component.Detect();
        bool corporate = _corporate();
        lock (_gate)
        {
            return new RemoteTunnelStatus
            {
                State = _state,
                Enabled = doc.Enabled,
                ConsentGiven = doc.ConsentUtc is not null,
                Allowed = !corporate,
                Mode = _settings.HasToken ? "named" : "quick",
                PublicUrl = _state == RemoteTunnelState.Online ? _publicUrl : string.Empty,
                ReasonCode = corporate ? RemoteTunnelReasons.CorporateBlocked : _reason,
                Message = corporate ? "The Internet tunnel is not available in PaRuX Corporate Lite." : _message,
                NextAction = corporate ? RemoteTunnelActions.None : _action,
                Attempt = _attempt,
                ListenerPort = _listenerPort(),
                MaxAttempts = MaxAttempts,
                ComponentDetected = found.Detected,
                ComponentOwnership = found.Ownership?.ToString() ?? string.Empty,
                ComponentVersion = found.Version ?? string.Empty,
                ComponentPath = found.Ownership == ComponentOwnership.USER_MANAGED ? found.Path ?? string.Empty : string.Empty,
                PinnedVersion = CloudflaredPin.Version,
                ComponentSkipped = found.Skipped,
                HasNamedToken = _settings.HasToken,
                NamedPublicUrl = doc.NamedPublicUrl,
                PendingPairings = Broker?.ListPending().ToList() ?? new List<RemoteTunnelPendingPairing>()
            };
        }
    }

    // ---- gating -----------------------------------------------------------------------------------------------------

    /// <summary>Null when starting is allowed; otherwise why not.</summary>
    internal (string Reason, string Message, string Action)? Blocked()
    {
        if (_corporate())
            return (RemoteTunnelReasons.CorporateBlocked, "The Internet tunnel is not available in PaRuX Corporate Lite.", RemoteTunnelActions.None);
        if (_gaming())
            return (RemoteTunnelReasons.GamingActive, "Gaming Mode is on, so the Internet tunnel is off. Turn it on again after you finish gaming.", RemoteTunnelActions.EndGaming);
        if (_listenerPort() <= 0)
            return (RemoteTunnelReasons.ListenerUnavailable, "PaRuX could not open its private tunnel port. Restart PaRuX and try again.", RemoteTunnelActions.Retry);
        return null;
    }

    // ---- commands ---------------------------------------------------------------------------------------------------

    /// <summary>Applies the Settings form. Consent is required before the first start and is recorded with its time.</summary>
    internal async Task<RemoteTunnelStatus> ConfigureAsync(RemoteTunnelConfigRequest request, CancellationToken ct)
    {
        if (_corporate()) throw new InvalidOperationException("The Internet tunnel is not available in PaRuX Corporate Lite.");
        TunnelSettingsStore.Document doc = _settings.Load();

        if (request.NamedToken is not null)
        {
            if (request.NamedToken.Trim().Length == 0) _settings.ClearToken();
            else
            {
                string token = request.NamedToken.Trim();
                if (token.Length is < 20 or > 4096 || token.Any(c => c <= ' '))
                    throw new InvalidDataException("That does not look like a Cloudflare tunnel token.");
                _settings.SaveToken(token);
            }
        }
        if (request.NamedPublicUrl is not null)
        {
            if (request.NamedPublicUrl.Trim().Length == 0) doc.NamedPublicUrl = string.Empty;
            else if (TunnelOutputParser.TryNormalizePublicUrl(request.NamedPublicUrl, out string normalized)) doc.NamedPublicUrl = normalized;
            else throw new InvalidDataException("Enter the https address your named tunnel serves, for example https://parux.example.com.");
        }
        if (request.Consent == true && doc.ConsentUtc is null) doc.ConsentUtc = DateTime.UtcNow;
        if (request.Consent == false) { doc.ConsentUtc = null; if (request.Enabled is null) doc.Enabled = false; }

        if (request.Enabled == true)
        {
            if (doc.ConsentUtc is null)
            {
                _settings.Save(doc);
                SetError(RemoteTunnelReasons.ConsentRequired, "Read and accept what the tunnel exposes before turning it on.", RemoteTunnelActions.Consent);
                return GetStatus();
            }
            (string Reason, string Message, string Action)? blocked = Blocked();
            if (blocked is { } b)
            {
                _settings.Save(doc);
                SetError(b.Reason, b.Message, b.Action);
                return GetStatus();
            }
            doc.Enabled = true;
            _settings.Save(doc);
            Begin();
        }
        else if (request.Enabled == false)
        {
            doc.Enabled = false;
            _settings.Save(doc);
            await StopAsync(RemoteTunnelReasons.None, string.Empty, ct).ConfigureAwait(false);
        }
        else
        {
            _settings.Save(doc);
            // Changing the token or hostname of a running tunnel takes effect by restarting it.
            if (State is RemoteTunnelState.Online or RemoteTunnelState.Starting && (request.NamedToken is not null || request.NamedPublicUrl is not null))
            {
                await StopAsync(RemoteTunnelReasons.None, string.Empty, ct).ConfigureAwait(false);
                Begin();
            }
        }
        return GetStatus();
    }

    /// <summary>Starts the supervised run if it is not already running. Never throws.</summary>
    internal void Begin()
    {
        lock (_gate)
        {
            if (_cts is not null && !_run.IsCompleted) return;
            Broker?.Gate.Reset();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _state = RemoteTunnelState.Starting;
            _reason = RemoteTunnelReasons.None;
            _action = RemoteTunnelActions.None;
            _message = "Starting the tunnel…";
            _publicUrl = string.Empty;
            _attempt = 0;
            _run = Task.Run(() => RunAsync(token));
        }
    }

    internal async Task StopAsync(string reason, string message, CancellationToken ct)
    {
        Task run;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            run = _run;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { await run.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        lock (_gate)
        {
            _state = RemoteTunnelState.Off;
            _publicUrl = string.Empty;
            _reason = reason;
            _message = message;
            _action = RemoteTunnelActions.None;
            _attempt = 0;
        }
        Broker?.Clear();
    }

    /// <summary>The person's "Install with PaRuX". Progress is the caller's concern; failures are honest and typed.</summary>
    internal async Task InstallComponentAsync(CancellationToken ct)
    {
        try { await _component.InstallAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            SetError(RemoteTunnelReasons.InstallFailed,
                "PaRuX could not download or verify cloudflared (" + ex.Message + "). Check your connection and try again.",
                RemoteTunnelActions.Retry);
            throw;
        }
        lock (_gate)
        {
            if (_reason is RemoteTunnelReasons.ComponentMissing or RemoteTunnelReasons.InstallFailed or RemoteTunnelReasons.ComponentSkipped)
            {
                _state = RemoteTunnelState.Off;
                _reason = RemoteTunnelReasons.None;
                _action = RemoteTunnelActions.None;
                _message = string.Empty;
            }
        }
    }

    // ---- supervised run ---------------------------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Blocked() is { } blocked)
                {
                    SetError(blocked.Reason, blocked.Message, blocked.Action);
                    return;
                }

                failures++;
                lock (_gate)
                {
                    _attempt = failures;
                    _state = RemoteTunnelState.Starting;
                    _publicUrl = string.Empty;
                    _message = failures == 1 ? "Starting the tunnel…" : "Trying again (" + failures + " of " + MaxAttempts + ")…";
                }

                AttemptResult result = await AttemptAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                if (result.StableFor >= _stableAfter) failures = 1;
                if (result.Fatal || failures >= MaxAttempts)
                {
                    SetError(result.Reason, result.Message, result.Action);
                    return;
                }
                await _delay(failures == 1 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetError(RemoteTunnelReasons.StartFailed, "The tunnel stopped unexpectedly (" + ex.GetType().Name + "). Try again.", RemoteTunnelActions.Retry);
        }
    }

    private sealed record AttemptResult(bool Fatal, string Reason, string Message, string Action, TimeSpan StableFor);

    private async Task<AttemptResult> AttemptAsync(CancellationToken ct)
    {
        TunnelSettingsStore.Document doc = _settings.Load();
        bool named = _settings.HasToken;
        string? token = named ? _settings.ReadToken() : null;
        if (named && token is null)
            return new AttemptResult(true, RemoteTunnelReasons.TokenRejected,
                "PaRuX cannot read the saved tunnel token on this account. Enter it again.", RemoteTunnelActions.CheckToken, TimeSpan.Zero);
        if (named && doc.NamedPublicUrl.Length == 0)
            return new AttemptResult(true, RemoteTunnelReasons.NamedUrlMissing,
                "Enter the https address your named tunnel serves, then turn the tunnel on again.", RemoteTunnelActions.CheckToken, TimeSpan.Zero);

        string? exe = _component.ResolveForLaunch(out string failure);
        if (exe is null)
        {
            bool skipped = _component.Detect().Skipped;
            return new AttemptResult(true,
                failure.Length > 0 ? RemoteTunnelReasons.InstallFailed : skipped ? RemoteTunnelReasons.ComponentSkipped : RemoteTunnelReasons.ComponentMissing,
                failure.Length > 0 ? failure : "cloudflared is needed for the tunnel. Install it with PaRuX or point PaRuX at your own copy.",
                RemoteTunnelActions.InstallComponent, TimeSpan.Zero);
        }

        string configPath = EnsureEmptyConfig();
        var args = new List<string> { "--config", configPath, "tunnel", "--no-autoupdate" };
        var env = new Dictionary<string, string>();
        int port = _listenerPort();
        if (named)
        {
            // Remotely managed tunnel: Cloudflare's dashboard decides where it forwards, so the person points the public
            // hostname at http://127.0.0.1:<ListenerPort> (shown in Settings). The port prefers a fixed value for that reason.
            args.Add("run");
            env["TUNNEL_TOKEN"] = token!;
        }
        else
        {
            args.Add("--url");
            args.Add("http://127.0.0.1:" + port);
        }

        var urlSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new TaskCompletionSource<TunnelOutputSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        string namedUrl = doc.NamedPublicUrl;

        void OnLine(string line)
        {
            if (!named && TunnelOutputParser.TryParseQuickTunnelUrl(line, out string url)) urlSeen.TrySetResult(url);
            switch (TunnelOutputParser.Classify(line))
            {
                case TunnelOutputSignal.Connected: connected.TrySetResult(true); break;
                case TunnelOutputSignal.NetworkFailure: signal.TrySetResult(TunnelOutputSignal.NetworkFailure); break;
                case TunnelOutputSignal.TokenRejected: signal.TrySetResult(TunnelOutputSignal.TokenRejected); break;
            }
        }

        // Re-check right before the launch: Gaming may have started while this attempt was preparing.
        if (_gaming())
            return new AttemptResult(true, RemoteTunnelReasons.GamingActive,
                "Gaming Mode is on, so the Internet tunnel is off. Turn it on again after you finish gaming.", RemoteTunnelActions.Retry, TimeSpan.Zero);

        ITunnelProcess process;
        try { process = _launcher.Start(exe, args, env, OnLine); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new AttemptResult(false, RemoteTunnelReasons.StartFailed,
                "Windows would not start cloudflared (" + ex.Message + "). Security software may be blocking it.", RemoteTunnelActions.Retry, TimeSpan.Zero);
        }

        using (process)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_startTimeout);
                Task ready = Task.WhenAll(named ? Task.FromResult(namedUrl) : urlSeen.Task, connected.Task);
                Task finished = await Task.WhenAny(ready, signal.Task, process.Exited, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token))
                    .ConfigureAwait(false);

                if (finished == ready)
                {
                    string url = named ? namedUrl : await urlSeen.Task.ConfigureAwait(false);
                    DateTime upSince = DateTime.UtcNow;
                    lock (_gate)
                    {
                        _publicUrl = url;
                        _state = RemoteTunnelState.Online;
                        _reason = RemoteTunnelReasons.None;
                        _action = RemoteTunnelActions.None;
                        _message = "The tunnel is online.";
                    }
                    await Task.WhenAny(process.Exited, Task.Delay(Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return new AttemptResult(false, RemoteTunnelReasons.ProcessExited,
                        "The tunnel connection dropped and could not be restored. Check your Internet connection and try again.",
                        RemoteTunnelActions.CheckNetwork, DateTime.UtcNow - upSince);
                }
                if (finished == signal.Task)
                {
                    return await signal.Task.ConfigureAwait(false) == TunnelOutputSignal.TokenRejected
                        ? new AttemptResult(true, RemoteTunnelReasons.TokenRejected,
                            "Cloudflare rejected the tunnel token. Check it in your Cloudflare dashboard and enter it again.", RemoteTunnelActions.CheckToken, TimeSpan.Zero)
                        : new AttemptResult(false, RemoteTunnelReasons.NetworkUnreachable,
                            "PaRuX could not reach Cloudflare. Check your Internet connection (a firewall or VPN may block it).", RemoteTunnelActions.CheckNetwork, TimeSpan.Zero);
                }
                if (finished == process.Exited)
                    return new AttemptResult(false, RemoteTunnelReasons.ProcessExited,
                        "cloudflared stopped right after starting. Try again, or reinstall it.", RemoteTunnelActions.Retry, TimeSpan.Zero);
                ct.ThrowIfCancellationRequested();
                return new AttemptResult(false, RemoteTunnelReasons.UrlTimeout,
                    "Cloudflare did not hand out a public address in time. Check your Internet connection and try again.", RemoteTunnelActions.CheckNetwork, TimeSpan.Zero);
            }
            finally
            {
                // Always: no orphan, whatever happened above.
                process.Kill();
            }
        }
    }

    private string EnsureEmptyConfig()
    {
        // cloudflared reads ~/.cloudflared/config.yml by default, which can turn a quick tunnel into an error. An empty file of
        // PaRuX's own keeps the person's cloudflared configuration out of this tunnel and this tunnel out of theirs.
        string directory = Path.Combine(_settings.StateDirectory, "tunnel");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cloudflared-empty.yml");
        if (!File.Exists(path)) File.WriteAllText(path, "# PaRuX: intentionally empty\n");
        return path;
    }

    private void SetError(string reason, string message, string action)
    {
        lock (_gate)
        {
            _state = RemoteTunnelState.Error;
            _publicUrl = string.Empty;
            _reason = reason;
            _message = message;
            _action = action;
        }
        if (reason is RemoteTunnelReasons.ConsentRequired or RemoteTunnelReasons.GamingActive or RemoteTunnelReasons.CorporateBlocked
            or RemoteTunnelReasons.ComponentMissing or RemoteTunnelReasons.ComponentSkipped)
        {
            // Not a failure of the tunnel itself: nothing is running, so show it as a plain "off, here is why".
            lock (_gate) _state = RemoteTunnelState.Off;
        }
    }

    // ---- host lifetime ----------------------------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_autoStart) return Task.CompletedTask;
        TunnelSettingsStore.Document doc = _settings.Load();
        if (doc.Enabled && doc.ConsentUtc is not null && Blocked() is null) Begin();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await StopAsync(RemoteTunnelReasons.None, string.Empty, CancellationToken.None).ConfigureAwait(false);

    // ---- Gaming hard-off --------------------------------------------------------------------------------------------

    public bool StopForGaming()
    {
        bool wasEnabled = _settings.Load().Enabled;
        StopAsync(wasEnabled ? RemoteTunnelReasons.GamingActive : RemoteTunnelReasons.None,
            wasEnabled ? "The tunnel was turned off because Gaming Mode started. Turn it on again when you are done." : string.Empty,
            CancellationToken.None).GetAwaiter().GetResult();
        if (wasEnabled)
        {
            TunnelSettingsStore.Document doc = _settings.Load();
            doc.Enabled = false;
            _settings.Save(doc);
        }
        return State == RemoteTunnelState.Off;
    }

    /// <summary>Deliberately does nothing: a public tunnel never comes back by itself after Gaming.</summary>
    public void ReopenAfterGaming() { }

    public void Dispose()
    {
        try { StopAsync(RemoteTunnelReasons.None, string.Empty, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
    }
}
