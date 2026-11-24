namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// One pure rule for operations that write into a game's own folders (XInput shim, save restore):
/// refused while PaRuX Gaming owns the machine OR while Playnite reports a running game.
/// </summary>
internal static class GameFileMutationPolicy
{
    internal const string GamingMessage = "Return to AI before changing a game's files.";
    internal const string GameRunningMessage = "A game is running. Close it before changing a game's files.";

    /// <summary>Null when allowed; otherwise the user-facing reason the operation is refused.</summary>
    internal static string? Refusal(bool gamingMode, bool playniteGameRunning) =>
        gamingMode ? GamingMessage : playniteGameRunning ? GameRunningMessage : null;
}
