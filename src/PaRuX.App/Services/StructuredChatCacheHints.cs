using System.Text.Json.Nodes;

namespace PaRuX.Services
{
    /// <summary>
    /// Prompt-cache request hints for structured desktop-agent turns that talk to a managed llama-server through the
    /// thin /v1/chat/completions passthrough (parux-role:* selectors). That route does not run ChatProxyService's
    /// PromptCacheScopePolicy, so without these fields the replayed evidence prefix relies on the server's defaults.
    /// The values mirror ChatProxyService.PrepareProviderPayload: cache_prompt reuses the longest common KV prefix
    /// of the slot, n_cache_reuse lets a shifted window (oldest evidence dropped) reuse matching chunks of at least
    /// this many tokens instead of re-evaluating the whole tail. A caller-supplied value is never overridden.
    /// </summary>
    internal static class StructuredChatCacheHints
    {
        /// <summary>Same chunk size as PromptCacheScopePolicy.ReuseChunkTokens.</summary>
        internal const int ReuseChunkTokens = 256;

        internal static void Apply(JsonObject directPayload)
        {
            if (!directPayload.ContainsKey("cache_prompt")) directPayload["cache_prompt"] = true;
            if (!directPayload.ContainsKey("n_cache_reuse")) directPayload["n_cache_reuse"] = ReuseChunkTokens;
        }
    }
}
