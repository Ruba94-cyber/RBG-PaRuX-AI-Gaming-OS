namespace PaRuX.Server.Tunnel;

/// <summary>
/// Rate limit and lockout for the unauthenticated pairing steps reachable from the Internet. Every tunnel request reaches Core
/// from loopback and Cf-Connecting-Ip is client-supplied, so there is no trustworthy per-source key: the budget is one shared
/// bucket for the whole tunnel. That is deliberate. It is much stricter than the LAN limiter because an Internet attacker can
/// try forever, a pairing code is eight digits valid five minutes, and five misses per lockout makes guessing hopeless
/// (about 5 / 10^8 per attempt window). The owner can always pair from the PC or restart the tunnel.
/// </summary>
internal sealed class TunnelPairingGate
{
    internal const int FailuresBeforeLockout = 5;
    internal const int RequestsPerMinute = 20;
    internal static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan LockoutBase = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan LockoutCap = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan LockoutMemory = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private readonly List<DateTime> _failures = new();
    private readonly List<DateTime> _requests = new();
    private int _level;
    private DateTime _lastTrip = DateTime.MinValue;
    private DateTime _lockedUntil = DateTime.MinValue;

    internal TunnelPairingGate(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>False when the caller must wait; <paramref name="retryAfter"/> says how long.</summary>
    internal bool TryEnter(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            DateTime now = _utcNow();
            retryAfter = TimeSpan.Zero;
            if (now < _lockedUntil)
            {
                retryAfter = _lockedUntil - now;
                return false;
            }
            _requests.RemoveAll(t => t <= now.AddMinutes(-1));
            if (_requests.Count >= RequestsPerMinute)
            {
                retryAfter = TimeSpan.FromSeconds(30);
                return false;
            }
            _requests.Add(now);
            return true;
        }
    }

    /// <summary>Counts a failed attempt; returns the lockout it triggered, if any.</summary>
    internal TimeSpan? RecordFailure()
    {
        lock (_gate)
        {
            DateTime now = _utcNow();
            _failures.RemoveAll(t => t <= now - FailureWindow);
            _failures.Add(now);
            if (_failures.Count < FailuresBeforeLockout) return null;

            if (_lastTrip != DateTime.MinValue && now - _lastTrip >= LockoutMemory) _level = 0;
            double minutes = Math.Min(LockoutBase.TotalMinutes * Math.Pow(2, _level), LockoutCap.TotalMinutes);
            if (_level < 16) _level++;
            _lastTrip = now;
            _failures.Clear();
            _lockedUntil = now + TimeSpan.FromMinutes(minutes);
            return TimeSpan.FromMinutes(minutes);
        }
    }

    internal void RecordSuccess()
    {
        lock (_gate) _failures.Clear();
    }

    internal bool IsLocked { get { lock (_gate) return _utcNow() < _lockedUntil; } }

    /// <summary>The owner restarting the tunnel clears a lockout that an Internet host provoked.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _failures.Clear();
            _requests.Clear();
            _level = 0;
            _lastTrip = DateTime.MinValue;
            _lockedUntil = DateTime.MinValue;
        }
    }
}
