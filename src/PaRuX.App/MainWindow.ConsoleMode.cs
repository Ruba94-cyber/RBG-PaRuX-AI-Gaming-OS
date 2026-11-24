using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Console/TV is an opt-in presenter mode of the EXISTING WinUI MainWindow.
/// No second executable, runtime, settings store, virtual display, driver or
/// background controller service is created. UI navigation stays shared.
/// </summary>
public sealed partial class MainWindow
{
    private bool _consoleModeActive;
    private bool _consoleRoutingInProgress;
    private RectInt32? _consolePreviousWindowBounds;
    private DateTime? _lastObservedVerifiedReturnUtc;
    private bool _handlingVerifiedReturn;

    /// <summary>Subscribe to the already-running LocalAI state watcher, not a
    /// second watcher or a timer. Ignore any event already on disk at startup.</summary>
    private void InitializeConsoleVerifiedReturn()
    {
        RememberLatestVerifiedReturn();
        LocalAiController.Current.VerifiedGameReturnChanged += OnVerifiedGameReturnChanged;
    }

    private void RememberLatestVerifiedReturn()
    {
        if (VerifiedGameReturnSignal.TryRead(
                VerifiedGameReturnSignal.FileIn(AppPaths.LocalAiStateDir), out var notice))
            _lastObservedVerifiedReturnUtc = notice?.ExitUtc;
    }

    private void OnVerifiedGameReturnChanged(object? sender, EventArgs args)
    {
        // The FileSystemWatcher callback is a worker thread, not a WinUI thread.
        // It already listens to the exact directory in which Core publishes the
        // atomic verified-return notice. No long-lived polling task is needed.
        // _dispatcher is captured on MainWindow's owning thread in its
        // constructor; do not read a WinUI Window property from the watcher.
        _dispatcher.TryEnqueue(() => _ = HandleVerifiedGameReturnAsync());
    }

    private async Task HandleVerifiedGameReturnAsync()
    {
        if (_handlingVerifiedReturn || App.IsShuttingDown) return;
        if (!VerifiedGameReturnSignal.TryRead(
                VerifiedGameReturnSignal.FileIn(AppPaths.LocalAiStateDir), out var notice)
            || notice is null || _lastObservedVerifiedReturnUtc == notice.ExitUtc)
            return;
        // Consume the signal exactly once, including when the opt-in is off,
        // Console is closed, or user interaction makes focus restoration unsafe.
        _lastObservedVerifiedReturnUtc = notice.ExitUtc;
        if (!_consoleModeActive || !SettingsStore.Current.Data.AutoShowConsoleAfterVerifiedGameExit
            || GameLibraryBusy || LocalAiController.Current.IsGameActive())
            return;

        _handlingVerifiedReturn = true;
        try
        {
            GamingStatusDto? state = await GamingOrchestratorClient.Current.GetStatusAsync();
            if (App.IsShuttingDown || !ConsoleAutoReturnPolicy.CanActivate(
                SettingsStore.Current.Data.AutoShowConsoleAfterVerifiedGameExit,
                _consoleModeActive, GameLibraryBusy,
                LocalAiController.Current.IsGameActive(), state, notice, DateTime.UtcNow))
                return;

            // A TV disconnected during gameplay may have caused Windows to
            // relocate PaRuX to another screen. Never automatically show Home
            // on an unselected PC monitor when the user chose TV-only Console.
            string displayId = SettingsStore.Current.Data.ConsoleTargetDisplayId ?? string.Empty;
            GamingDisplayModeDto? selectedTv = null;
            if (!string.IsNullOrWhiteSpace(displayId))
            {
                var topology = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                if (!topology.Succeeded) return;
                selectedTv = ConsoleDisplaySelectionPolicy.SelectTarget(topology.Value,
                    SettingsStore.Current.Data.ConsoleDisplays, displayId);
                if (selectedTv is null) return;
            }
            if (App.IsShuttingDown || !_consoleModeActive || _consoleRoutingInProgress ||
                LocalAiController.Current.IsGameActive() || GameLibraryBusy)
                return;

            // Windows can relocate our existing fullscreen presenter during a
            // game's exclusive display changes. Re-anchor only THIS PaRuX window
            // to the explicitly selected connected TV before foregrounding it.
            // Never select another monitor, change Windows display settings,
            // affect the owned game, or foreground on an unsuccessful move.
            if (selectedTv is not null)
            {
                if (!TryRestoreConsoleHomeToSelectedTv(selectedTv)) return;
                // The HDMI device can vanish or be reidentified while the
                // presenter switches. Re-check the selected TV immediately
                // before foregrounding; a stale topology is not authorization.
                var updated = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                GamingDisplayModeDto? stillSelected = updated.Succeeded
                    ? ConsoleDisplaySelectionPolicy.SelectTarget(updated.Value,
                        SettingsStore.Current.Data.ConsoleDisplays, displayId)
                    : null;
                if (stillSelected is null || stillSelected.X != selectedTv.X ||
                    stillSelected.Y != selectedTv.Y || stillSelected.Width != selectedTv.Width ||
                    stillSelected.Height != selectedTv.Height)
                    return;
                var finalPosition = AppWindow.Position;
                var finalSize = AppWindow.Size;
                if (!ConsoleHomeRepositionPolicy.IsOnTarget(stillSelected,
                        new ConsoleHomeRepositionPolicy.Bounds(finalPosition.X, finalPosition.Y,
                            finalSize.Width, finalSize.Height)))
                    return;
            }
            if (App.IsShuttingDown || !_consoleModeActive || _consoleRoutingInProgress ||
                LocalAiController.Current.IsGameActive() || GameLibraryBusy)
                return;

            // This is the sole intentionally foregrounding behavior: explicitly
            // opt-in, same Console window, exact Core-confirmed process exit.
            // Windows may still deny foreground activation; never force it with
            // synthetic keyboard input, taskbar hacks or process privileges.
            App.ShowMainWindow();
            await GameLibraryPage.OnConsoleForegroundActivatedAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Verified Console Home return failed: " + ex.GetType().Name);
        }
        finally { _handlingVerifiedReturn = false; }
    }

    /// <summary>
    /// When a game changes Windows fullscreen topology, our one existing
    /// Console window can end up on a different monitor. The selected TV was
    /// already revalidated against Core's current topology by the caller.
    /// No second window, no display-mode mutation and no foreground activation
    /// on an unsuccessful or ambiguous route.
    /// </summary>
    private bool TryRestoreConsoleHomeToSelectedTv(GamingDisplayModeDto tv)
    {
        if (!_consoleModeActive || _consoleRoutingInProgress ||
            AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
            return false;

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (ConsoleHomeRepositionPolicy.IsOnTarget(tv,
                new ConsoleHomeRepositionPolicy.Bounds(position.X, position.Y, size.Width, size.Height)))
            return true;

        ConsoleHomeRepositionPolicy.Bounds? anchor = ConsoleHomeRepositionPolicy.CenteredAnchor(tv);
        if (anchor is null) return false;
        _consoleRoutingInProgress = true;
        try
        {
            // Only AppWindow changes: temporarily leave fullscreen, move the
            // normal window inside the chosen TV, then restore fullscreen.
            // Do not overwrite the saved desktop bounds from EnterConsoleMode.
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppWindow.MoveAndResize(new RectInt32(
                anchor.Value.X, anchor.Value.Y, anchor.Value.Width, anchor.Value.Height));
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            var finalPosition = AppWindow.Position;
            var finalSize = AppWindow.Size;
            return AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen &&
                ConsoleHomeRepositionPolicy.IsOnTarget(tv,
                    new ConsoleHomeRepositionPolicy.Bounds(
                        finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height));
        }
        catch (Exception ex)
        {
            Log.Write("Console TV return route failed: " + ex.GetType().Name);
            // The user may still restore the app manually. Do not call
            // ShowMainWindow() if presenter restoration is unsuccessful.
            try
            {
                if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
                    AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            catch (Exception restoreEx)
            {
                Log.Write("Console fullscreen restore failed: " + restoreEx.GetType().Name);
            }
            _consoleModeActive = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            if (!_consoleModeActive) GameLibraryPageIfCreated?.ApplyConsoleMode(false);
            UpdateConsoleModeButton();
            return false;
        }
        finally { _consoleRoutingInProgress = false; }
    }

    /// <summary>Only the user's activation of the existing PaRuX window triggers this.
    /// No background game-process scan, focus stealing or permanent controller poller.</summary>
    private async void OnConsoleWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_consoleModeActive || args.WindowActivationState == WindowActivationState.Deactivated)
            return;
        try { await GameLibraryPage.OnConsoleForegroundActivatedAsync(); }
        catch (Exception ex) { Log.Write("Console foreground status refresh failed: " + ex.GetType().Name); }
    }

    private async void ConsoleModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_consoleRoutingInProgress) return;
        _consoleRoutingInProgress = true;
        ConsoleModeButton.IsEnabled = false;
        try
        {
            if (_consoleModeActive) ExitConsoleMode();
            else await EnterConsoleModeAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Console window presenter change failed: " + ex.GetType().Name);
            _consoleModeActive = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            if (!_consoleModeActive)
            {
                try { RestorePreviousConsoleDisplay(); }
                catch (Exception restore) { Log.Write("Console display recovery failed: " + restore.GetType().Name); }
            }
            UpdateConsoleModeButton();
        }
        finally
        {
            _consoleRoutingInProgress = false;
            ConsoleModeButton.IsEnabled = true;
        }
    }

    private async Task EnterConsoleModeAsync()
    {
        // A previous session's verified exit is not a future Home-return event.
        RememberLatestVerifiedReturn();
        // A selected TV changes only THIS app window's display. It does not
        // change Windows' monitor arrangement, mirroring, HDR, VRR, or default.
        string targetId = SettingsStore.Current.Data.ConsoleTargetDisplayId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var result = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
            GamingDisplayModeDto? destination = result.Succeeded
                ? ConsoleDisplaySelectionPolicy.SelectTarget(
                    result.Value, SettingsStore.Current.Data.ConsoleDisplays, targetId)
                : null;
            if (destination is null)
            {
                await new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = ConsoleText("TV not available", "TV non disponibile"),
                    Content = ConsoleText(
                        "The chosen Console TV is disconnected, changed its Windows display ID or the local Core cannot read it. Reconnect the TV or update Gaming > Console / TV display routing. PaRuX has not moved windows or changed any display settings.",
                        "La TV scelta non è collegata, ha cambiato identificativo Windows o il Core locale non riesce a rilevarla. Ricollega la TV o aggiorna Gaming > Destinazione Console / TV. PaRuX non ha spostato finestre né modificato le impostazioni degli schermi."),
                    CloseButtonText = "OK"
                }.ShowAsync();
                return;
            }

            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            _consolePreviousWindowBounds = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
            if (AppWindow.Presenter is OverlappedPresenter overlapped)
                overlapped.Restore();
            // Use the same overflow-safe anchor as verified return routing.
            // The fullscreen presenter then uses the monitor owning this window.
            var anchor = ConsoleHomeRepositionPolicy.CenteredAnchor(destination);
            if (anchor is null) throw new InvalidOperationException("Invalid Console TV geometry.");
            AppWindow.MoveAndResize(new RectInt32(
                anchor.Value.X, anchor.Value.Y, anchor.Value.Width, anchor.Value.Height));
        }

        try
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _consoleModeActive = true;
            NavView.IsPaneOpen = true;
            ShowStudioWorkspace(StudioWorkspace.Games);
            GameLibraryPage.ApplyConsoleMode(true);
            GameLibraryPage.OnShown();
            UpdateConsoleModeButton();
            GameLibraryPage.FocusConsoleHome();
            StartConsoleRawPad();
            _ = ApplyConsolePowerProfileAsync();
            _ = ApplyConsoleGameBarBlockAsync();
        }
        catch
        {
            StopConsoleRawPad();
            if (_consoleModeActive)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _consoleModeActive = false;
            }
            RestorePreviousConsoleDisplay();
            throw;
        }
    }

    private static string ConsoleText(string en, string it) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

    private void RestorePreviousConsoleDisplay()
    {
        if (_consolePreviousWindowBounds is not { } original) return;
        if (AppWindow.Presenter is OverlappedPresenter overlapped)
            overlapped.Restore();
        // The monitor the window came from may be gone by now: never restore it off-screen.
        var areas = new System.Collections.Generic.List<ConsoleHomeRepositionPolicy.Bounds>();
        foreach (var display in Microsoft.UI.Windowing.DisplayArea.FindAll())
            areas.Add(new ConsoleHomeRepositionPolicy.Bounds(display.WorkArea.X, display.WorkArea.Y, display.WorkArea.Width, display.WorkArea.Height));
        var primary = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        var safe = ConsoleHomeRepositionPolicy.SafeRestoreBounds(
            new ConsoleHomeRepositionPolicy.Bounds(original.X, original.Y, original.Width, original.Height),
            areas, new ConsoleHomeRepositionPolicy.Bounds(primary.X, primary.Y, primary.Width, primary.Height));
        AppWindow.MoveAndResize(new RectInt32(safe.X, safe.Y, safe.Width, safe.Height));
        _consolePreviousWindowBounds = null;
    }

    private void ExitConsoleMode()
    {
        StopConsoleRawPad();
        _ = RestoreConsolePowerProfileAsync();
        _ = RestoreConsoleGameBarBlockAsync();
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        _consoleModeActive = false;
        GameLibraryPageIfCreated?.ApplyConsoleMode(false);
        RestorePreviousConsoleDisplay();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        UpdateConsoleModeButton();
        ConsoleModeButton.Focus(FocusState.Programmatic);
    }

    private void RootGrid_ConsoleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_consoleModeActive && !PlayniteLauncherOpen) return;   // the Playnite launcher dialog is operated by the same pad navigation
        // Gamepad virtual keys (195..218) mean WinUI itself is delivering the pad: the direct reader must stand down.
        if ((int)e.Key is >= 195 and <= 218) _consoleRawPad?.NoteWinUiGamepadKeysSeen();
        ConsoleNavigationAction action = ConsoleNavigationPolicy.Resolve(
            (int)e.Key, GameLibraryBusy, ConsoleOverlayOpen);
        if (ApplyConsoleAction(action)) e.Handled = true;
    }

    /// <summary>
    /// One place that turns a Console/TV navigation action into UI behaviour, shared by WinUI gamepad key events (Xbox-class pads)
    /// and the driverless raw-pad navigator (for example a Stadia pad). Returns true when the action was handled.
    /// </summary>
    private bool ApplyConsoleAction(ConsoleNavigationAction action)
    {
        if ((!_consoleModeActive && !PlayniteLauncherOpen) || action == ConsoleNavigationAction.None) return false;

        // A dialog, flyout, drop-down or the on-screen keyboard is what the pad operates: B closes it, directions stay inside it.
        if (ConsoleOverlayOpen) return HandleConsoleOverlayAction(action);

        // A game's quick-actions card sits above the library: B closes it (back to the tile it came from) before B can leave
        // Console mode, and nothing underneath (paging, search, workspace switch) reacts while it is open.
        if (ConsoleOnGamingWorkspace() && GameLibraryPageIfCreated is { QuickActionsRoot: not null } quickPage)
        {
            switch (action)
            {
                case ConsoleNavigationAction.Exit: quickPage.CloseQuickActions(); return true;
                case ConsoleNavigationAction.ScrollUp: quickPage.ScrollBy(-240); return true;
                case ConsoleNavigationAction.ScrollDown: quickPage.ScrollBy(240); return true;
                case ConsoleNavigationAction.SwitchWorkspace or ConsoleNavigationAction.PreviousPage
                    or ConsoleNavigationAction.NextPage or ConsoleNavigationAction.FocusSearch: return true;
            }
        }

        if (action == ConsoleNavigationAction.SwitchWorkspace)
            return SwitchConsoleWorkspace();

        // B on the AI page goes back to the Gaming library first; only B on the library leaves Console/TV mode.
        if (action == ConsoleNavigationAction.Exit && !ConsoleOnGamingWorkspace())
            return SwitchConsoleWorkspace();

        if (action == ConsoleNavigationAction.Exit)
        {
            try
            {
                ExitConsoleMode();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Console exit input failed: " + ex.GetType().Name);
                _consoleModeActive = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
                UpdateConsoleModeButton();
                return false;
            }
        }

        if (!ConsoleOnGamingWorkspace()
            && action is ConsoleNavigationAction.PreviousPage or ConsoleNavigationAction.NextPage
                or ConsoleNavigationAction.ScrollUp or ConsoleNavigationAction.ScrollDown or ConsoleNavigationAction.FocusSearch)
            return false;   // library-only actions are ignored on the AI page; native focus navigation below still works there

        switch (action)
        {
            case ConsoleNavigationAction.PreviousPage: GameLibraryPage.PageLists(-1); return true;
            case ConsoleNavigationAction.NextPage: GameLibraryPage.PageLists(1); return true;
            case ConsoleNavigationAction.ScrollUp: GameLibraryPage.ScrollBy(-240); return true;
            case ConsoleNavigationAction.ScrollDown: GameLibraryPage.ScrollBy(240); return true;
            case ConsoleNavigationAction.FocusSearch: GameLibraryPage.FocusSearch(); return true;
        }

        // The existing native WinUI control handles Accept (A/Enter) and text
        // input. Don't synthesize clicks: that would risk duplicate launches.
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
        if (direction is FocusNavigationDirection.Up or FocusNavigationDirection.Down
            && GameLibraryPageIfCreated?.QuickActionsRoot is { } quickRoot && TryMoveQuickActionsFocus(quickRoot, direction))
            return true;
        var options = new FindNextElementOptions { SearchRoot = root };
        return FocusManager.TryMoveFocus(direction, options);
    }

    /// <summary>
    /// Up/Down inside the Console quick-actions card go row by row in reading order. WinUI's geometric search prefers a wide control
    /// further down (an Expander) over a narrow button directly below the focus and skips what is inside an open Expander, so Edit,
    /// Save and the launch-profile fields were unreachable by the pad. Left/Right keep the geometric search.
    /// </summary>
    private bool TryMoveQuickActionsFocus(UIElement layer, FocusNavigationDirection direction)
    {
        if (RootGrid.XamlRoot is null || FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not UIElement focused
            || !IsInside(focused, layer)) return false;
        var items = new List<(Control Control, Windows.Foundation.Rect Bounds)>();
        CollectQuickFocusables(layer, layer, items);
        // The focus can sit on a part of a control (an Expander's header toggle): use the innermost listed control that contains it.
        int current = items.FindLastIndex(i => IsInside(focused, i.Control));
        if (current < 0) return false;
        Windows.Foundation.Rect here = items[current].Bounds;
        // A full-width control (an Expander header) enters the next row at its left end, never at a destructive button in the middle.
        double hereX = here.Width > ((FrameworkElement)layer).ActualWidth * 0.5 ? here.X : here.X + here.Width / 2;
        const double rowTolerance = 4;
        var candidates = (direction == FocusNavigationDirection.Down
            ? items.Where(i => i.Bounds.Y > here.Y + rowTolerance)
            : items.Where(i => i.Bounds.Y < here.Y - rowTolerance)).ToList();
        if (candidates.Count == 0) return false;
        double row = direction == FocusNavigationDirection.Down ? candidates.Min(i => i.Bounds.Y) : candidates.Max(i => i.Bounds.Y);
        var target = candidates.Where(i => Math.Abs(i.Bounds.Y - row) <= rowTolerance)
            .OrderBy(i => Math.Abs((here.Width > ((FrameworkElement)layer).ActualWidth * 0.5 ? i.Bounds.X : i.Bounds.X + i.Bounds.Width / 2) - hereX)).First();
        return target.Control.Focus(FocusState.Keyboard);
    }

    private static void CollectQuickFocusables(DependencyObject node, UIElement layer, List<(Control Control, Windows.Foundation.Rect Bounds)> items)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
            if (child is UIElement element && element.Visibility != Visibility.Visible) continue;
            if (child is Control control && control.IsEnabled && control.IsTabStop
                && (control is ButtonBase or TextBox or PasswordBox or ComboBox or Slider or ToggleSwitch or CheckBox or Expander or RadioButton))
            {
                var size = control.ActualSize;
                if (size.X > 0 && size.Y > 0)
                {
                    var origin = control.TransformToVisual(layer).TransformPoint(new Windows.Foundation.Point(0, 0));
                    items.Add((control, new Windows.Foundation.Rect(origin.X, origin.Y, size.X, size.Y)));
                }
                // An open Expander's content is part of the card; every other focusable control is a leaf here.
                if (control is Expander expander && expander.IsExpanded && expander.Content is DependencyObject content)
                    CollectQuickFocusables(content, layer, items);
                continue;
            }
            CollectQuickFocusables(child, layer, items);
        }
    }

    // ---- Driverless navigation for raw HID pads (for example Google Stadia) -------------------------------------------------
    // WinUI only turns Xbox-class gamepads into navigation key events. A raw HID pad is read directly while Console/TV mode is
    // open and no game is running; it never starts a virtual pad and stops the moment a game or Gaming mode takes over.
    private ConsoleRawPadNavigator? _consoleRawPad;

    private void StartConsoleRawPad()
    {
        try
        {
            if (_consoleRawPad == null)
            {
                _consoleRawPad = new ConsoleRawPadNavigator();
                _consoleRawPad.ActionRaised += action => { if (_consoleModeActive || PlayniteLauncherOpen) ApplyConsoleAction(action); };
                _consoleRawPad.AcceptRequested += AcceptFocusedConsoleControl;
                _consoleRawPad.PadKeyRaised += vk => { if ((_consoleModeActive || PlayniteLauncherOpen) && _keyboardOverlay?.IsOpen == true) _keyboardOverlay.HandlePadKey(vk); };
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
        if ((!_consoleModeActive && !PlayniteLauncherOpen) || RootGrid.XamlRoot is null) return;
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
            // Expanders (the per-game settings sections of the quick-actions card) expose only ExpandCollapse: A opens or closes them.
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

    private void UpdateConsoleModeButton()
    {
        bool italian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
        ConsoleModeButton.Content = _consoleModeActive
            ? italian ? "Torna al desktop" : "Return to desktop"
            : italian ? "Console / TV" : "Console / TV";
        AutomationProperties.SetName(ConsoleModeButton, _consoleModeActive
            ? italian ? "Esci dalla modalità Console" : "Exit Console mode"
            : italian ? "Apri la modalità Console TV" : "Enter Console TV mode");
        AutomationProperties.SetHelpText(ConsoleModeButton, italian
            ? "Usa la stessa libreria giochi a schermo intero. Usa D-pad o levetta sinistra per navigare, A per selezionare ed Esc o B per uscire."
            : "Use the same game library in fullscreen. Use D-pad or left stick to navigate, A to select and Escape or B to exit.");
    }
}

