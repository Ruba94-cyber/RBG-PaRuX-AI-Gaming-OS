using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Gamepad operation of everything that is NOT the page itself while the Playnite launcher dialog is pad-driven: ContentDialogs, flyouts and ComboBox drop-downs
/// (they live in separate popup roots), the on-screen keyboard, text fields, sliders and toggles. It only calls the controls' own
/// public API (Hide, IsDropDownOpen, IsOn, Value, Focus) and the in-app on-screen keyboard; no mouse/keyboard input is synthesised.
/// Text entry deliberately uses the app's own pad-operable keyboard (<c>OnScreenKeyboardOverlay</c>) and NOT the Windows touch
/// keyboard (InputPane/TabTip): the touch keyboard is a touch-only surface that a gamepad cannot drive, so it would strand the owner.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Open popups (ContentDialog, flyout, ComboBox list, suggestion list) that hold something a pad can focus, oldest first.
    /// Tooltips and other non-interactive popups are ignored.</summary>
    private List<Popup> OpenInteractivePopups()
    {
        var result = new List<Popup>();
        if (RootGrid.XamlRoot is null) return result;
        IReadOnlyList<Popup> popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot);
        if (popups.Count == 0) return result;
        foreach (Popup popup in popups)
        {
            if (!popup.IsOpen || popup.Child is not UIElement child || child.Visibility != Visibility.Visible) continue;
            if (FocusManager.FindFirstFocusableElement(child) is null) continue;
            result.Add(popup);
        }
        return result;
    }

    private Popup? TopInteractivePopup()
    {
        List<Popup> popups = OpenInteractivePopups();
        return popups.Count > 0 ? popups[^1] : null;
    }

    /// <summary>True while a dialog, flyout, drop-down or the on-screen keyboard is what the pad is operating.</summary>
    private bool ConsoleOverlayOpen => _keyboardOverlay?.IsOpen == true || TopInteractivePopup() is not null;

    /// <summary>The element directional focus may move within: the keyboard, else the topmost popup, else the whole window content.</summary>
    private DependencyObject? ConsoleFocusSearchRoot()
    {
        if (_keyboardOverlay?.IsOpen == true) return _keyboardOverlay;
        if (TopInteractivePopup()?.Child is { } popupChild) return popupChild;
        return RootGrid.XamlRoot?.Content;
    }

    /// <summary>Handles any pad action while an overlay is open. Returns true (the action is consumed): B closes the overlay,
    /// directions move inside it, everything else is ignored so it cannot act on the page underneath.</summary>
    private bool HandleConsoleOverlayAction(ConsoleNavigationAction action)
    {
        switch (action)
        {
            case ConsoleNavigationAction.Exit:
                CloseTopConsoleOverlay();
                return true;
            case ConsoleNavigationAction.Up or ConsoleNavigationAction.Down or ConsoleNavigationAction.Left or ConsoleNavigationAction.Right:
                if (TryAdjustFocusedSlider(action)) return true;
                if (_keyboardOverlay?.IsOpen != true && TopInteractivePopup() is { } popup && RescueOverlayFocus(popup)) return true;
                TryMoveConsoleFocus(ToDirection(action));
                return true;
            default:
                return true;
        }
    }

    private static FocusNavigationDirection ToDirection(ConsoleNavigationAction action) => action switch
    {
        ConsoleNavigationAction.Up => FocusNavigationDirection.Up,
        ConsoleNavigationAction.Down => FocusNavigationDirection.Down,
        ConsoleNavigationAction.Left => FocusNavigationDirection.Left,
        _ => FocusNavigationDirection.Right
    };

    /// <summary>D-pad left/right on a focused Slider changes its value (a Slider owns the horizontal axis).</summary>
    private bool TryAdjustFocusedSlider(ConsoleNavigationAction action)
    {
        if (action is not (ConsoleNavigationAction.Left or ConsoleNavigationAction.Right)) return false;
        if (RootGrid.XamlRoot is null || FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not Slider slider || !slider.IsEnabled) return false;
        double coarse = Math.Max(slider.StepFrequency, (slider.Maximum - slider.Minimum) / 100.0);
        slider.Value = ConsolePadOverlayPolicy.NextSliderValue(slider.Value, slider.Minimum, slider.Maximum, coarse,
            action == ConsoleNavigationAction.Right ? 1 : -1);
        return true;
    }

    // ---- Closing -------------------------------------------------------------------------------------------------------------
    private void CloseTopConsoleOverlay()
    {
        try
        {
            if (_keyboardOverlay?.IsOpen == true) { _keyboardOverlay.Close(commit: false); return; }
            Popup? popup = TopInteractivePopup();
            if (popup?.Child is not DependencyObject child) return;
            if (FindDialog(child) is { } dialog) { dialog.Hide(); return; }
            if (RootGrid.XamlRoot is not null
                && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is ComboBoxItem item
                && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox combo)
            {
                combo.IsDropDownOpen = false;
                combo.Focus(FocusState.Programmatic);
                return;
            }
            popup.IsOpen = false;   // flyout / menu / suggestion list
        }
        catch (Exception ex)
        {
            Log.Write("Console overlay close failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---- Dialog focus rescue -------------------------------------------------------------------------------------------------
    private static ContentDialog? FindDialog(DependencyObject root)
    {
        for (DependencyObject? up = root; up != null; up = VisualTreeHelper.GetParent(up))
            if (up is ContentDialog self) return self;
        return FindDescendant<ContentDialog>(root);
    }

    private static T? FindDescendant<T>(DependencyObject root, string? name = null) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && (name is null || typed.Name == name)) return typed;
            if (FindDescendant<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (DependencyObject? d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container)) return true;
        return false;
    }

    /// <summary>When a popup is open but the pad focus is still behind it, moves the focus into it: a dialog gets its default button
    /// (the SAFE one when the dialog named none), any other popup its first focusable element. Returns true when focus was moved.</summary>
    private bool RescueOverlayFocus(Popup popup)
    {
        if (RootGrid.XamlRoot is null || popup.Child is not DependencyObject child) return false;
        if (IsInside(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject, child)) return false;

        if (FindDialog(child) is { } dialog)
        {
            Button? Visible(string name) => FindDescendant<Button>(child, name) is { Visibility: Visibility.Visible, IsEnabled: true } b ? b : null;
            Button? primary = Visible("PrimaryButton"), secondary = Visible("SecondaryButton"), close = Visible("CloseButton");
            ConsoleDialogButton declared = dialog.DefaultButton switch
            {
                ContentDialogButton.Primary => ConsoleDialogButton.Primary,
                ContentDialogButton.Secondary => ConsoleDialogButton.Secondary,
                ContentDialogButton.Close => ConsoleDialogButton.Close,
                _ => ConsoleDialogButton.None
            };
            Button? target = ConsolePadOverlayPolicy.PickDialogButton(declared, primary != null, secondary != null, close != null) switch
            {
                ConsoleDialogButton.Primary => primary,
                ConsoleDialogButton.Secondary => secondary,
                ConsoleDialogButton.Close => close,
                _ => null
            };
            if (target != null && target.Focus(FocusState.Programmatic)) return true;
        }
        return FocusManager.FindFirstFocusableElement(child) is Control first && first.Focus(FocusState.Programmatic);
    }

    // ---- Accept (A) on controls that have no Invoke pattern ------------------------------------------------------------------
    /// <summary>Returns true when <paramref name="focused"/> was handled here (text field, ComboBox, drop-down item, ToggleSwitch, Slider).</summary>
    private bool TryAcceptSpecialConsoleControl(UIElement focused)
    {
        if (focused is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox owner)
        {
            int index = owner.IndexFromContainer(item);
            if (index >= 0) owner.SelectedIndex = index;
            owner.IsDropDownOpen = false;
            owner.Focus(FocusState.Programmatic);
            return true;
        }
        if (focused is ComboBox combo) { combo.IsDropDownOpen = !combo.IsDropDownOpen; return true; }
        if (focused is ToggleSwitch toggle) { toggle.IsOn = !toggle.IsOn; return true; }
        if (focused is Slider) return true;   // left/right adjust it; A has nothing to do
        if (focused is RichEditBox rich) { rich.Focus(FocusState.Keyboard); return true; }
        if (_keyboardOverlay is not { IsOpen: true }
            && Views.OnScreenKeyboardOverlay.IsSupportedField(focused, out Control? field) && field is not null)
        {
            RequestConsoleKeyboard(field);
            return true;
        }
        return false;
    }

    /// <summary>Test seam: the number of times a pad asked for the on-screen keyboard (also written to the log).</summary>
    internal int ConsoleKeyboardRequests { get; private set; }

    private void RequestConsoleKeyboard(Control field)
    {
        ConsoleKeyboardRequests++;
        bool opened = EnsureOnScreenKeyboard().Open(field, RootGrid.ActualHeight);
        Log.Write("Console pad: A on a text field -> on-screen keyboard " + (opened ? "opened" : "could not open") + " (" + field.GetType().Name + ").");
    }
}
