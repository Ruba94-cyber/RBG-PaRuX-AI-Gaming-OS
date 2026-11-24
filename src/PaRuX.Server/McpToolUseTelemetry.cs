namespace PaRuX.Server;

/// <summary>
/// Privacy-safe, process-local MCP telemetry used to verify that schema narrowing actually reduces
/// prompt cost without hiding the tool the model subsequently needs. No prompt text, tool arguments,
/// tool results, file paths, or external server configuration are retained. The store is deliberately
/// bounded so long-running local sessions cannot turn observability into an unbounded memory sink.
/// </summary>
public static class McpToolUseTelemetry
{
    private const int MaxRoutingEvents = 512;
    private const int MaxExecutionEvents = 1_024;
    private static readonly object Gate = new();
    private static readonly Queue<RoutingEvent> RoutingEvents = new();
    private static readonly Queue<ExecutionEvent> ExecutionEvents = new();

    public sealed record RoutingEvent(
        DateTimeOffset TimestampUtc,
        bool Routed,
        int OriginalCount,
        int SelectedCount,
        int OriginalCharacters,
        int SelectedCharacters,
        string Reason,
        long ElapsedMilliseconds);

    public sealed record ExecutionEvent(
        DateTimeOffset TimestampUtc,
        string ToolName,
        int AdvertisedToolCount,
        bool Success,
        string Outcome,
        bool ArgumentParseFallback,
        long ElapsedMilliseconds);

    public sealed record Snapshot(
        int RoutingEventCount,
        int ExecutionEventCount,
        int RoutedEventCount,
        int OriginalSchemas,
        int SelectedSchemas,
        long OriginalSchemaCharacters,
        long SelectedSchemaCharacters,
        double SchemaCountReductionRatio,
        double SchemaCharacterReductionRatio,
        double RoutingActivationRate,
        int SuccessfulExecutions,
        int FailedExecutions,
        double ExecutionSuccessRate,
        int ArgumentParseFallbacks,
        double ArgumentParseFallbackRate,
        double AverageAdvertisedToolsAtExecution,
        IReadOnlyList<RoutingEvent> RecentRouting,
        IReadOnlyList<ExecutionEvent> RecentExecutions,
        RepairCounters? Repairs = null);

    /// <summary>Self-healing tool-call counters (no arguments or results are retained).</summary>
    public sealed record RepairCounters(
        int Detected, int FixedLocally, int Reprompted, int Exhausted, int ExecutionFailuresFedBack, int TextualCallsRecovered);

    private static int _repairDetected, _repairFixedLocally, _repairReprompted, _repairExhausted, _executionFailuresFedBack, _textualRecovered;

    /// <summary>kind: detected | fixed-locally | reprompted | exhausted | execution-failure | textual-recovered.</summary>
    public static void RecordRepair(string kind)
    {
        switch (kind)
        {
            case "detected": Interlocked.Increment(ref _repairDetected); break;
            case "fixed-locally": Interlocked.Increment(ref _repairFixedLocally); break;
            case "reprompted": Interlocked.Increment(ref _repairReprompted); break;
            case "exhausted": Interlocked.Increment(ref _repairExhausted); break;
            case "execution-failure": Interlocked.Increment(ref _executionFailuresFedBack); break;
            case "textual-recovered": Interlocked.Increment(ref _textualRecovered); break;
        }
    }

    public static RepairCounters GetRepairCounters() => new(
        Volatile.Read(ref _repairDetected), Volatile.Read(ref _repairFixedLocally), Volatile.Read(ref _repairReprompted),
        Volatile.Read(ref _repairExhausted), Volatile.Read(ref _executionFailuresFedBack), Volatile.Read(ref _textualRecovered));

    public static void RecordRouting(
        bool routed,
        int originalCount,
        int selectedCount,
        int originalCharacters,
        int selectedCharacters,
        string reason,
        long elapsedMilliseconds)
    {
        var item = new RoutingEvent(
            DateTimeOffset.UtcNow,
            routed,
            Math.Max(0, originalCount),
            Math.Max(0, selectedCount),
            Math.Max(0, originalCharacters),
            Math.Max(0, selectedCharacters),
            Bound(reason, 320),
            Math.Max(0, elapsedMilliseconds));
        lock (Gate)
        {
            RoutingEvents.Enqueue(item);
            Trim(RoutingEvents, MaxRoutingEvents);
        }
    }

    public static void RecordExecution(
        string toolName,
        int advertisedToolCount,
        bool success,
        string outcome,
        bool argumentParseFallback,
        long elapsedMilliseconds)
    {
        var item = new ExecutionEvent(
            DateTimeOffset.UtcNow,
            Bound(toolName, 160),
            Math.Max(0, advertisedToolCount),
            success,
            Bound(outcome, 80),
            argumentParseFallback,
            Math.Max(0, elapsedMilliseconds));
        lock (Gate)
        {
            ExecutionEvents.Enqueue(item);
            Trim(ExecutionEvents, MaxExecutionEvents);
        }
    }

    public static Snapshot GetSnapshot(int recentLimit = 20)
    {
        recentLimit = Math.Clamp(recentLimit, 0, 100);
        lock (Gate)
        {
            RoutingEvent[] routing = RoutingEvents.ToArray();
            ExecutionEvent[] executions = ExecutionEvents.ToArray();

            int originalSchemas = routing.Sum(item => item.OriginalCount);
            int selectedSchemas = routing.Sum(item => item.SelectedCount);
            long originalChars = routing.Sum(item => (long)item.OriginalCharacters);
            long selectedChars = routing.Sum(item => (long)item.SelectedCharacters);
            int successCount = executions.Count(item => item.Success);
            int failedCount = executions.Length - successCount;
            int fallbackCount = executions.Count(item => item.ArgumentParseFallback);

            return new Snapshot(
                routing.Length,
                executions.Length,
                routing.Count(item => item.Routed),
                originalSchemas,
                selectedSchemas,
                originalChars,
                selectedChars,
                ReductionRatio(originalSchemas, selectedSchemas),
                ReductionRatio(originalChars, selectedChars),
                routing.Length == 0 ? 0d : routing.Count(item => item.Routed) / (double)routing.Length,
                successCount,
                failedCount,
                executions.Length == 0 ? 0d : successCount / (double)executions.Length,
                fallbackCount,
                executions.Length == 0 ? 0d : fallbackCount / (double)executions.Length,
                executions.Length == 0 ? 0d : executions.Average(item => item.AdvertisedToolCount),
                routing.TakeLast(recentLimit).ToArray(),
                executions.TakeLast(recentLimit).ToArray(),
                GetRepairCounters());
        }
    }

    internal static void ClearForTests()
    {
        lock (Gate)
        {
            RoutingEvents.Clear();
            ExecutionEvents.Clear();
        }
        _repairDetected = _repairFixedLocally = _repairReprompted = _repairExhausted = _executionFailuresFedBack = _textualRecovered = 0;
    }

    private static double ReductionRatio(long original, long selected)
    {
        if (original <= 0) return 0d;
        return Math.Clamp(1d - selected / (double)original, 0d, 1d);
    }

    private static string Bound(string? value, int maxCharacters)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return normalized.Length <= maxCharacters ? normalized : normalized[..maxCharacters];
    }

    private static void Trim<T>(Queue<T> queue, int maximum)
    {
        while (queue.Count > maximum) queue.Dequeue();
    }
}
