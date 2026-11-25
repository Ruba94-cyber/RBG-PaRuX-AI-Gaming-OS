using System.Diagnostics;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Unit tests for the chat hot-path helpers: process working-set lookup without a system-wide snapshot,
/// text health memo TTL/launch-identity invalidation, and zero-overhead opt-in phase tracing.
/// </summary>
public sealed class ChatHotPathTests
{
    [Fact]
    public void ProcessWorkingSet_reads_positive_bytes_for_current_process_and_rejects_null()
    {
        using Process self = Process.GetCurrentProcess();
        long workingSet = ProcessWorkingSet.Read(self);

        Assert.True(workingSet > 0, $"Expected positive working set for current process, got {workingSet}");
        Assert.Throws<ArgumentNullException>(() => ProcessWorkingSet.Read(null!));
    }

    [Fact]
    public void ProcessWorkingSet_throws_for_unstarted_process_instead_of_inventing_a_value()
    {
        using var unstarted = new Process();
        Assert.Throws<InvalidOperationException>(() => ProcessWorkingSet.Read(unstarted));
    }

    [Fact]
    public void TextHealthyMemo_reuses_same_launch_within_ttl_and_invalidates_on_expiry_or_new_launch()
    {
        object launchA = new();
        object launchB = new();
        long t0 = Stopwatch.GetTimestamp();
        long withinTtl = t0 + (long)(Stopwatch.Frequency * 0.25);
        long atTtl = t0 + (long)(Stopwatch.Frequency * LocalAiProcessManager.TextHealthyMemoTtl.TotalSeconds) + 1;

        Assert.True(LocalAiProcessManager.IsTextHealthyMemoReusable(launchA, launchA, t0, withinTtl));
        Assert.False(LocalAiProcessManager.IsTextHealthyMemoReusable(launchA, launchA, t0, atTtl));
        Assert.False(LocalAiProcessManager.IsTextHealthyMemoReusable(launchB, launchA, t0, withinTtl));
        Assert.False(LocalAiProcessManager.IsTextHealthyMemoReusable(null, launchA, t0, withinTtl));
        Assert.False(LocalAiProcessManager.IsTextHealthyMemoReusable(launchA, null, t0, withinTtl));
    }

    [Fact]
    public void ChatPhaseTrace_is_disabled_by_default_and_allocates_nothing()
    {
        if (ChatPhaseTrace.Enabled) return; // test runner explicitly opted in via PARUX_CHAT_PHASE_TRACE=1

        Assert.Null(ChatPhaseTrace.Start());
        Assert.Null(ChatPhaseTrace.Current);
    }
}
