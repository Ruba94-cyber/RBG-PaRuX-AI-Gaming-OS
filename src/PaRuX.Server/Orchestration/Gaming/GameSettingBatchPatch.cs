using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Produces one bounded, reversible config proposal for multiple existing
/// gameplay settings in the SAME file. No IO, new keys, code execution or game
/// format assumptions. Every edit must succeed before a preview is published.
/// </summary>
internal static class GameSettingBatchPatch
{
    internal const int MinEdits = 2;
    internal const int MaxEdits = 8;

    internal static string Apply(string original, string relativePath, IReadOnlyList<GameModSettingEdit>? edits)
    {
        if (edits is null || edits.Count is < MinEdits or > MaxEdits)
            throw new ArgumentException("A gameplay preset must change between two and eight existing settings.");

        string extension = Path.GetExtension(relativePath ?? "");
        bool json = extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
        bool ini = extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase);
        if (!json && !ini)
            throw new ArgumentException("Gameplay presets require a pre-existing INI, CFG or JSON file.");

        // Reject same-target edits even if names differ only in case (fail closed
        // for games that load setting names with inconsistent casing).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string current = original;
        foreach (GameModSettingEdit edit in edits)
        {
            if (edit is null || edit.Section is null || edit.Key is null || edit.Value is null ||
                edit.Section.Length > 80 || edit.Key.Length is < 1 or > 80 || edit.Value.Length > 256)
                throw new ArgumentException("Every gameplay edit requires a bounded section, key and value.");
            string identity = edit.Section + "\u001f" + edit.Key;
            if (!seen.Add(identity))
                throw new ArgumentException("The gameplay preset changes the same property more than once.");
            current = json
                ? GameJsonSettingPatch.Apply(current, edit.Section, edit.Key, edit.Value)
                : GameIniSettingPatch.Apply(current, edit.Section, edit.Key, edit.Value);
        }

        return current;
    }
}
