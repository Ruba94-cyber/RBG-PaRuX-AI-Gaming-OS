namespace PaRuX.Contracts;

/// <summary>
/// The owner's list of games the controller "pause / resume" button never pauses (some games misbehave when frozen). It only ADDS
/// a refusal: pausing is still limited to a game PaRuX launched and still refused for anti-cheat processes. Resuming a game that is
/// already paused is never blocked by this list.
/// </summary>
public static class PadFreezeExclusion
{
    public const int MaxEntries = 64;
    public const int MaxLength = 80;

    public static List<string> Normalize(IEnumerable<string?>? raw)
    {
        var list = new List<string>();
        foreach (string? r in raw ?? Array.Empty<string?>())
        {
            string v = new string((r ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (v.Length == 0) continue;
            if (v.Length > MaxLength) v = v[..MaxLength];
            if (list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(v);
            if (list.Count >= MaxEntries) break;
        }
        return list;
    }

    public static bool IsExcluded(IEnumerable<string?>? excluded, string? gameName, string? processName)
    {
        string[] names = new[] { gameName, processName }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Strip).ToArray();
        if (names.Length == 0) return false;
        return Normalize(excluded).Any(e => names.Any(n => string.Equals(n, Strip(e), StringComparison.OrdinalIgnoreCase)));
    }

    private static string Strip(string? v)
    {
        string t = (v ?? string.Empty).Trim();
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[..^4] : t;
    }
}
