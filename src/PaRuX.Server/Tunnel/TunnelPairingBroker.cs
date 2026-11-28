using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Server.Tunnel;

internal enum TunnelPollOutcome { Pending, Approved, Denied, Unknown }

/// <summary>
/// A NEW device that presents a valid pairing code through the tunnel does not receive a token. It becomes a pending request
/// that only the desktop owner (loopback + desktop token, never through the tunnel) can approve or deny. The phone then polls
/// with a private secret. The code, the owner's click and the poll secret are three independent proofs.
/// </summary>
internal sealed class TunnelPairingBroker
{
    internal const int MaxPending = 3;
    internal static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly RemoteAccessService _remote;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string> _tunnelEndpoint;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    internal TunnelPairingGate Gate { get; }

    internal TunnelPairingBroker(RemoteAccessService remote, Func<string> tunnelEndpoint, Func<DateTime>? utcNow = null, TunnelPairingGate? gate = null)
    {
        _remote = remote;
        _tunnelEndpoint = tunnelEndpoint;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Gate = gate ?? new TunnelPairingGate(_utcNow);
    }

    /// <summary>Handles POST /api/v1/remote/pair/complete arriving through the tunnel.</summary>
    internal PairingApprovalPending RequestApproval(PairingCompleteRequest request)
    {
        string name = request.DeviceName?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 80) throw new InvalidDataException("Device name must be 1 to 80 characters.");
        if (!Gate.TryEnter(out TimeSpan wait)) throw Locked(wait);

        lock (_gate)
        {
            Sweep();
            // Checked before the code is spent, so a flood of pending requests cannot burn a valid code.
            if (_pending.Values.Count(p => p.Status == TunnelPollOutcome.Pending) >= MaxPending)
                throw new InvalidOperationException("Too many pairing requests are waiting for approval on the PC.");
        }

        if (!_remote.TryConsumePairingCode(request.Code))
        {
            TimeSpan? lockout = Gate.RecordFailure();
            if (lockout is { } locked) throw Locked(locked);
            throw new UnauthorizedAccessException("The pairing code is invalid or expired.");
        }
        Gate.RecordSuccess();

        DateTime now = _utcNow();
        var entry = new Pending
        {
            Id = Guid.NewGuid().ToString("N"),
            Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            DeviceName = name,
            RequestedUtc = now,
            ExpiresUtc = now + RequestLifetime,
            Status = TunnelPollOutcome.Pending
        };
        lock (_gate) _pending[entry.Id] = entry;
        return new PairingApprovalPending
        {
            RequestId = entry.Id,
            PollSecret = entry.Secret,
            PollIntervalSeconds = 2,
            ExpiresUtc = entry.ExpiresUtc,
            Message = "Confirm this pairing on the PC where PaRuX is running."
        };
    }

    internal IReadOnlyList<RemoteTunnelPendingPairing> ListPending()
    {
        lock (_gate)
        {
            Sweep();
            return _pending.Values.Where(p => p.Status == TunnelPollOutcome.Pending)
                .OrderBy(p => p.RequestedUtc)
                .Select(p => new RemoteTunnelPendingPairing
                {
                    Id = p.Id, DeviceName = p.DeviceName, RequestedUtc = p.RequestedUtc, ExpiresUtc = p.ExpiresUtc
                }).ToList();
        }
    }

    /// <summary>Owner approval (desktop only). Issues the device; the phone collects the token by polling.</summary>
    internal bool Approve(string id)
    {
        lock (_gate)
        {
            Sweep();
            if (!_pending.TryGetValue(id ?? string.Empty, out Pending? entry) || entry.Status != TunnelPollOutcome.Pending) return false;
            entry.Result = _remote.IssueDevice(entry.DeviceName, _tunnelEndpoint());
            entry.Status = TunnelPollOutcome.Approved;
            entry.ExpiresUtc = _utcNow() + ResultLifetime;
            return true;
        }
    }

    internal bool Deny(string id)
    {
        lock (_gate)
        {
            Sweep();
            if (!_pending.TryGetValue(id ?? string.Empty, out Pending? entry) || entry.Status != TunnelPollOutcome.Pending) return false;
            entry.Status = TunnelPollOutcome.Denied;
            entry.ExpiresUtc = _utcNow() + TimeSpan.FromMinutes(1);
            return true;
        }
    }

    /// <summary>Handles POST /api/v1/remote/pair/poll. The token is handed over exactly once.</summary>
    internal TunnelPollOutcome Poll(PairingPollRequest request, out PairingCompleteResponse? result)
    {
        result = null;
        if (!Gate.TryEnter(out TimeSpan wait)) throw Locked(wait);
        lock (_gate)
        {
            Sweep();
            if (!_pending.TryGetValue(request.RequestId ?? string.Empty, out Pending? entry)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(entry.Secret), Encoding.UTF8.GetBytes(request.PollSecret ?? string.Empty)))
            {
                TimeSpan? lockout = Gate.RecordFailure();
                if (lockout is { } locked) throw Locked(locked);
                return TunnelPollOutcome.Unknown;
            }
            switch (entry.Status)
            {
                case TunnelPollOutcome.Approved:
                    result = entry.Result;
                    _pending.Remove(entry.Id);
                    return TunnelPollOutcome.Approved;
                case TunnelPollOutcome.Denied:
                    _pending.Remove(entry.Id);
                    return TunnelPollOutcome.Denied;
                default:
                    return TunnelPollOutcome.Pending;
            }
        }
    }

    /// <summary>Called when the tunnel stops: nothing waiting for approval survives it.</summary>
    internal void Clear()
    {
        lock (_gate) _pending.Clear();
    }

    private void Sweep()
    {
        DateTime now = _utcNow();
        foreach (string id in _pending.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToList()) _pending.Remove(id);
    }

    private static RemoteAccessService.PairingLockedException Locked(TimeSpan wait)
    {
        int seconds = (int)Math.Ceiling(Math.Max(1, wait.TotalSeconds));
        return new RemoteAccessService.PairingLockedException("Too many pairing attempts through the Internet tunnel. Try again later.", TimeSpan.FromSeconds(seconds));
    }

    private sealed class Pending
    {
        public string Id = string.Empty;
        public string Secret = string.Empty;
        public string DeviceName = string.Empty;
        public DateTime RequestedUtc;
        public DateTime ExpiresUtc;
        public TunnelPollOutcome Status;
        public PairingCompleteResponse? Result;
    }
}
