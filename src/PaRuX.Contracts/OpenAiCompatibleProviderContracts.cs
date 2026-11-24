namespace PaRuX.Contracts;

/// <summary>
/// Save request for the generic "local OpenAI-compatible server" provider (vLLM, KoboldCpp, the LM Studio server,
/// text-generation-webui, TabbyAPI, ...). The server is USER_MANAGED: PaRuX never starts, stops, updates or
/// configures it. Only a loopback or private-LAN address is accepted.
/// </summary>
public sealed class OpenAiCompatibleProviderRequest
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional preset id (<c>vllm</c>, <c>ollama</c>, <c>lmstudio</c>) that fills a blank address and name.
    /// An address typed by the user always wins.</summary>
    public string? PresetId { get; set; }

    /// <summary>Server address such as <c>http://127.0.0.1:8000</c>. A trailing <c>/v1</c> is accepted and stripped.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Null keeps the stored key (only while the address is unchanged); an empty string clears it.</summary>
    public string? ApiKey { get; set; }

    public string? SelectedModel { get; set; }

    /// <summary>When true, the chat uses this server instead of PaRuX's own runtime. Off by default.</summary>
    public bool UseForChat { get; set; }
}

public sealed class OpenAiCompatibleProviderStatus
{
    public bool Configured { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public string? SelectedModel { get; set; }
    public bool UseForChat { get; set; }

    /// <summary>Always USER_MANAGED: PaRuX talks to this server but never installs or mutates it.</summary>
    public string Ownership { get; set; } = "USER_MANAGED";

    /// <summary>True when the server is on the private LAN rather than this machine.</summary>
    public bool IsLanHost { get; set; }
}

public static class OpenAiCompatibleStates
{
    public const string Ok = "ok";
    public const string InvalidConfig = "invalid_config";
    public const string Unreachable = "unreachable";
    public const string Timeout = "timeout";
    public const string Unauthorized = "unauthorized";
    public const string NotOpenAiCompatible = "not_openai_compatible";
    public const string NoModels = "no_models";
    public const string ModelNotServed = "model_not_served";
    public const string ServerError = "server_error";
    public const string GamingActive = "gaming_active";
}

public sealed class OpenAiCompatibleTestResult
{
    /// <summary>One of <see cref="OpenAiCompatibleStates"/>.</summary>
    public string State { get; set; } = OpenAiCompatibleStates.InvalidConfig;
    public string Message { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new();

    /// <summary>Plain-language warning shown next to a successful test, for example about a key sent over plain HTTP on the LAN.</summary>
    public string? Warning { get; set; }
}
