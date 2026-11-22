using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// The hero of the Games home: the game the user played last, large, with one-click Play and Details. It only appears when
/// the library has real play history (it never invents a recommendation). Play raises <see cref="PlayRequested"/> and the host
/// runs the same launch-consent flow as every tile; nothing starts by itself.
/// </summary>
internal sealed class GameHeroCard : UserControl
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly Border _frame = new();
    private readonly Border _backdrop = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(14) };
    private readonly Border _scrim = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(14) };
    private readonly Border _fanart = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(14), Visibility = Visibility.Collapsed };
    private readonly Border _scrimBottom = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(14), Visibility = Visibility.Collapsed };
    private readonly Image _logo = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private bool _hasFanart;
    private bool _hasLogo;
    private int _artGeneration;
    private readonly Border _cover = new() { Width = 200, Height = 250, CornerRadius = new CornerRadius(10) };
    private readonly Grid _layout = new() { ColumnSpacing = 24, RowSpacing = 16, Margin = new Thickness(24) };
    private readonly StackPanel _text = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapRowPanel _buttons = new() { ColumnSpacing = 10, RowSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
    private bool _console;
    private bool _compact;
    private bool _stacked;
    private readonly TextBlock _kicker = new() { FontSize = 13, Opacity = 0.8 };
    private readonly TextBlock _name = new() { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 13 };
    private readonly Button _play = new() { MinHeight = 44, MinWidth = 140 };
    private readonly Button _details = new() { MinHeight = 44, MinWidth = 120 };
    private GameTitleDto? _title;
    // The last-played title the hero is for. While the user moves over tiles the hero may show another title (a preview) and returns here.
    private GameTitleDto? _base;
    private bool _previewing;
    private BitmapImage? _fanartBitmap;

    internal event Action<GameTitleDto>? PlayRequested;
    internal event Action<GameTitleDto>? DetailsRequested;

    internal GameHeroCard()
    {
        Visibility = Visibility.Collapsed;
        Content = _frame;
        _frame.CornerRadius = new CornerRadius(14);
        _frame.BorderThickness = new Thickness(1);
        AutomationProperties.SetAutomationId(this, "GameHome_Hero");
        AutomationProperties.SetName(this, T("Continue playing", "Continua a giocare"));

        _kicker.Text = KickerText(false);
        // The text block cross-fades when the previewed title changes (see Preview); decorative only, never a live region.
        _text.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) };
        AutomationProperties.SetAutomationId(_name, "GameHome_Hero_Title");
        AutomationProperties.SetHeadingLevel(_name, AutomationHeadingLevel.Level3);
        AutomationProperties.SetAutomationId(_logo, "GameHome_Hero_Logo");
        AutomationProperties.SetAutomationId(_summary, "GameHome_Hero_Summary");
        AutomationProperties.SetAutomationId(_source, "GameHome_Hero_Source");
        AutomationProperties.SetAutomationId(_play, "GameHome_Hero_Play");
        AutomationProperties.SetAutomationId(_details, "GameHome_Hero_Details");
        _details.Content = T("Details", "Dettagli");
        if (GameUi.AccentButtonStyle is { } accent) _play.Style = accent;
        _play.Click += (_, _) => { if (_title is not null) PlayRequested?.Invoke(_title); };
        _details.Click += (_, _) => { if (_title is not null) DetailsRequested?.Invoke(_title); };

        _text.Children.Add(_kicker);
        _text.Children.Add(_logo);
        _text.Children.Add(_name);
        _text.Children.Add(_summary);
        _text.Children.Add(_source);
        _buttons.Children.Add(_play);
        _buttons.Children.Add(_details);
        _text.Children.Add(_buttons);

        _layout.Children.Add(_cover);
        _layout.Children.Add(_text);
        _frame.Child = new Grid { Children = { _backdrop, _fanart, _scrim, _scrimBottom, _layout } };
        UpdateLayoutMode(false, false);
        SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 560;
            bool stacked = e.NewSize.Width < 380;
            if (compact != _compact || stacked != _stacked) UpdateLayoutMode(compact, stacked);
        };
        ApplyBrushes();
        ActualThemeChanged += (_, _) => ApplyBrushes();
    }

    private void UpdateLayoutMode(bool compact, bool stacked)
    {
        _compact = compact;
        _stacked = stacked;
        _layout.Margin = new Thickness(compact ? 14 : _console ? 20 : 16);
        _layout.ColumnSpacing = _hasFanart ? 0 : compact ? 14 : 24;
        _cover.Visibility = _hasFanart ? Visibility.Collapsed : Visibility.Visible;
        _frame.MinHeight = _hasFanart && !stacked ? (_console ? 300 : 220) : 0;
        _text.MaxWidth = _hasFanart ? (_console ? 760 : 560) : double.PositiveInfinity;
        _text.HorizontalAlignment = HorizontalAlignment.Left;
        _logo.MaxHeight = _console ? 112 : compact ? 56 : 80;
        _logo.MaxWidth = _console ? 560 : 360;
        _layout.ColumnDefinitions.Clear();
        _layout.RowDefinitions.Clear();
        if (stacked)
        {
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(_cover, 0);
            Grid.SetRow(_cover, 0);
            Grid.SetColumn(_text, 0);
            Grid.SetRow(_text, 1);
        }
        else
        {
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_cover, 0);
            Grid.SetColumn(_cover, 0);
            Grid.SetRow(_text, 0);
            Grid.SetColumn(_text, 1);
        }
        _cover.HorizontalAlignment = HorizontalAlignment.Left;
        _cover.Width = stacked ? 104 : compact ? 112 : _console ? 176 : 144;
        _cover.Height = _cover.Width * 4 / 3;
        _name.FontSize = compact ? 24 : _console ? 36 : 30;
    }

    private void ApplyBrushes()
    {
        _frame.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceCardBrush");
        _frame.BorderBrush = GameUi.ThemeBrush(this, "PaRuXStrokeBrush");
        UpdateBackdropTheme();
        _kicker.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
        _name.Foreground = GameUi.ThemeBrush(this, "PaRuXTextPrimaryBrush");
        _summary.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        _source.Foreground = GameUi.ThemeBrush(this, "PaRuXTextSecondaryBrush");
        if (_cover.Background is not ImageBrush)
        {
            _cover.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
            if (_cover.Child is TextBlock initials) initials.Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush");
        }
    }

    private void UpdateBackdropTheme()
    {
        // High contrast: no decorative art or scrim at all, text sits on the plain system surface.
        bool highContrast = GameUi.IsHighContrast(this);
        _scrim.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        _backdrop.Visibility = highContrast || _hasFanart ? Visibility.Collapsed : Visibility.Visible;
        _fanart.Visibility = _scrimBottom.Visibility = highContrast || !_hasFanart ? Visibility.Collapsed : Visibility.Visible;
        _backdrop.Opacity = ActualTheme == ElementTheme.Light ? 0.14 : 0.30;
        if (_frame.Background is not SolidColorBrush surface) return;
        Windows.UI.Color c = surface.Color;
        Windows.UI.Color Alpha(byte a) => Windows.UI.Color.FromArgb(a, c.R, c.G, c.B);

        var fadingSurface = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        if (_hasFanart)
        {
            // Text lives on the left: near-opaque there, easing out so the art stays visible on the right.
            fadingSurface.GradientStops.Add(new GradientStop { Color = Alpha(245), Offset = 0 });
            fadingSurface.GradientStops.Add(new GradientStop { Color = Alpha(225), Offset = 0.32 });
            fadingSurface.GradientStops.Add(new GradientStop { Color = Alpha(130), Offset = 0.62 });
            fadingSurface.GradientStops.Add(new GradientStop { Color = Alpha(30), Offset = 1 });
            var bottom = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.5, 0),
                EndPoint = new Windows.Foundation.Point(0.5, 1)
            };
            bottom.GradientStops.Add(new GradientStop { Color = Alpha(0), Offset = 0.45 });
            bottom.GradientStops.Add(new GradientStop { Color = Alpha(210), Offset = 1 });
            _scrimBottom.Background = bottom;
        }
        else
        {
            fadingSurface.GradientStops.Add(new GradientStop { Color = c, Offset = 0.44 });
            fadingSurface.GradientStops.Add(new GradientStop { Color = Alpha(0), Offset = 1 });
        }
        _scrim.Background = fadingSurface;
    }

    internal void SetConsole(bool enabled)
    {
        _console = enabled;
        _summary.FontSize = enabled ? 20 : 14;
        _source.FontSize = enabled ? 17 : 13;
        _kicker.FontSize = enabled ? 17 : 13;
        _play.MinHeight = _details.MinHeight = enabled ? 56 : 44;
        _play.FontSize = _details.FontSize = enabled ? 20 : 14;
        UpdateLayoutMode(_compact, _stacked);
    }

    internal GameTitleDto? Current => _title;

    /// <summary>The hero's own (last-played) title, whatever is previewed right now.</summary>
    internal GameTitleDto? BaseTitle => _base;

    internal bool FocusPlay() => _play.Focus(FocusState.Programmatic);

    private static string KickerText(bool preview) => preview
        ? T("SELECTED GAME", "GIOCO SELEZIONATO")
        : T("CONTINUE PLAYING", "CONTINUA A GIOCARE");

    /// <summary>True while the hero shows a title the user is pointing at instead of the last-played one.</summary>
    internal bool Previewing => _previewing;

    /// <summary>
    /// Makes <paramref name="title"/> the hero's own (last-played) title, or hides the hero for null. Any preview in progress ends.
    /// </summary>
    internal void Show(GameTitleDto? title)
    {
        _base = title;
        _previewing = false;
        Present(title, fade: false);
    }

    /// <summary>
    /// Shows the title the user focuses or hovers (null, or the hero's own title, returns to the last-played one). Only a hero that
    /// is already showing a last-played title previews; a library without play history never gets a hero that pops in and moves the
    /// tiles under the user's focus. The change is silent for assistive technology: no live region, focus stays on the tile.
    /// </summary>
    internal void Preview(GameTitleDto? title)
    {
        if (_base is null) return;
        bool toBase = title is null || string.Equals(title.TitleKey, _base.TitleKey, StringComparison.Ordinal);
        if (toBase && !_previewing) return;
        _previewing = !toBase;
        Present(toBase ? _base : title, fade: true);
    }

    private void Present(GameTitleDto? title, bool fade)
    {
        _title = title;
        _kicker.Text = KickerText(_previewing);
        if (fade && title is not null && GameMotion.Allowed(this))
        {
            _text.Opacity = 0.25;
            DispatcherQueue?.TryEnqueue(() => _text.Opacity = 1);
        }
        else _text.Opacity = 1;
        if (title is null)
        {
            Visibility = Visibility.Collapsed;
            _artGeneration++;
            CancelFanartDecode();
            _cover.Background = _backdrop.Background = _fanart.Background = null;
            _logo.Source = null;
            _hasFanart = _hasLogo = false;
            ApplyArtState();
            return;
        }
        Visibility = Visibility.Visible;
        _name.Text = title.Name;
        _summary.Text = GameUi.PlaySummary(title);
        _source.Text = GameUi.SourceSummary(title);
        GameInstallationDto? chosen = GameUi.ChooseInstallation(title);
        _play.Content = GameUi.PlayLabel(chosen);
        _play.IsEnabled = GameUi.CanStart(chosen);
        AutomationProperties.SetName(_play, GameUi.PlayLabel(chosen) + " " + title.Name);
        AutomationProperties.SetName(_details, T("Details of ", "Dettagli di ") + title.Name);

        int generation = ++_artGeneration;
        CancelFanartDecode();
        _fanart.Background = null;
        _logo.Source = null;
        _hasFanart = _hasLogo = false;
        ApplyArtState();
        _ = LoadWideArtAsync(title, generation);

        ShowFallback(title.Name);
        string? path = GameCoverStore.TryGetPath(title.TitleKey);
        if (path is null) return;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 360 };
            bitmap.ImageFailed += (_, _) =>
            {
                // A recycled hero must not replace the new title's cover after an old decode fails.
                if (_cover.Background is ImageBrush brush && ReferenceEquals(brush.ImageSource, bitmap))
                    ShowFallback(_title?.Name ?? string.Empty);
            };
            _cover.Child = null;
            _cover.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            _backdrop.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            bitmap.UriSource = new Uri(path);
        }
        catch (Exception) { ShowFallback(title.Name); }
    }

    // A preview that is replaced before its landscape art finished decoding gives the decode back (the source is cleared).
    private void CancelFanartDecode()
    {
        BitmapImage? old = _fanartBitmap;
        _fanartBitmap = null;
        if (old is null) return;
        try { old.UriSource = null; } catch (Exception) { /* Already torn down. */ }
    }

    /// <summary>
    /// Resolves hero (landscape) and logo art off the UI thread, then decodes them at a bounded pixel width. A stale result
    /// (the hero moved to another title or collapsed meanwhile) is dropped. Missing or invalid art leaves the current look.
    /// </summary>
    private async Task LoadWideArtAsync(GameTitleDto title, int generation)
    {
        string key = title.TitleKey;
        GameArtInfo?[] found;
        try
        {
            string root = GameCoverStore.StorageFolder;
            found = await Task.Run(() => new[]
            {
                GameArtStore.Resolve(key, GameArtKind.Hero, GameCoverStore.TryGetPath, root),
                GameArtStore.Resolve(key, GameArtKind.Logo, GameCoverStore.TryGetPath, root)
            });
        }
        catch (Exception) { return; }
        if (generation != _artGeneration || _title is null) return;

        try
        {
            if (found[0] is { } hero)
            {
                var bitmap = new BitmapImage { DecodePixelWidth = _console ? 1920 : 1280 };
                bitmap.ImageFailed += (_, _) =>
                {
                    if (generation != _artGeneration) return;
                    _fanart.Background = null;
                    _hasFanart = false;
                    ApplyArtState();
                };
                _fanart.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
                _fanartBitmap = bitmap;
                bitmap.UriSource = new Uri(hero.Path);
                _hasFanart = true;
            }
            if (found[1] is { } logo)
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 640 };
                bitmap.ImageFailed += (_, _) =>
                {
                    if (generation != _artGeneration) return;
                    _logo.Source = null;
                    _hasLogo = false;
                    ApplyArtState();
                };
                _logo.Source = bitmap;
                AutomationProperties.SetName(_logo, title.Name);
                bitmap.UriSource = new Uri(logo.Path);
                _hasLogo = true;
            }
        }
        catch (Exception) { _hasFanart = _hasLogo = false; _fanart.Background = null; _logo.Source = null; }
        ApplyArtState();
    }

    /// <summary>Applies the cinematic layout only for the art that is really there; the title text stays in the tree (UIA heading, existing automation id).</summary>
    private void ApplyArtState()
    {
        bool logo = _hasLogo && !GameUi.IsHighContrast(this);
        _logo.Visibility = logo ? Visibility.Visible : Visibility.Collapsed;
        _name.Opacity = logo ? 0 : 1;
        _name.MaxHeight = logo ? 1 : double.PositiveInfinity;
        UpdateLayoutMode(_compact, _stacked);
        UpdateBackdropTheme();
    }

    private void ShowFallback(string name)
    {
        _cover.Background = GameUi.ThemeBrush(this, "PaRuXSurfaceRaisedBrush");
        _backdrop.Background = null;
        _cover.Child = new TextBlock
        {
            Text = GameUi.Initials(name),
            FontSize = 48,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GameUi.ThemeBrush(this, "PaRuXBrandTextBrush")
        };
    }
}
