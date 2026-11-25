using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using PaRuX.Views;

namespace PaRuX;

/// <summary>
/// Hosts the "Playnite Console Mode" launcher surface: from the tray, from Settings, from a command-line argument
/// (--playnite-console, --playnite-desktop, --streaming, --playnite-settings, --playnite-launcher), from a shortcut or Jump List task, or
/// from another PaRuX process (single-instance request). One launcher at a time; a request that arrives while it is open runs inside it.
/// The surface lives in a ContentDialog, so the existing Console/TV pad navigation (and the raw-pad reader for pads WinUI does not drive)
/// operates it: directions move, A accepts, B goes back or closes.
/// </summary>
public sealed partial class MainWindow
{
    private PlayniteLauncherSurface? _plSurface;
    private ContentDialog? _plDialog;

    /// <summary>True while the launcher is what the pad is operating (so the Console navigation code also acts outside fullscreen Console mode).</summary>
    private bool PlayniteLauncherOpen => _plSurface is not null;

    internal void ShowPlayniteLauncher(PlayniteLauncherAction action, bool gamepad = false, bool fromRequest = false) =>
        _ = ShowPlayniteLauncherAsync(action, gamepad, fromRequest);

    internal async Task ShowPlayniteLauncherAsync(PlayniteLauncherAction action, bool gamepad, bool fromRequest)
    {
        try
        {
            if (App.IsShuttingDown || CorporateMode.IsActive) return;
            if (action == PlayniteLauncherAction.None) action = PlayniteLauncherAction.Open;
            // A direct controller, tray shortcut or second-instance request
            // MUST NOT move PaRuX over a running game before Core can refuse
            // the actual Playnite start. Reject here before PadBringForward.
            if (PlayniteLauncherForegroundPolicy.RequiresGamePreflight(action))
            {
                if (GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive())
                {
                    PadToast(PadT("Finish the active game before opening another Playnite mode.",
                        "Termina il gioco attivo prima di aprire un'altra modalità Playnite."));
                    return;
                }
                GamingCallResult<PlayniteStatusDto> before = await PlayniteClient.Current.GetStatusAsync();
                if (!before.Succeeded ||
                    !PlayniteLauncherForegroundPolicy.MayOpen(action,
                        GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive(), before.Value))
                {
                    PadToast(PadT("Cannot safely open Playnite while a game is active or its status is unknown.",
                        "Impossibile aprire Playnite in sicurezza mentre un gioco è attivo o lo stato è sconosciuto."));
                    return;
                }
            }
            // Explicit Console request (argument, shortcut, pad action, second launch): go straight to Playnite Fullscreen like the owner's
            // Console Mode shortcut, without a dialog. If it cannot start, the launcher below shows the reason and the setup.
            if (action == PlayniteLauncherAction.Console && fromRequest && _plSurface is null && await TryDirectPlayniteConsoleAsync())
                return;
            PadBringForward();
            if (_plSurface is { } open)
            {
                await open.RunAsync(action, fromRequest);   // already open: the request runs inside it
                return;
            }
            if (RootGrid.XamlRoot is null) return;

            bool settingsOnly = action == PlayniteLauncherAction.Settings && fromRequest;
            bool large = gamepad || _consoleModeActive;
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = PadT("Playnite Console Mode", "Playnite Modalità Console"),
                CloseButtonText = PadT("Close", "Chiudi"),
                DefaultButton = ContentDialogButton.None
            };
            var surface = new PlayniteLauncherSurface(DispatcherQueue, large, settingsOnly,
                () => { try { dialog.Hide(); } catch (Exception ex) { Log.Write("Playnite launcher close failed: " + ex.GetType().Name); } },
                onSubPage => dialog.CloseButtonText = onSubPage ? PadT("Back", "Indietro") : PadT("Close", "Chiudi"));
            dialog.Content = new ScrollViewer { Content = surface.Root, MaxHeight = large ? 720 : 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "PlayniteLauncher_Dialog");
            // B / Escape / the close button: on Settings or Shortcuts they go back one page first; only on the main page they close.
            dialog.Closing += (_, args) => { if (args.Result == ContentDialogResult.None && surface.TryBack()) args.Cancel = true; };
            dialog.Opened += (_, _) => (Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(surface.Root) as Control)?.Focus(FocusState.Programmatic);

            _plSurface = surface;
            _plDialog = dialog;
            if (!_consoleModeActive) StartConsoleRawPad();   // pads WinUI does not drive (for example Stadia) can operate the launcher too
            surface.Start();
            _ = surface.RunAsync(action, fromRequest);
            try { await dialog.ShowAsync(); }
            catch (InvalidOperationException)
            {
                // Another dialog is already open: never stack two; say so instead of failing silently.
                PadToast(PadT("Close the open dialog first, then open Console Mode again.", "Chiudi prima la finestra aperta, poi riapri la modalità Console."));
            }
            finally
            {
                surface.Dispose();
                _plSurface = null;
                _plDialog = null;
                if (!_consoleModeActive) StopConsoleRawPad();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Playnite launcher failed: " + ex.GetType().Name + ": " + ex.Message);
            _plSurface = null;
            _plDialog = null;
        }
    }

    /// <summary>
    /// Start-up resolution (arguments, held Ctrl/Shift) for this process. An explicit launcher request replaces the automatic Console/TV
    /// start and the Playnite-with-PaRuX start for this launch only; nothing else about the app changes. True when the launcher took over.
    /// </summary>
    internal bool ApplyPlayniteLauncherStartup()
    {
        PlayniteLauncherResolution resolution = PlayniteLauncherModeResolver.Resolve(App.StartupLauncherRequest, App.StartupCtrlHeld, App.StartupShiftHeld);
        if (resolution.Action == PlayniteLauncherAction.None) return false;
        Log.Write("Playnite launcher start: " + resolution.Action + " (" + resolution.Reason + ").");
        ShowPlayniteLauncher(resolution.Action, App.StartupLauncherRequest.Gamepad, fromRequest: true);   // not awaited: the rest of start-up goes on behind the open launcher
        return true;
    }
}
