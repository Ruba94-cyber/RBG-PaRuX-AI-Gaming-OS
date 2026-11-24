using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Non-blocking GPU utilisation / VRAM reader for the training history charts. It never waits for a
/// process launch on the log-pump path: a read returns the most recent sample immediately and, at most
/// once per <see cref="RefreshInterval"/>, kicks a background refresh through the shared cached telemetry
/// provider (the same nvidia-smi source the resource guard uses). When no sensor answers, or the last sample
/// is older than <see cref="MaxAge"/>, the result is null, so a chart shows a gap instead of a fabricated zero.
/// VRAM is the device-wide figure (it includes chat/game memory on purpose: that is what the run competes for).
/// </summary>
internal sealed class TrainingGpuSampler
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    internal static TrainingGpuSampler Shared { get; } = new(
        ct => CachedGpuPerformanceTelemetrySnapshotProvider.Shared.GetSnapshotAsync(ct));

    private readonly Func<CancellationToken, Task<GpuPerformanceTelemetrySnapshot?>> _source;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private (double? Util, double? Vram)? _latest;
    private DateTime _latestUtc = DateTime.MinValue;
    private DateTime _lastKickUtc = DateTime.MinValue;
    private bool _inFlight;

    internal TrainingGpuSampler(
        Func<CancellationToken, Task<GpuPerformanceTelemetrySnapshot?>> source, Func<DateTime>? utcNow = null)
    {
        _source = source;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal (double? GpuUtilPercent, double? VramMib)? Read()
    {
        DateTime now = _utcNow();
        bool kick = false;
        (double? Util, double? Vram)? result;
        lock (_gate)
        {
            if (!_inFlight && now - _lastKickUtc >= RefreshInterval)
            {
                _inFlight = true;
                _lastKickUtc = now;
                kick = true;
            }
            result = now - _latestUtc <= MaxAge ? _latest : null;
        }
        if (kick) _ = Task.Run(RefreshAsync);
        return result;
    }

    internal async Task RefreshAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            GpuPerformanceTelemetrySnapshot? snapshot = await _source(cts.Token).ConfigureAwait(false);
            // The trainer uses CUDA device 0; with several NVIDIA GPUs that is index 0 in nvidia-smi order.
            GpuPerformanceTelemetryDto? device = snapshot?.Devices.OrderBy(d => d.Index).FirstOrDefault();
            lock (_gate)
            {
                if (device == null || (device.UtilizationPercent == null && device.MemoryUsedMib == null))
                    _latest = null;
                else
                {
                    _latest = (device.UtilizationPercent, device.MemoryUsedMib);
                    _latestUtc = _utcNow();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            lock (_gate) _latest = null;
        }
        finally
        {
            lock (_gate) _inFlight = false;
        }
    }
}
