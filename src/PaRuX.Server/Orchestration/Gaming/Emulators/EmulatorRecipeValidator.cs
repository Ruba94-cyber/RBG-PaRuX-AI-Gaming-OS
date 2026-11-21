using System.Text.RegularExpressions;

namespace PaRuX.Server.Orchestration.Gaming.Emulators;

/// <summary>
/// Static checks of an emulator launch recipe. They cannot prove an emulator build accepts the arguments (that is what the
/// "unverified" status is for), but they catch every mistake a recipe can make on its own: an unknown token, a recipe with no game
/// path, a display mode advertised but never inserted, a core-less recipe that needs a core, or odd characters in a literal.
/// </summary>
public static class EmulatorRecipeValidator
{
    private static readonly HashSet<string> Tokens = new(StringComparer.Ordinal)
    {
        "{rom}", "{romdir}", "{romname}", "{core}", "{fullscreen}", "{input-profile}", "{controller-ui}", "{extra}"
    };

    private static readonly Regex Placeholder = new(@"\{[^{}]*\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex IdShape = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<string> Problems(EmulatorDefinition d)
    {
        var problems = new List<string>();
        void Add(string text) => problems.Add(d.Id + ": " + text);

        if (!IdShape.IsMatch(d.Id)) Add("the id must be lowercase letters, digits and hyphens");
        if (string.IsNullOrWhiteSpace(d.Name)) Add("no name");
        if (string.IsNullOrWhiteSpace(d.Platform)) Add("no platform");

        if (d.ExecutableNames.Count == 0) Add("no executable name to detect");
        foreach (string exe in d.ExecutableNames)
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || exe.Any(char.IsControl))
                Add("executable name '" + exe + "' must be a plain .exe file name");

        if (d.GameExtensions.Count == 0 && d.GameFileNames.Count == 0) Add("no game extension or file name to match games");
        foreach (string ext in d.GameExtensions)
            if (ext.Length < 2 || ext[0] != '.' || ext != ext.ToLowerInvariant() || ext.Any(c => char.IsWhiteSpace(c) || c is '*' or '?' or '\\' or '/'))
                Add("game extension '" + ext + "' must look like .iso");
        foreach (string name in d.GameFileNames)
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '\\', '/', '*', '?' }) >= 0)
                Add("game file name '" + name + "' must be a plain file name");

        int pathTokens = 0, fullscreenTokens = 0;
        bool core = false;
        foreach (string token in d.Template)
        {
            if (token.Any(char.IsControl)) Add("a template argument has a control character");
            foreach (Match m in Placeholder.Matches(token))
            {
                if (!Tokens.Contains(m.Value) && m.Value is not ("{rom}" or "{romdir}" or "{romname}")) Add("unknown placeholder " + m.Value);
                if (m.Value is "{rom}" or "{romdir}" or "{romname}") pathTokens++;
                if (m.Value == "{core}") core = true;
            }
            if (token == "{fullscreen}") fullscreenTokens++;
            if (token.Contains("{fullscreen}", StringComparison.Ordinal) && token != "{fullscreen}") Add("{fullscreen} must be an argument of its own");
            if (token.Contains("{extra}", StringComparison.Ordinal) && token != "{extra}") Add("{extra} must be an argument of its own");
        }
        if (pathTokens == 0) Add("the recipe never passes the game");
        if (fullscreenTokens > 1) Add("{fullscreen} appears more than once");
        if (core != d.RequiresCore) Add(d.RequiresCore ? "needs a core but the recipe has no {core}" : "the recipe has {core} but RequiresCore is false");

        foreach (IReadOnlyList<string>? list in new[] { d.FullscreenArguments, d.WindowedArguments })
        {
            if (list is null) continue;
            if (list.Count == 0) Add("an empty display-mode list should be null");
            if (list.Any(a => Placeholder.IsMatch(a) || a.Any(char.IsControl))) Add("display-mode arguments must be literal");
            if (fullscreenTokens == 0) Add("a display mode is advertised but the recipe has no {fullscreen}");
        }
        if (!new[] { "unverified", "verified" }.Contains(d.TemplateStatus)) Add("template status must be unverified or verified");
        return problems;
    }
}
