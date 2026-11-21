using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Read-only RetroAchievements progress for emulated library games, through the owner's own user name and Web API key (the same
/// data the RetroAchievements site and other launchers show). User-initiated: nothing is requested until the owner saves
/// credentials AND the Internet switch is on; the key is stored encrypted, never returned and never audited. Only
/// retroachievements.org is contacted, over https, without redirects. A game is matched by an explicit RetroAchievements game id
/// or, when none is given, by its exact (normalised) title inside the console list that matches the emulator platform.
/// </summary>
public sealed class RetroAchievementsService
{
    internal const string Host = "retroachievements.org";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();
    private readonly string _stateDirectory;
    private readonly string _path;
    private readonly Func<bool> _internetEnabled;
    private readonly HttpClient _http;
    private readonly Dictionary<int, (DateTime FetchedUtc, List<(int Id, string Title)> Games)> _gameLists = new();

    private sealed class Stored { public string UserName { get; set; } = string.Empty; public string ProtectedKey { get; set; } = string.Empty; }

    public RetroAchievementsService(string stateDirectory, Func<bool> internetEnabled, HttpMessageHandler? handler = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _path = Path.Combine(_stateDirectory, "retroachievements-credentials.json");
        _internetEnabled = internetEnabled;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) })
        { Timeout = TimeSpan.FromSeconds(20) };
    }

    private ISecretProtector Protector => SecretProtection.For(_stateDirectory);

    public RetroAchievementsCredentialsStatus Status()
    {
        lock (Gate) { Stored? s = Read(); return new RetroAchievementsCredentialsStatus { Configured = s is not null, UserName = s?.UserName ?? string.Empty }; }
    }

    /// <returns>Null on success, otherwise the reason.</returns>
    public string? Save(RetroAchievementsCredentialsRequest request)
    {
        string name = (request?.UserName ?? string.Empty).Trim();
        string key = (request?.ApiKey ?? string.Empty).Trim();
        if (name.Length is < 2 or > 30 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            return "A RetroAchievements user name is 2 to 30 letters, digits, dot, dash or underscore.";
        if (key.Length is < 16 or > 64 || !key.All(char.IsAsciiLetterOrDigit))
            return "A RetroAchievements Web API key is 16 to 64 letters and digits (copy it from your RetroAchievements settings page).";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Stored { UserName = name, ProtectedKey = Protector.Protect("retroachievements-api-key", key) }, Json));
                File.Move(temp, _path, overwrite: true);
                _gameLists.Clear();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The credentials could not be saved."; }
        }
    }

    public bool Clear()
    {
        lock (Gate)
        {
            _gameLists.Clear();
            if (!File.Exists(_path)) return false;
            try { File.Delete(_path); return true; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// Progress of the owner on one game. <paramref name="gameId"/> &gt; 0 wins; otherwise <paramref name="title"/> is matched inside
    /// the console list(s) of <paramref name="platform"/>.
    /// </summary>
    public async Task<RetroAchievementsResult> GetAsync(int gameId, string? title, string? platform, CancellationToken ct = default)
    {
        var result = new RetroAchievementsResult { GameId = gameId };
        Stored? creds; lock (Gate) creds = Read();
        if (creds is null) { result.Problem = "Save your RetroAchievements user name and Web API key first."; return result; }
        if (!_internetEnabled()) { result.Problem = "Turn the Internet switch on to read RetroAchievements."; return result; }
        string? key = Protector.Unprotect("retroachievements-api-key", creds.ProtectedKey);
        if (key is null) { result.Problem = "The saved RetroAchievements key can no longer be read. Save it again."; return result; }
        if (gameId < 0) { result.Problem = "Not a valid RetroAchievements game id."; return result; }

        try
        {
            if (gameId == 0)
            {
                string cleanTitle = (title ?? string.Empty).Trim();
                if (cleanTitle.Length == 0) { result.Problem = "Enter the RetroAchievements game id (the number at the end of the game's page address)."; return result; }
                IReadOnlyList<int> consoles = RetroAchievementsConsoles.ForPlatform(platform);
                if (consoles.Count == 0)
                {
                    result.Problem = "No RetroAchievements console is known for this platform. Enter the RetroAchievements game id instead.";
                    return result;
                }
                gameId = await ResolveAsync(creds.UserName, key, consoles, cleanTitle, ct).ConfigureAwait(false);
                if (gameId <= 0)
                {
                    result.Problem = "RetroAchievements has no game with exactly this title on this console. Enter its game id instead.";
                    return result;
                }
                result.GameId = gameId;
            }

            using JsonDocument doc = await GetJsonAsync("API_GetGameInfoAndUserProgress.php", creds.UserName, key,
                "&u=" + Uri.EscapeDataString(creds.UserName) + "&g=" + gameId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { result.Problem = "RetroAchievements answered in an unexpected format."; return result; }
            if (root.TryGetProperty("Error", out JsonElement err) && err.ValueKind == JsonValueKind.String) { result.Problem = Clip(err.GetString()!); return result; }
            result.GameName = Str(root, "Title");
            result.ConsoleName = Str(root, "ConsoleName");
            if (root.TryGetProperty("Achievements", out JsonElement list) && list.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in list.EnumerateObject().Take(2000))
                {
                    JsonElement a = p.Value;
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    DateTime? earned = ParseDate(Str(a, "DateEarnedHardcore")) ?? ParseDate(Str(a, "DateEarned"));
                    result.Achievements.Add(new AchievementEntryDto
                    {
                        Key = p.Name.Length <= 40 ? p.Name : p.Name[..40],
                        Name = Str(a, "Title"), Description = Str(a, "Description"),
                        Achieved = earned is not null, UnlockedUtc = earned,
                    });
                }
            }
            result.Total = result.Achievements.Count;
            result.Unlocked = result.Achievements.Count(a => a.Achieved);
            if (result.Total == 0) result.Problem = "RetroAchievements lists no achievements for this game.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        { result.Problem = "RetroAchievements refused the user name or key."; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        { result.Problem = "Could not reach RetroAchievements or read its answer."; }
        return result;
    }

    private async Task<int> ResolveAsync(string user, string key, IReadOnlyList<int> consoles, string title, CancellationToken ct)
    {
        string wanted = NormalizeTitle(title);
        if (wanted.Length == 0) return 0;
        foreach (int console in consoles.Take(4))
        {
            List<(int Id, string Title)> games = await GameListAsync(user, key, console, ct).ConfigureAwait(false);
            foreach ((int id, string name) in games)
                foreach (string variant in name.Split(" | ", StringSplitOptions.RemoveEmptyEntries))
                    if (NormalizeTitle(variant) == wanted) return id;
        }
        return 0;
    }

    private async Task<List<(int Id, string Title)>> GameListAsync(string user, string key, int console, CancellationToken ct)
    {
        lock (Gate)
            if (_gameLists.TryGetValue(console, out var cached) && DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromHours(1)) return cached.Games;
        using JsonDocument doc = await GetJsonAsync("API_GetGameList.php", user, key,
            "&i=" + console.ToString(CultureInfo.InvariantCulture) + "&f=1", ct, maxBytes: 16 * 1024 * 1024).ConfigureAwait(false);
        var games = new List<(int, string)>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (JsonElement g in doc.RootElement.EnumerateArray().Take(30000))
                if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("ID", out JsonElement id) && id.TryGetInt32(out int gid) && gid > 0)
                    games.Add((gid, Str(g, "Title")));
        lock (Gate) _gameLists[console] = (DateTime.UtcNow, games);
        return games;
    }

    /// <summary>Lower-case letters and digits only, so "Super Mario Bros. 3" and "super mario bros 3" match.</summary>
    internal static string NormalizeTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime d) ? d : null;

    /// <summary>Real RetroAchievements, always, except a loopback fake while PARUX_DATA_ROOT marks an isolated test profile (PARUX_RA_API_BASE_TEST).</summary>
    internal static string BaseUrl()
    {
        string? over = Environment.GetEnvironmentVariable("PARUX_RA_API_BASE_TEST");
        if (!string.IsNullOrWhiteSpace(over)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARUX_DATA_ROOT"))
            && Uri.TryCreate(over, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host == "127.0.0.1" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(uri.UserInfo))
            return uri.GetLeftPart(UriPartial.Authority);
        return "https://" + Host;
    }

    private async Task<JsonDocument> GetJsonAsync(string endpoint, string user, string key, string extraQuery, CancellationToken ct, int maxBytes = 4 * 1024 * 1024)
    {
        string url = BaseUrl() + "/API/" + endpoint + "?z=" + Uri.EscapeDataString(user) + "&y=" + Uri.EscapeDataString(key) + extraQuery;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("RetroAchievements answered " + (int)response.StatusCode, null, response.StatusCode);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > maxBytes) throw new JsonException("Answer too large.");
        return JsonDocument.Parse(bytes);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()!) : string.Empty;

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300];

    private Stored? Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}

/// <summary>Maps an emulator platform label to the RetroAchievements console ids worth searching (empty when RetroAchievements has none).</summary>
public static class RetroAchievementsConsoles
{
    private static readonly (string Needle, int Id)[] Map =
    {
        ("playstation portable", 41), ("playstation 2", 21), ("playstation 3", 0), ("playstation 4", 0), ("playstation", 12),
        ("game boy advance", 5), ("game boy color", 6), ("game boy", 4),
        ("mega drive", 1), ("genesis", 1), ("master system", 11), ("game gear", 15), ("saturn", 39), ("dreamcast", 40), ("sega cd", 9), ("32x", 10),
        ("nintendo 64", 2), ("super nintendo", 3), ("snes", 3), ("nintendo entertainment", 7), ("famicom", 7), ("game cube", 16), ("gamecube", 16), ("nintendo ds", 18), ("nes", 7),
        ("pc engine", 8), ("turbografx", 8), ("atari 2600", 25), ("atari lynx", 13), ("neo geo pocket", 14), ("arcade", 27),
    };

    public static IReadOnlyList<int> ForPlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return Array.Empty<int>();
        string p = platform.ToLowerInvariant();
        var found = new List<int>();
        // A composite label ("Game Boy / Game Boy Color / Game Boy Advance") lists every console of its parts, most specific first.
        foreach (string part in p.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            foreach ((string needle, int id) in Map)
                if (part.Contains(needle, StringComparison.Ordinal))
                {
                    if (id > 0 && !found.Contains(id)) found.Add(id);
                    break;   // most specific needle of this part wins (the map is ordered specific first)
                }
        return found;
    }
}
