namespace PaRuX.Intelligence;

/// <summary>Pure reordering of group-chat speakers (2-4). Never changes the set, only the order.</summary>
public static class GroupChatOrderPolicy
{
    public static bool CanMove<T>(IReadOnlyList<T> speakers, int index, int delta)
    {
        int target = index + delta;
        return index >= 0 && index < speakers.Count && target >= 0 && target < speakers.Count && delta != 0;
    }

    public static List<T> Move<T>(IReadOnlyList<T> speakers, int index, int delta)
    {
        var copy = speakers.ToList();
        if (!CanMove(speakers, index, delta)) return copy;
        T item = copy[index];
        copy.RemoveAt(index);
        copy.Insert(index + delta, item);
        return copy;
    }

    /// <summary>Turns that read the regenerated reply (every later speaker); the verdict is stale too.</summary>
    public static IReadOnlyList<int> StaleTurnsAfterRegenerate(int turnCount, int index) =>
        index < 0 || index >= turnCount ? Array.Empty<int>() : Enumerable.Range(index + 1, turnCount - index - 1).ToList();
}
