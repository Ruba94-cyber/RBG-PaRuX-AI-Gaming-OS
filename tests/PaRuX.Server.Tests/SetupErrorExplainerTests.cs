using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SetupErrorExplainerTests
{
    [Theory]
    [InlineData("Downloading the model failed: There is not enough space on the disk.", SetupErrorKind.DiskFull)]
    [InlineData("Downloading the model failed: No such host is known.", SetupErrorKind.Network)]
    [InlineData("Downloading the model failed: SHA256 mismatch for model.gguf", SetupErrorKind.HashMismatch)]
    [InlineData("Preparing the text runtime failed: CUDA_ERROR_NO_DEVICE", SetupErrorKind.GpuDriver)]
    [InlineData("Verifying local chat failed: Only one usage of each socket address (protocol/network address/port) is normally permitted.", SetupErrorKind.PortInUse)]
    [InlineData("Downloading the model failed: Access to the path 'D:\\store\\m.gguf' is denied.", SetupErrorKind.AccessDenied)]
    public void Common_raw_errors_map_to_a_cause_with_a_next_action_in_both_languages(string raw, SetupErrorKind expected)
    {
        Assert.Equal(expected, SetupErrorExplainer.Classify(raw));
        FriendlyError en = SetupErrorExplainer.Explain(raw, italian: false)!;
        FriendlyError it = SetupErrorExplainer.Explain(raw, italian: true)!;
        Assert.NotEqual(en.Cause, it.Cause);
        Assert.False(string.IsNullOrWhiteSpace(en.NextAction));
        Assert.False(string.IsNullOrWhiteSpace(it.NextAction));
        Assert.DoesNotContain("Exception", en.Line);
    }

    [Fact]
    public void Unknown_errors_are_not_given_an_invented_cause_and_details_hide_user_paths()
    {
        Assert.Null(SetupErrorExplainer.Explain("something odd happened", italian: false));
        Assert.Null(SetupErrorExplainer.Explain(null, italian: true));
        string? details = SetupErrorExplainer.RedactForDetails(@"File not found: D:\store\models\a.gguf");
        Assert.DoesNotContain("store", details);
        Assert.Contains("<path>", details);
    }

    [Fact]
    public void Failed_setup_shows_the_friendly_text_and_keeps_the_raw_error_for_the_details_expander()
    {
        var state = new AutoSetupState { Phase = AutoSetupPhase.Failed, Message = "Downloading the model failed: No such host is known." };
        AutoSetupViewModel en = OnboardingAutoSetupPresenter.Present(state, italian: false);
        AutoSetupViewModel it = OnboardingAutoSetupPresenter.Present(state, italian: true);
        Assert.Contains("couldn't reach the internet", en.Detail);
        Assert.DoesNotContain("No such host", en.Detail);
        Assert.Contains("No such host", en.TechnicalDetails);
        Assert.Contains("internet", it.Detail);
        Assert.Contains("riprovare", it.Detail);
        Assert.DoesNotContain("No such host", it.Detail);
    }
}
