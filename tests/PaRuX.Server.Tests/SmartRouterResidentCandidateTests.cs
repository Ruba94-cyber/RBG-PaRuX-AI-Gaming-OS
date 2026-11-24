using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SmartRouterResidentCandidateTests
{
    [Theory]
    [InlineData(true, false, true)]   // resident model that the budget estimate now rejects stays usable
    [InlineData(true, null, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)] // a candidate that is not loaded keeps the estimate
    [InlineData(false, true, true)]
    public void A_resident_candidate_is_compatible_and_any_other_keeps_its_estimate(
        bool isCurrentlyLoaded, bool? estimated, bool? expected)
    {
        Assert.Equal(expected, SmartRouterService.ResolveCandidateCompatibility(isCurrentlyLoaded, estimated));
    }

    [Fact]
    public void An_unknown_estimate_for_a_non_resident_candidate_stays_unknown()
    {
        Assert.Null(SmartRouterService.ResolveCandidateCompatibility(false, null));
    }
}
