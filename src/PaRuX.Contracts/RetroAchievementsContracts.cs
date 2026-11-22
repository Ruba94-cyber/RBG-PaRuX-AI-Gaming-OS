namespace PaRuX.Contracts;

/// <summary>Owner-supplied RetroAchievements Web API credentials (the owner's own user name and Web API key). The key is never returned after it is saved.</summary>
public sealed class RetroAchievementsCredentialsRequest
{
    public string UserName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class RetroAchievementsCredentialsStatus
{
    public bool Configured { get; set; }
    public string UserName { get; set; } = string.Empty;
}

/// <summary>One achievement of any source (Steam or RetroAchievements) in the shape the unlock tracker works on.</summary>
public sealed class AchievementEntryDto
{
    /// <summary>Stable id inside the game: the Steam api name or the RetroAchievements achievement id.</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Achieved { get; set; }
    public DateTime? UnlockedUtc { get; set; }
}

public sealed class RetroAchievementsResult
{
    public int GameId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string ConsoleName { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Unlocked { get; set; }
    public List<AchievementEntryDto> Achievements { get; set; } = new();
    /// <summary>Empty on success; otherwise a plain reason (no credentials, Internet off, unknown console or game, refused key, no achievements).</summary>
    public string Problem { get; set; } = string.Empty;
}

/// <summary>
/// Pure comparison used by the unlock toast: which achievements are unlocked now that were not in the previous snapshot.
/// The first snapshot of a session is only a baseline, so nothing already earned is announced.
/// </summary>
public static class AchievementUnlockDiff
{
    /// <summary>Keys of the achievements that are unlocked in <paramref name="current"/>.</summary>
    public static HashSet<string> UnlockedKeys(IEnumerable<AchievementEntryDto> current) =>
        new(current.Where(a => a.Achieved && !string.IsNullOrEmpty(a.Key)).Select(a => a.Key), StringComparer.Ordinal);

    /// <summary>Achievements unlocked in <paramref name="current"/> whose key is not in <paramref name="knownUnlocked"/>; ordered by unlock time.</summary>
    public static IReadOnlyList<AchievementEntryDto> NewlyUnlocked(ISet<string> knownUnlocked, IEnumerable<AchievementEntryDto> current) =>
        current.Where(a => a.Achieved && !string.IsNullOrEmpty(a.Key) && !knownUnlocked.Contains(a.Key))
            .OrderBy(a => a.UnlockedUtc ?? DateTime.MaxValue).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();

    public static AchievementEntryDto FromSteam(SteamAchievementDto a) => new()
    { Key = a.ApiName, Name = a.Name, Description = a.Description, Achieved = a.Achieved, UnlockedUtc = a.UnlockedUtc };
}
