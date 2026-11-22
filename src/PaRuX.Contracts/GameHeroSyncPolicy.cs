namespace PaRuX.Contracts;

/// <summary>Where a hero preview request comes from.</summary>
public enum GameHeroSource { Focus, Hover }

public enum GameHeroSyncKind { None, Preview, Revert }

/// <summary>What the hero should do now. <see cref="Key"/> is the title to preview (null for <see cref="GameHeroSyncKind.Revert"/>).</summary>
public readonly record struct GameHeroSyncDecision(GameHeroSyncKind Kind, string? Key);

/// <summary>
/// Pure, clock-free rules that let the Games hero follow the tile the user focuses or hovers. Rules:
/// focus that the user did not cause (programmatic focus on start-up, after a refresh, Console home focus) is ignored, so the hero
/// never changes by itself; a request becomes a <see cref="GameHeroSyncKind.Preview"/> only after <c>debounceMs</c> without a newer
/// request, so holding a D-pad across 20 tiles yields one preview of the last tile (intermediate titles are never emitted, which
/// bounds image decodes); when the user leaves the tiles the hero returns to the last-played title after <c>revertMs</c>, unless the
/// pointer or focus is on the hero itself (it must not change under a Play click). The caller supplies a monotonic millisecond
/// clock; nothing here reads time, files or UI.
/// </summary>
public sealed class GameHeroSyncPolicy
{
    public const int DefaultDebounceMs = 120;
    public const int DefaultRevertMs = 1500;

    private readonly int _debounceMs;
    private readonly int _revertMs;
    private string? _baseKey;
    private string? _focusKey, _hoverKey;
    private long _focusAt, _hoverAt;
    private bool _held;
    private string? _shown;           // null: the hero shows its own (last-played) title
    private bool _hasPending;
    private string? _pending;         // null: revert
    private long _due;

    public GameHeroSyncPolicy(int debounceMs = DefaultDebounceMs, int revertMs = DefaultRevertMs)
    {
        _debounceMs = Math.Max(0, debounceMs);
        _revertMs = Math.Max(0, revertMs);
    }

    /// <summary>Key of the previewed title, or null when the hero shows its own title.</summary>
    public string? Shown => _shown;

    /// <summary>When <see cref="Poll"/> next has something to say (monotonic ms), or null when nothing is pending.</summary>
    public long? DueAt => _hasPending ? _due : null;

    /// <summary>The hero's own title changed or the surface was rebuilt: forget every preview and pending request.</summary>
    public void Reset(string? baseKey)
    {
        _baseKey = baseKey;
        _focusKey = _hoverKey = null;
        _held = false;
        _shown = null;
        _hasPending = false;
        _pending = null;
    }

    public void Enter(GameHeroSource source, string key, bool userInitiated, long now)
    {
        if (!userInitiated || string.IsNullOrEmpty(key)) return;
        if (source == GameHeroSource.Focus) { _focusKey = key; _focusAt = now; }
        else { _hoverKey = key; _hoverAt = now; }
        Retarget(now);
    }

    public void Leave(GameHeroSource source, long now)
    {
        if (source == GameHeroSource.Focus) _focusKey = null; else _hoverKey = null;
        Retarget(now);
    }

    /// <summary>The pointer or keyboard focus is on the hero (Play / Details): it must keep the title the user is about to act on.</summary>
    public void Hold(bool held, long now)
    {
        if (_held == held) return;
        _held = held;
        Retarget(now);
    }

    public GameHeroSyncDecision Poll(long now)
    {
        if (!_hasPending || now < _due) return new GameHeroSyncDecision(GameHeroSyncKind.None, null);
        _hasPending = false;
        string? target = _pending;
        _shown = target;
        return target is null
            ? new GameHeroSyncDecision(GameHeroSyncKind.Revert, null)
            : new GameHeroSyncDecision(GameHeroSyncKind.Preview, target);
    }

    private void Retarget(long now)
    {
        string? effective = _hoverKey is not null && (_focusKey is null || _hoverAt >= _focusAt) ? _hoverKey : _focusKey;
        if (string.Equals(effective, _baseKey, StringComparison.Ordinal)) effective = null;   // the hero's own title is the base, not a preview
        if (effective is null && _held)
        {
            // Nothing is targeted but the user is on the hero: keep whatever it shows.
            _hasPending = false;
            return;
        }
        if (string.Equals(effective, _shown, StringComparison.Ordinal))
        {
            _hasPending = false;   // already showing it: drop any pending change (this also cancels a revert)
            return;
        }
        if (_hasPending && string.Equals(_pending, effective, StringComparison.Ordinal)) return;   // same target: keep its due time
        _hasPending = true;
        _pending = effective;
        _due = now + (effective is null ? _revertMs : _debounceMs);
    }
}
