namespace PaRuX.Server;

/// <summary>One ready-made connection for a well-known local OpenAI-compatible server.</summary>
public sealed record OpenAiCompatibleProviderPreset(
    string Id, string DisplayName, string BaseUrl, bool ApiKeyUsuallyRequired,
    string NoteEn, string NoteIt);

/// <summary>
/// Presets for vLLM, Ollama and LM Studio. They only pre-fill the address of the generic local
/// OpenAI-compatible provider (same loopback/LAN-only validation, same encrypted key handling): PaRuX does not
/// install, start or manage these servers, and nothing here makes them reachable from outside this PC.
/// </summary>
public static class OpenAiCompatibleProviderPresets
{
    public static readonly IReadOnlyList<OpenAiCompatibleProviderPreset> All = new[]
    {
        new OpenAiCompatibleProviderPreset("vllm", "vLLM", "http://127.0.0.1:8000/v1", false,
            "Start it yourself with 'vllm serve <model>'. If you launched it with --api-key, enter that key.",
            "Avvialo tu con 'vllm serve <modello>'. Se lo hai lanciato con --api-key, inserisci quella chiave."),
        new OpenAiCompatibleProviderPreset("ollama", "Ollama", "http://127.0.0.1:11434/v1", false,
            "Ollama must be running (its tray app or 'ollama serve'). It needs no key; PaRuX lists the models you pulled.",
            "Ollama deve essere in esecuzione (app nella tray o 'ollama serve'). Non serve chiave; PaRuX elenca i modelli scaricati."),
        new OpenAiCompatibleProviderPreset("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", false,
            "Turn on the Local Server in LM Studio's Developer tab and load a model first.",
            "Attiva il Local Server nella scheda Developer di LM Studio e carica prima un modello."),
    };

    /// <summary>Fills a blank address/name from the preset. False with a reason for an unknown preset id.</summary>
    public static bool TryApply(PaRuX.Contracts.OpenAiCompatibleProviderRequest request, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(request.PresetId)) return true;
        OpenAiCompatibleProviderPreset? preset = Find(request.PresetId);
        if (preset is null)
        {
            error = "Unknown provider preset. Known presets: " + string.Join(", ", All.Select(p => p.Id)) + ".";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.BaseUrl)) request.BaseUrl = preset.BaseUrl;
        if (string.IsNullOrWhiteSpace(request.DisplayName)) request.DisplayName = preset.DisplayName;
        return true;
    }

    public static OpenAiCompatibleProviderPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}
