using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class UncensoredFallbackGateTests
{
    private static AiProfileStatus StandardEnabled() => new()
    {
        TextProfile = "STANDARD",
        AutoUncensoredFallback = true,
        GamingMode = false
    };

    [Fact]
    public void Disables_fallback_when_uncensored_model_file_is_missing()
    {
        Assert.False(ChatProxyService.CanConsiderUncensoredFallback(
            StandardEnabled(),
            manualModelLock: false,
            usesExternalProvider: false,
            activeModelPath: @"C:\models\qwen3-4b.gguf",
            uncensoredModelPath: @"C:\models\uncensored.gguf",
            fileExists: _ => false));
    }

    [Fact]
    public void Disables_fallback_when_active_model_is_the_same_file_as_uncensored()
    {
        Assert.False(ChatProxyService.CanConsiderUncensoredFallback(
            StandardEnabled(),
            manualModelLock: false,
            usesExternalProvider: false,
            activeModelPath: @"C:\models\shared.gguf",
            uncensoredModelPath: @"C:\models\shared.gguf",
            fileExists: _ => true));
    }

    [Fact]
    public void Enables_fallback_only_when_distinct_uncensored_model_exists_on_standard_unpinned_local()
    {
        Assert.True(ChatProxyService.CanConsiderUncensoredFallback(
            StandardEnabled(),
            manualModelLock: false,
            usesExternalProvider: false,
            activeModelPath: @"C:\models\standard.gguf",
            uncensoredModelPath: @"C:\models\uncensored.gguf",
            fileExists: _ => true));
    }

    [Fact]
    public void Disables_fallback_for_manual_lock_gaming_external_or_disabled_toggle()
    {
        Assert.False(ChatProxyService.CanConsiderUncensoredFallback(
            StandardEnabled(), manualModelLock: true, usesExternalProvider: false,
            activeModelPath: @"C:\models\standard.gguf", uncensoredModelPath: @"C:\models\uncensored.gguf", fileExists: _ => true));
        Assert.False(ChatProxyService.CanConsiderUncensoredFallback(
            StandardEnabled(), manualModelLock: false, usesExternalProvider: true,
            activeModelPath: @"C:\models\standard.gguf", uncensoredModelPath: @"C:\models\uncensored.gguf", fileExists: _ => true));

        var gaming = StandardEnabled();
        gaming.GamingMode = true;
        Assert.False(ChatProxyService.CanConsiderUncensoredFallback(
            gaming, manualModelLock: false, usesExternalProvider: false,
            activeModelPath: @"C:\models\standard.gguf", uncensoredModelPath: @"C:\models\uncensored.gguf", fileExists: _ => true));
    }
}
