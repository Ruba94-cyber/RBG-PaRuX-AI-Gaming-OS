using System.Diagnostics;
using System.Text;

namespace PaRuX.Server;

/// <summary>
/// Opt-in per-request phase timings for the chat hot path (PARUX_CHAT_PHASE_TRACE=1). Off by default:
/// nothing is allocated or written then. When on, one line per request is appended to
/// <c>&lt;data root&gt;/logs/chat-phases.log</c> with elapsed milliseconds at each mark, so time-to-first-token
/// overhead can be attributed to routing, retrieval, token counting, upstream prefill and streaming.
/// Only phase names and durations are written, never prompt text, model paths or credentials.
/// </summary>
internal sealed class ChatPhaseTrace
{
    internal static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("PARUX_CHAT_PHASE_TRACE"), "1", StringComparison.Ordinal);

    private static readonly object WriteLock = new();
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly List<(string Name, double Ms)> _marks = new(16);
    private bool _flushed;

    private ChatPhaseTrace() { }

    private static readonly AsyncLocal<ChatPhaseTrace?> CurrentTrace = new();

    /// <summary>The trace of the chat request on this async flow, or null (always null when tracing is off).</summary>
    internal static ChatPhaseTrace? Current => Enabled ? CurrentTrace.Value : null;

    internal static ChatPhaseTrace? Start()
    {
        if (!Enabled) return null;
        var trace = new ChatPhaseTrace();
        CurrentTrace.Value = trace;
        return trace;
    }

    internal void Mark(string phase)
    {
        lock (_marks) _marks.Add((phase, Stopwatch.GetElapsedTime(_start).TotalMilliseconds));
    }

    /// <summary>Marks once (first call wins), e.g. the first streamed line.</summary>
    internal void MarkOnce(string phase)
    {
        lock (_marks)
        {
            foreach ((string name, _) in _marks)
                if (string.Equals(name, phase, StringComparison.Ordinal)) return;
            _marks.Add((phase, Stopwatch.GetElapsedTime(_start).TotalMilliseconds));
        }
    }

    internal void Flush()
    {
        string line;
        lock (_marks)
        {
            if (_flushed) return;
            _flushed = true;
            var builder = new StringBuilder(256);
            builder.Append(DateTimeOffset.UtcNow.ToString("O"));
            foreach ((string name, double ms) in _marks)
                builder.Append(' ').Append(name).Append('=').Append(ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" total=").Append(Stopwatch.GetElapsedTime(_start).TotalMilliseconds
                .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            line = builder.ToString();
        }
        try
        {
            string directory = Path.Combine(PaRuX.Contracts.PaRuXDataRoot.Path, "logs");
            Directory.CreateDirectory(directory);
            lock (WriteLock) File.AppendAllText(Path.Combine(directory, "chat-phases.log"), line + Environment.NewLine);
        }
        catch { }
    }
}
