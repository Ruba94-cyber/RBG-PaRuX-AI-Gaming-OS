using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConsoleStartupPolicyTests
{
    [Theory]
    [InlineData(false, "tv-1", true, "minimized", ConsoleStartupDecision.Normal)]       // preference off: nothing changes, even with the TV on
    [InlineData(true, "tv-1", true, "normal", ConsoleStartupDecision.EnterConsole)]      // TV connected: straight to the library on the TV
    [InlineData(true, "tv-1", true, "minimized", ConsoleStartupDecision.EnterConsole)]
    [InlineData(true, "tv-1", false, "normal", ConsoleStartupDecision.Normal)]           // TV off: the usual AI window
    [InlineData(true, "tv-1", false, "minimized", ConsoleStartupDecision.Minimize)]      // TV off: minimized, as chosen
    [InlineData(true, "", true, "minimized", ConsoleStartupDecision.Minimize)]            // no TV chosen yet: the fallback applies, never Console
    [InlineData(true, null, true, "normal", ConsoleStartupDecision.Normal)]
    [InlineData(true, "tv-1", false, "garbage", ConsoleStartupDecision.Normal)]          // unknown value: visible window, never hidden
    [InlineData(true, "tv-1", false, null, ConsoleStartupDecision.Normal)]
    public void The_start_preference_resolves_to_console_normal_or_minimized(
        bool enabled, string? target, bool tvPresent, string? fallback, ConsoleStartupDecision expected) =>
        Assert.Equal(expected, ConsoleStartupPolicy.Decide(enabled, target, tvPresent, fallback));
}
