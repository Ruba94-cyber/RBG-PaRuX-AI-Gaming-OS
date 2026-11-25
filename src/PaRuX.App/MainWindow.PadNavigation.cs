using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Gamepad operation of the Playnite Console Mode launcher dialog (and the dialogs, drop-downs and on-screen keyboard it opens).
/// This is what remains of the former native Console/TV shell after step 3 of the gaming-removal map: the fullscreen PaRuX
/// presenter, its TV re-anchoring and its Gaming-library pad actions are gone; Console mode is Playnite Fullscreen (with the
/// owner's theme), started by <see cref="TryStartPlayniteConsoleAsync"/>. No mouse/keyboard input is synthesised here.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>True while a sign-in / startup Console decision is being carried out (Playnite Fullscreen handoff).</summary>
    private bool _consoleRoutingInProgress;

    private static string ConsoleText(string en, string it) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

    /// <summary>Gaming-tab footer button: Console mode is Playnite Fullscreen. Same path as the tray and the pad action (the launcher
    /// shows the reason, or Detect / Locate / Install / Skip, when Fullscreen cannot start). Nothing starts while a game is active.</summary>
    private async void PlayniteConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowPlayniteLauncherAsync(PlayniteLauncherAction.Console, gamepad: false, fromRequest: true); }
        catch (Exception ex) { Log.Write("Playnite Console button failed: " + ex.GetType().Name); }
    }

    private void RootGrid_ConsoleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!PlayniteLauncherOpen) return;   // only the Playnite launcher dialog is operated by this pad navigation
        // Gamepad virtual keys (195..218) mean WinUI itself is delivering the pad: the direct reader must stand down.
        if ((int)e.Key is >= 195 and <= 218) _consoleRawPad?.NoteWinUiGamepadKeysSeen();
        ConsoleNavigationAction action = ConsoleNavigationPolicy.Resolve(
            (int)e.Key, GameLibraryBusy, ConsoleOverlayOpen);
        if (ApplyConsoleAction(action)) e.Handled = true;
    }

    /// <summary>
    /// Turns a pad navigation action into UI behaviour for the launcher dialog, shared by WinUI gamepad key events (Xbox-class pads)
    /// and the driverless raw-pad navigator (for example a Stadia pad). Returns true when the action was handled.
    /// </summary>
    private bool ApplyConsoleAction(ConsoleNavigationAction action)
    {
        if (!PlayniteLauncherOpen || action == ConsoleNavigationAction.None) return false;

        // The launcher is a ContentDialog: B closes the top dialog/flyout/drop-down/keyboard, directions stay inside it.
        if (ConsoleOverlayOpen) return HandleConsoleOverlayAction(action);

        FocusNavigationDirection? direction = action switch
        {
            ConsoleNavigationAction.Up => FocusNavigationDirection.Up,
            ConsoleNavigationAction.Down => FocusNavigationDirection.Down,
            ConsoleNavigationAction.Left => FocusNavigationDirection.Left,
            ConsoleNavigationAction.Right => FocusNavigationDirection.Right,
            _ => null
        };
        if (TryAdjustFocusedSlider(action)) return true;
        return direction.HasValue && TryMoveConsoleFocus(direction.Value);
    }

    /// <summary>
    /// Moves the keyboard-style focus one step. The parameterless FocusManager.TryMoveFocus throws in this WinUI 3 desktop window (found
    /// with a real controller), so the search root is given explicitly through the options overload.
    /// </summary>
    private bool TryMoveConsoleFocus(FocusNavigationDirection direction)
    {
        if (ConsoleFocusSearchRoot() is not DependencyObject root) return false;
        var options = new FindNextElementOptions { SearchRoot = root };
        return FocusManager.TryMoveFocus(direction, options);
    }

    // ---- Driverless navigation for raw HID pads (for example Google Stadia) -------------------------------------------------
    // WinUI only turns Xbox-class gamepads into navigation key events. A raw HID pad is read directly while the Playnite launcher is
    // open and no game is running; it never starts a virtual pad and stops the moment a game or Gaming mode takes over.
    private ConsoleRawPadNavigator? _consoleRawPad;

    private void StartConsoleRawPad()
    {
        try
        {
            if (_consoleRawPad == null)
            {
                _consoleRawPad = new ConsoleRawPadNavigator();
                _consoleRawPad.ActionRaised += action => { if (PlayniteLauncherOpen) ApplyConsoleAction(action); };
                _consoleRawPad.AcceptRequested += AcceptFocusedConsoleControl;
                _consoleRawPad.PadKeyRaised += vk => { if (PlayniteLauncherOpen && _keyboardOverlay?.IsOpen == true) _keyboardOverlay.HandlePadKey(vk); };
            }
            _consoleRawPad.OverlayOpen = () => ConsoleOverlayOpen;
            _consoleRawPad.Start(DispatcherQueue, () => GameLibraryBusy);
        }
        catch (Exception ex)
        {
            Log.Write("Console raw-pad navigation could not start: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void StopConsoleRawPad()
    {
        try { _consoleRawPad?.Stop(); }
        catch (Exception ex) { Log.Write("Console raw-pad navigation stop failed: " + ex.GetType().Name); }
    }

    /// <summary>Accept (A) from a raw pad: invoke the focused control through UI Automation, exactly what a screen reader's Invoke does.
    /// No mouse or keyboard input is synthesized.</summary>
    private void AcceptFocusedConsoleControl()
    {
        if (!PlayniteLauncherOpen || RootGrid.XamlRoot is null) return;
        if (_keyboardOverlay?.IsOpen != true && TopInteractivePopup() is { } popup && RescueOverlayFocus(popup)) return;
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not UIElement focused) return;
        try
        {
            if (TryAcceptSpecialConsoleControl(focused)) return;
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(focused);
            if (peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
                is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke) { invoke.Invoke(); return; }
            if (peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)
                is Microsoft.UI.Xaml.Automation.Provider.IToggleProvider toggle) { toggle.Toggle(); return; }
            if (peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)
                is Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider expand)
            {
                if (expand.ExpandCollapseState == Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed) expand.Expand(); else expand.Collapse();
                return;
            }
            if (peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.SelectionItem)
                is Microsoft.UI.Xaml.Automation.Provider.ISelectionItemProvider item) item.Select();
        }
        catch (Exception ex)
        {
            Log.Write("Console raw-pad accept failed: " + ex.GetType().Name);
        }
    }
}
