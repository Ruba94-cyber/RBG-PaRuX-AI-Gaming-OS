using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using PaRuX.Contracts;

namespace PaRuX.Views;

/// <summary>
/// What the Games home needs from the library surface: one virtualized <see cref="GameTileGrid"/> (no grouping) or a
/// <see cref="GroupedGameLibrary"/> (one virtualized grid per group). The host never cares which one it holds.
/// </summary>
internal interface IGameTileSurface
{
    FrameworkElement View { get; }
    int Count { get; }
    event Action<GameTitleDto>? OpenRequested;
    event Action<GameTitleDto>? PlayRequested;
    event Action<GameTitleDto>? SelectionToggled;
    /// <summary>A tile took focus or was hovered; the flag is true when the user (not the program) caused it. Feeds the hero preview.</summary>
    event Action<GameTitleDto, GameHeroSource, bool>? TileEntered;
    event Action<GameHeroSource>? TileLeft;
    void SetConsole(bool enabled);
    void SetSelectMode(bool enabled);
    void SetCoverSize(string size);
    void Render(IReadOnlyList<GameTitleDto> titles, ISet<string> selected, string? openKey);
    void RefreshState(ISet<string> selected, string? openKey);
    void UpdateTitle(GameTitleDto title);
    bool FocusTile(string key);
    bool FocusFirst();
}
