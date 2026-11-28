using PaRuX.Contracts;

namespace PaRuX.Server.AgentSkills;

/// <summary>Ephemeral, bounded, per-conversation pending proposals. One per conversation: a new one replaces the old.</summary>
public sealed class ChatSettingProposalService
{
    public const int MaxPending = 64;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, ChatSettingProposalDto> _pending = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;

    public ChatSettingProposalService(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public static bool ValidConversation(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);

    public ChatSettingProposalDto Propose(string conversationId, ChatSettingChange change)
    {
        if (!ValidConversation(conversationId)) throw new InvalidOperationException("A chat is required to propose a setting.");
        lock (_gate)
        {
            Prune();
            if (!_pending.ContainsKey(conversationId) && _pending.Count >= MaxPending) throw new InvalidOperationException("Too many pending setting proposals.");
            var proposal = new ChatSettingProposalDto
            {
                Id = Guid.NewGuid().ToString("N"), Key = change.Key, Value = change.Value, Description = change.Description,
                ExpiresUtc = _clock() + Lifetime
            };
            _pending[conversationId] = proposal;
            return proposal;
        }
    }

    public List<ChatSettingProposalDto> List(string conversationId)
    {
        lock (_gate)
        {
            Prune();
            return conversationId != null && _pending.TryGetValue(conversationId, out ChatSettingProposalDto? p)
                ? new List<ChatSettingProposalDto> { p } : new List<ChatSettingProposalDto>();
        }
    }

    /// <summary>Removes the proposal (the owner pressed Apply or Cancel). Returns whether it was still pending.</summary>
    public bool Delete(string conversationId, string id)
    {
        lock (_gate)
        {
            Prune();
            if (conversationId == null || !_pending.TryGetValue(conversationId, out ChatSettingProposalDto? p) || p.Id != id) return false;
            return _pending.Remove(conversationId);
        }
    }

    private void Prune()
    {
        DateTime now = _clock();
        foreach (string key in _pending.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList()) _pending.Remove(key);
    }
}
