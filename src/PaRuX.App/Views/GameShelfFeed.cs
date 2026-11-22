using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;

namespace PaRuX.Views;

/// <summary>
/// The console-style feed of the Games home: a vertical stack of horizontal shelves (Continue, Favorites, Recently added and the
/// largest collections), each shelf a virtualized <see cref="GameTileGrid"/> row that realizes only the covers near its own viewport
/// (and none while it is far below the page's viewport). The shelves come from the pure <see cref="GameShelfFeedModel"/>; this
/// class only presents them. Up/Down moves focus to the same column of the next shelf and hands focus back to the page at the first
/// and last shelf, Left/Right/Home/End/bumpers move inside a shelf (<see cref="GameShelfFeedModel.Move"/>, unit-tested).
/// Sections are reused by shelf id on refresh, so covers are rebound rather than rebuilt.
/// </summary>
internal sealed class GameShelfFeed : StackPanel, IGameTileSurface
{
    private sealed class Section
    {
        public required string Id { get; init; }
        public required TextBlock Header { get; init; }
        public required GameTileGrid Row { get; init; }
        public GameShelfKind Kind { get; set; }
    }

    private readonly Dictionary<string, Section> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _firstShelfOfTitle = new(StringComparer.Ordinal);
    private List<Section> _ordered = new();
    private IReadOnlyList<GameShelf> _shelves = Array.Empty<GameShelf>();
    private List<GameTitleDto> _titles = new();
    private ISet<string> _selected = new HashSet<string>(StringComparer.Ordinal);
    private string? _openKey;
    private bool _console;
    private bool _selectMode;
    private string _coverSize = GameCoverSizes.Medium;

    public event Action<GameTitleDto>? OpenRequested;
    public event Action<GameTitleDto>? PlayRequested;
    public event Action<GameTitleDto>? SelectionToggled;
    public event Action<GameTitleDto, GameHeroSource, bool>? TileEntered;
    public event Action<GameHeroSource>? TileLeft;

    /// <summary>Hidden titles join the shelves only while the library shows hidden titles.</summary>
    internal bool IncludeHidden { get; set; }

    internal GameShelfFeed()
    {
        Spacing = 6;
        ActualThemeChanged += (_, _) => { foreach (Section section in _sections.Values) PaintHeader(section.Header); };
        PreviewKeyDown += OnKeyDown;   // tunnelling: runs before the inner scroll viewers and the focus system see the key
    }

    public FrameworkElement View => this;
    public int Count => _titles.Count;

    /// <summary>Number of shelves and of tiles that exist right now across all shelves (virtualization diagnostics).</summary>
    internal int ShelfCount => _ordered.Count;
    internal int RealizedTileCount => _ordered.Sum(s => s.Row.RealizedCount);

    public void SetConsole(bool enabled)
    {
        _console = enabled;
        foreach (Section section in _sections.Values)
        {
            section.Row.SetConsole(enabled);
            section.Header.FontSize = enabled ? 26 : 18;
        }
    }

    public void SetSelectMode(bool enabled)
    {
        _selectMode = enabled;
        foreach (Section section in _sections.Values) section.Row.SetSelectMode(enabled);
    }

    public void SetCoverSize(string size)
    {
        _coverSize = GameCoverSizes.Normalize(size);
        foreach (Section section in _sections.Values) section.Row.SetCoverSize(_coverSize);
    }

    public void Render(IReadOnlyList<GameTitleDto> titles, ISet<string> selected, string? openKey)
    {
        _selected = selected;
        _openKey = openKey;
        _titles = new List<GameTitleDto>(titles);
        IReadOnlyList<GameShelf> shelves = GameShelfFeedModel.Build(_titles, IncludeHidden);
        _shelves = shelves;

        _firstShelfOfTitle.Clear();
        var ordered = new List<Section>(shelves.Count);
        foreach (GameShelf shelf in shelves)
        {
            Section section = _sections.TryGetValue(shelf.Id, out Section? existing) ? existing : CreateSection(shelf.Id);
            section.Kind = shelf.Kind;
            section.Header.Text = NameOf(shelf) + " · " + shelf.Titles.Count.ToString(System.Globalization.CultureInfo.CurrentUICulture);
            AutomationProperties.SetName(section.Header, section.Header.Text);
            AutomationProperties.SetName(section.Row, section.Header.Text);
            section.Row.Render(shelf.Titles, selected, openKey);
            foreach (GameTitleDto title in shelf.Titles) _firstShelfOfTitle.TryAdd(title.TitleKey, shelf.Id);
            ordered.Add(section);
        }

        foreach (string stale in _sections.Keys.Where(k => !shelves.Any(s => s.Id == k)).ToList()) _sections.Remove(stale);

        // Only touch the visual tree when the sequence of shelves changed; a refresh of the same shelves keeps tiles and scroll offsets.
        bool same = ordered.Count == _ordered.Count && ordered.Zip(_ordered).All(pair => ReferenceEquals(pair.First, pair.Second));
        _ordered = ordered;
        if (same && Children.Count == ordered.Count * 2) return;
        Children.Clear();
        foreach (Section section in ordered)
        {
            Children.Add(section.Header);
            Children.Add(section.Row);
        }
    }

    public void RefreshState(ISet<string> selected, string? openKey)
    {
        _selected = selected;
        _openKey = openKey;
        foreach (Section section in _ordered) section.Row.RefreshState(selected, openKey);
    }

    public void UpdateTitle(GameTitleDto title)
    {
        int index = _titles.FindIndex(t => string.Equals(t.TitleKey, title.TitleKey, StringComparison.Ordinal));
        if (index < 0) return;
        _titles[index] = title;
        // A favorite toggled, a collection edited or a play recorded can move the title between shelves: rebuild then, rebind otherwise.
        IReadOnlyList<GameShelf> next = GameShelfFeedModel.Build(_titles, IncludeHidden);
        if (!GameShelfFeedModel.SameMembership(_shelves, next))
        {
            Render(_titles, _selected, _openKey);
            return;
        }
        _shelves = next;
        foreach (Section section in _ordered) section.Row.UpdateTitle(title);
    }

    public bool FocusTile(string key) =>
        _firstShelfOfTitle.TryGetValue(key, out string? shelf) && _sections.TryGetValue(shelf, out Section? section) && section.Row.FocusTile(key);

    public bool FocusFirst() => _ordered.Count > 0 && _ordered[0].Row.FocusIndex(0);

    private Section CreateSection(string id)
    {
        var header = new TextBlock
        {
            FontSize = _console ? 26 : 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            IsTextScaleFactorEnabled = true
        };
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level3);
        AutomationProperties.SetAutomationId(header, "GameHome_Shelf_" + id);
        PaintHeader(header);

        var row = new GameTileGrid(GameTileGrid.Presentation.Row) { HandlesNavigationKeys = false };
        row.SetIdScope(id);
        AutomationProperties.SetAutomationId(row, "GameHome_ShelfRow_" + id);
        row.SetConsole(_console);
        row.SetSelectMode(_selectMode);
        row.SetCoverSize(_coverSize);
        row.OpenRequested += t => OpenRequested?.Invoke(t);
        row.PlayRequested += t => PlayRequested?.Invoke(t);
        row.SelectionToggled += t => SelectionToggled?.Invoke(t);
        row.TileEntered += (t, source, user) => TileEntered?.Invoke(t, source, user);
        row.TileLeft += source => TileLeft?.Invoke(source);

        var section = new Section { Id = id, Header = header, Row = row };
        _sections[id] = section;
        return section;
    }

    private void PaintHeader(TextBlock header) => header.Foreground = GameUi.ThemeBrush(header, "PaRuXTextPrimaryBrush");

    private static string NameOf(GameShelf shelf) => shelf.Kind switch
    {
        GameShelfKind.Continue => GameUi.T("Continue playing", "Continua a giocare"),
        GameShelfKind.Favorites => GameUi.T("Favorites", "Preferiti"),
        GameShelfKind.RecentlyAdded => GameUi.T("Recently added", "Aggiunti di recente"),
        _ => shelf.Name,
    };

    /// <summary>Keyboard / D-pad movement across the shelves. Only keys pressed on a tile's title button are handled.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_selectMode || e.OriginalSource is not ButtonBase source) return;
        if (GameTileGrid.DirectionOf(e.Key) is not { } direction) return;
        int shelf = -1, index = -1;
        for (int i = 0; i < _ordered.Count && index < 0; i++)
        {
            index = _ordered[i].Row.IndexOfSource(source);
            if (index >= 0) shelf = i;
        }
        if (shelf < 0) return;
        (int nextShelf, int nextIndex) = GameShelfFeedModel.Move(
            _ordered.Select(s => s.Row.Count).ToList(), shelf, index, direction, Math.Max(1, _ordered[shelf].Row.ColumnCount - 1));
        // Nothing to move to (the first/last shelf, or the end of a shelf): the page's own focus navigation continues.
        if (nextShelf < 0 || (nextShelf == shelf && nextIndex == index)) return;
        GameTileGrid.NoteUserNavigation();
        if (_ordered[nextShelf].Row.FocusIndex(nextIndex)) e.Handled = true;
    }
}
