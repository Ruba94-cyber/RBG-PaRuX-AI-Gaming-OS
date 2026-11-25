using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Quick Resume at the very top of the Gaming library: while an app or game is PAUSED, a prominent card offers "Resume" before
/// anything else (and takes the first focus in Console/TV mode), so the owner never has to hunt for the paused game. Invisible when
/// nothing is paused. It reads the same Quick Resume status as <see cref="QuickResumePanel"/> and acts through the same Core calls
/// (the pause/resume safety rules stay in Core); it polls only while it is on screen, about every 5 seconds, and never while a
/// refresh or action is in flight.
/// </summary>
internal sealed class PausedGameBanner : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _busy;
    private bool _acting;
    private IReadOnlyList<QuickResumeAppDto>? _paused;   // null = nothing rendered yet / force a rebuild

    /// <summary>Raised after the owner resumed something, so the rest of the library (the Quick Resume list) can refresh.</summary>
    public event Func<Task>? Resumed;

    public PausedGameBanner()
    {
        Visibility = Visibility.Collapsed;
        Spacing = 8;
        Margin = new Thickness(0, 0, 0, 16);
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        if (Application.Current.Resources.TryGetValue("SystemFillColorAttentionBackgroundBrush", out object? back) && back is Microsoft.UI.Xaml.Media.Brush b)
            Background = b;
        var heading = new TextBlock
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = T("Paused — pick up where you left off", "In pausa — riprendi da dove eri rimasto"),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(heading, "GameHome_PausedBanner_Heading");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Children.Add(heading);
        Children.Add(_rows);
        AutomationProperties.SetAutomationId(this, "GameHome_PausedBanner");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => { if (IsLoaded && XamlRoot is not null && !UiPollCadence.IsWindowHidden()) await RefreshAsync(); };
    }

    /// <summary>True while a paused app is offered.</summary>
    public bool HasPaused => Visibility == Visibility.Visible && _paused is { Count: > 0 };

    /// <summary>Focuses the first Resume button (Console/TV start focus). False when nothing is paused.</summary>
    public bool FocusFirstResume()
    {
        if (!HasPaused) return false;
        foreach (UIElement row in _rows.Children)
            if (row is Grid grid && grid.Children.OfType<Button>().FirstOrDefault() is { } button)
                return button.Focus(FocusState.Programmatic);
        return false;
    }

    public async Task RefreshAsync()
    {
        if (_busy || _acting) return;
        _busy = true;
        try
        {
            var result = await GamingLibraryClient.Current.GetQuickResumeAsync();
            // A failed read keeps what is shown (a transient Core hiccup must not make the Resume card flicker away).
            if (!result.Succeeded || result.Value is null) return;
            List<QuickResumeAppDto> paused = result.Value.Apps.Where(a => a.Suspended).ToList();
            if (SamePaused(paused)) return;
            _paused = paused;
            _rows.Children.Clear();
            foreach (QuickResumeAppDto app in paused.Take(4)) _rows.Children.Add(BuildRow(app));
            Visibility = paused.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Write("Paused-game banner refresh failed: " + ex.GetType().Name);
        }
        finally { _busy = false; }
    }

    private bool SamePaused(List<QuickResumeAppDto> now) =>
        _paused is { } shown && now.Count == shown.Count
        && now.Zip(shown).All(p => p.First.ProcessId == p.Second.ProcessId && p.First.StartedUtc == p.Second.StartedUtc);

    private UIElement BuildRow(QuickResumeAppDto app)
    {
        string title = string.IsNullOrWhiteSpace(app.WindowTitle) ? app.ProcessName : app.WindowTitle;
        if (title.Length > 80) title = title[..80] + "…";
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var button = new Button
        {
            MinHeight = 52,
            MinWidth = 180,
            Content = T("Resume ", "Riprendi ") + title,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) && style is Style accent) button.Style = accent;
        AutomationProperties.SetAutomationId(button, "GameHome_PausedBanner_Resume_" + app.ProcessId);
        AutomationProperties.SetName(button, T("Resume ", "Riprendi ") + title);
        button.Click += async (_, _) => await ResumeAsync(app, button);
        row.Children.Add(button);

        var detail = new TextBlock
        {
            Text = app.ProcessName + " · " + T("Paused", "In pausa"),
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        return row;
    }

    private async Task ResumeAsync(QuickResumeAppDto app, Button button)
    {
        if (_acting) return;
        _acting = true;               // a double click must not send two resume requests
        button.IsEnabled = false;
        try
        {
            var result = await GamingLibraryClient.Current.QuickResumeActionAsync(
                "resume", new QuickResumeActionRequest(app.ProcessId, app.StartedUtc));
            Log.Write("Quick Resume from the banner (pid " + app.ProcessId + "): succeeded=" + (result.Value?.Succeeded.ToString() ?? "n/a")
                + "; " + (result.Value?.Message ?? result.Error ?? "no answer"));
        }
        catch (Exception ex)
        {
            Log.Write("Quick Resume from the banner failed: " + ex.GetType().Name);
        }
        finally
        {
            _acting = false;
            button.IsEnabled = true;
        }
        _paused = null;   // force a rebuild from the fresh status
        await RefreshAsync();
        if (Resumed is { } handler) await handler();
    }
}
