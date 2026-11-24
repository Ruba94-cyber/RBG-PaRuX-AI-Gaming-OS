using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OnnxLoadRetryPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_first_failure_is_not_retried_before_the_cooldown()
        => Assert.False(OnnxLoadRetryPolicy.MayRetry(1, Now.Ticks, Now.AddSeconds(10)));

    [Fact]
    public void A_transient_failure_is_retried_after_the_cooldown()
        => Assert.True(OnnxLoadRetryPolicy.MayRetry(1, Now.Ticks, Now.Add(OnnxLoadRetryPolicy.Cooldown)));

    [Fact]
    public void A_model_that_keeps_failing_stays_off_after_the_maximum_failures()
        => Assert.False(OnnxLoadRetryPolicy.MayRetry(OnnxLoadRetryPolicy.MaxFailures, Now.Ticks, Now.AddHours(1)));

    [Fact]
    public void Without_a_recorded_failure_there_is_nothing_to_retry()
        => Assert.False(OnnxLoadRetryPolicy.MayRetry(0, 0, Now));
}
