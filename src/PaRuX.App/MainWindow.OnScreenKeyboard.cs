using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;
using PaRuX.Views;

namespace PaRuX;

/// <summary>
/// Hosts the pad-operable on-screen keyboard. Since the native Console/TV shell was removed (gaming-removal step 3) it opens only
/// when a raw pad presses A on a text field inside the Playnite launcher dialog (MainWindow.ConsolePad.cs). Desktop typing is
/// untouched. Event-driven: no polling, no process, no telemetry.
/// </summary>
public sealed partial class MainWindow
{
    private OnScreenKeyboardOverlay? _keyboardOverlay;

    /// <summary>Lazily constructs the pad-operable on-screen keyboard on first use so cold start never runs XamlReader.Load.</summary>
    private OnScreenKeyboardOverlay EnsureOnScreenKeyboard()
    {
        if (_keyboardOverlay != null) return _keyboardOverlay;
        _keyboardOverlay = new OnScreenKeyboardOverlay();
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_keyboardOverlay, 2);
        RootGrid.Children.Add(_keyboardOverlay);
        return _keyboardOverlay;
    }
}
