using System;
using System.IO;
using System.Linq;

namespace PaRuX.Services;

/// <summary>What the host knows when a button fires (all read just before running the action).</summary>
public sealed record PadActionContext(bool GamingActive, bool ConsoleMode, bool OnGamingWorkspace);

public sealed record PadActionDecision(bool Allowed, string ReasonEn = "", string ReasonIt = "")
{
    public static readonly PadActionDecision Allow = new(true);
    public string Reason(bool italian) => italian ? ReasonIt : ReasonEn;
}

/// <summary>
/// The Gaming hard-off invariant for controller actions: while a game runs or Gaming mode is active nothing that needs the local AI
/// (mini agent, dictation) and no switch to the AI workspace is allowed; the refusal carries a short reason for the toast.
/// "Open PaRuX menu" is always allowed and never closes, suspends or injects input into a game.
/// </summary>
public static class PadActionPolicy
{
    public static PadActionDecision Decide(PadActionId action, PadActionContext ctx)
    {
        switch (action)
        {
            case PadActionId.MiniAgent:
            case PadActionId.Dictation:
                return ctx.GamingActive
                    ? new PadActionDecision(false, "Gaming is active: the local AI is off, so this action was not started.",
                        "Gaming è attivo: l'IA locale è spenta, l'azione non è stata avviata.")
                    : PadActionDecision.Allow;
            case PadActionId.SwitchWorkspace:
                return ctx.GamingActive && ctx.OnGamingWorkspace
                    ? new PadActionDecision(false, "Gaming is active: the AI workspace stays closed.",
                        "Gaming è attivo: l'area AI resta chiusa.")
                    : PadActionDecision.Allow;
            case PadActionId.LaunchPlayniteConsole:
            case PadActionId.LaunchPlayniteDesktop:
                // A controller command must not launch/focus a frontend over an active game.
                // Playnite's own game lifecycle continues to own the foreground.
                return ctx.GamingActive
                    ? new PadActionDecision(false,
                        "A game is active: close it before opening another Playnite mode.",
                        "Un gioco è attivo: chiudilo prima di aprire un'altra modalità Playnite.")
                    : PadActionDecision.Allow;
            case PadActionId.PauseResumeGame:
                return ctx.GamingActive
                    ? PadActionDecision.Allow
                    : new PadActionDecision(false, "No game started by PaRuX is running.", "Nessun gioco avviato da PaRuX è in esecuzione.");
            default:
                return PadActionDecision.Allow;
        }
    }

    /// <summary>Capture folder bound: a runaway button cannot fill the disk.</summary>
    public const int MaxScreenshotFiles = 2000;

    public static string ScreenshotFileName(DateTime localNow) =>
        "parux-" + localNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".png";

    public static bool ScreenshotFolderHasRoom(string folder)
    {
        try { return !Directory.Exists(folder) || Directory.EnumerateFiles(folder, "*.png").Take(MaxScreenshotFiles).Count() < MaxScreenshotFiles; }
        catch (Exception) { return false; }
    }
}
