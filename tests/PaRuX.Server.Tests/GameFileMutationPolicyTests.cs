using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameFileMutationPolicyTests
{
    [Fact]
    public void Allowed_only_when_neither_gaming_nor_a_playnite_game_is_active() =>
        Assert.Null(GameFileMutationPolicy.Refusal(gamingMode: false, playniteGameRunning: false));

    [Fact]
    public void Refused_while_a_playnite_game_is_running()
    {
        string? reason = GameFileMutationPolicy.Refusal(gamingMode: false, playniteGameRunning: true);
        Assert.Equal(GameFileMutationPolicy.GameRunningMessage, reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gaming_mode_always_refuses(bool playniteRunning) =>
        Assert.Equal(GameFileMutationPolicy.GamingMessage,
            GameFileMutationPolicy.Refusal(gamingMode: true, playniteGameRunning: playniteRunning));
}
