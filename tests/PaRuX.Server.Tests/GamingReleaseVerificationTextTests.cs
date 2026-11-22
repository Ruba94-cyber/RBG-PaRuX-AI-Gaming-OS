using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingReleaseVerificationTextTests
{
    [Fact]
    public void Verified_ShowsMeasuredMibInBothLanguages()
    {
        var status = new GamingStatusDto { AiReleaseVerification = "verified", AiVramReleasedMib = 3299 };
        Assert.Equal("GPU release verified (3299 MiB freed)", GamingReleaseVerificationText.English(status));
        Assert.Equal("rilascio GPU verificato (3299 MiB liberati)", GamingReleaseVerificationText.Italian(status));
    }

    [Fact]
    public void Verified_WithoutMeasuredMib_NeverInventsANumber()
    {
        var status = new GamingStatusDto { AiReleaseVerification = "verified", AiVramReleasedMib = null };
        Assert.DoesNotContain("MiB", GamingReleaseVerificationText.English(status));
        Assert.DoesNotContain("MiB", GamingReleaseVerificationText.Italian(status));
    }

    [Theory]
    [InlineData("unverified", "unverified", "non verificato")]
    [InlineData("not_released", "NOT released", "NON rilasciata")]
    public void DegradedOutcomes_AreSaidHonestly(string outcome, string english, string italian)
    {
        var status = new GamingStatusDto { AiReleaseVerification = outcome, AiVramReleasedMib = 12 };
        Assert.Contains(english, GamingReleaseVerificationText.English(status));
        Assert.Contains(italian, GamingReleaseVerificationText.Italian(status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-else")]
    public void NoCheckOrUnknownOutcome_IsEmpty(string? outcome)
    {
        var status = new GamingStatusDto { AiReleaseVerification = outcome };
        Assert.Equal(string.Empty, GamingReleaseVerificationText.English(status));
        Assert.Equal(string.Empty, GamingReleaseVerificationText.Italian(status));
    }

    [Fact]
    public void EnsureLoadLogVerbosity_AddsLv4_WhenNoVerbosityIsSet()
    {
        var args = new List<string> { "--model", "m.gguf" };
        LlamaLoadLogParser.EnsureLoadLogVerbosity(args);
        Assert.Equal(new[] { "--model", "m.gguf", "-lv", "4" }, args);
    }

    [Theory]
    [InlineData("-lv")]
    [InlineData("--log-verbosity")]
    [InlineData("-v")]
    [InlineData("--verbose")]
    public void EnsureLoadLogVerbosity_KeepsAnExistingChoice(string flag)
    {
        var args = new List<string> { "--model", "m.gguf", flag };
        if (flag is "-lv" or "--log-verbosity") args.Add("2");
        List<string> before = new(args);
        LlamaLoadLogParser.EnsureLoadLogVerbosity(args);
        Assert.Equal(before, args);
    }
}
