using System;
using Microsoft.UI.Xaml;

namespace PaRuX.Views;

/// <summary>
/// Decorative motion of the Games home (tile lift, hero cross-fade). It is off whenever Windows animations are off or a high-contrast
/// theme is active; focus and selection stay readable through solid borders either way. Any failure to ask the system means "no motion".
/// </summary>
internal static class GameMotion
{
    private static Windows.UI.ViewManagement.UISettings? _settings;

    internal static bool Allowed(FrameworkElement owner)
    {
        try
        {
            if (GameUi.IsHighContrast(owner)) return false;
            _settings ??= new Windows.UI.ViewManagement.UISettings();
            return _settings.AnimationsEnabled;
        }
        catch (Exception) { return false; }
    }
}
