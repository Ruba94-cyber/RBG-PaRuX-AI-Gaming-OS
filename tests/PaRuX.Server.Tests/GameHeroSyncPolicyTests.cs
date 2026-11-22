using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The pure rules by which the Games hero follows the focused / hovered tile (slice 3).</summary>
public sealed class GameHeroSyncPolicyTests
{
    private static GameHeroSyncPolicy NewPolicy(string? baseKey = "base")
    {
        var policy = new GameHeroSyncPolicy(debounceMs: 120, revertMs: 1500);
        policy.Reset(baseKey);
        return policy;
    }

    [Fact]
    public void Rapid_focus_changes_debounce_hero_updates_to_latest_target()
    {
        GameHeroSyncPolicy policy = NewPolicy();
        // Holding D-pad right across 20 tiles, one every 30 ms.
        for (int i = 0; i < 20; i++)
        {
            policy.Enter(GameHeroSource.Focus, "game" + i, userInitiated: true, now: 1000 + i * 30L);
            Assert.Equal(GameHeroSyncKind.None, policy.Poll(1000 + i * 30L).Kind);   // nothing is decoded while the key is held
        }
        long last = 1000 + 19 * 30L;
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(last + 119).Kind);
        GameHeroSyncDecision decision = policy.Poll(last + 120);
        Assert.Equal(GameHeroSyncKind.Preview, decision.Kind);
        Assert.Equal("game19", decision.Key);   // only the final tile is ever shown: intermediate decodes are skipped
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(last + 5000).Kind);   // and it is reported exactly once
    }

    [Fact]
    public void Focus_the_user_did_not_cause_never_changes_the_hero()
    {
        GameHeroSyncPolicy policy = NewPolicy();
        policy.Enter(GameHeroSource.Focus, "first-tile-focused-by-console-home", userInitiated: false, now: 0);
        Assert.Null(policy.DueAt);
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(10_000).Kind);
        Assert.Null(policy.Shown);
    }

    [Fact]
    public void Pointing_at_the_heros_own_title_is_not_a_preview()
    {
        GameHeroSyncPolicy policy = NewPolicy("base");
        policy.Enter(GameHeroSource.Hover, "base", true, 0);
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(10_000).Kind);
    }

    [Fact]
    public void Leaving_the_tiles_returns_to_the_last_played_title_after_a_delay_and_re_entering_cancels_it()
    {
        GameHeroSyncPolicy policy = NewPolicy();
        policy.Enter(GameHeroSource.Focus, "a", true, 0);
        Assert.Equal("a", policy.Poll(120).Key);
        policy.Leave(GameHeroSource.Focus, 200);
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(1699).Kind);
        policy.Enter(GameHeroSource.Focus, "a", true, 1600);   // focus came back to the same tile: nothing to change
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(5000).Kind);
        policy.Leave(GameHeroSource.Focus, 6000);
        GameHeroSyncDecision revert = policy.Poll(7500);
        Assert.Equal(GameHeroSyncKind.Revert, revert.Kind);
        Assert.Null(revert.Key);
        Assert.Null(policy.Shown);
    }

    [Fact]
    public void Hero_keeps_its_preview_while_the_pointer_or_focus_is_on_the_hero()
    {
        GameHeroSyncPolicy policy = NewPolicy();
        policy.Enter(GameHeroSource.Focus, "a", true, 0);
        Assert.Equal("a", policy.Poll(120).Key);
        policy.Hold(true, 300);                            // the user moved to the Play button of the previewed hero
        policy.Leave(GameHeroSource.Focus, 300);
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(60_000).Kind);
        Assert.Equal("a", policy.Shown);
        policy.Hold(false, 61_000);                        // and away again: now the hero may return
        Assert.Equal(GameHeroSyncKind.Revert, policy.Poll(62_500).Kind);
    }

    [Fact]
    public void Latest_of_focus_and_hover_wins_and_hover_exit_falls_back_to_the_focused_tile()
    {
        GameHeroSyncPolicy policy = NewPolicy();
        policy.Enter(GameHeroSource.Focus, "focused", true, 0);
        policy.Enter(GameHeroSource.Hover, "hovered", true, 50);
        Assert.Equal("hovered", policy.Poll(170).Key);
        policy.Leave(GameHeroSource.Hover, 400);
        GameHeroSyncDecision back = policy.Poll(520);
        Assert.Equal(GameHeroSyncKind.Preview, back.Kind);
        Assert.Equal("focused", back.Key);
    }

    [Fact]
    public void Reset_forgets_previews_and_pending_requests_when_the_hero_title_changes()
    {
        GameHeroSyncPolicy policy = NewPolicy();
        policy.Enter(GameHeroSource.Focus, "a", true, 0);
        policy.Reset("another-base");
        Assert.Null(policy.DueAt);
        Assert.Equal(GameHeroSyncKind.None, policy.Poll(10_000).Kind);
    }
}
