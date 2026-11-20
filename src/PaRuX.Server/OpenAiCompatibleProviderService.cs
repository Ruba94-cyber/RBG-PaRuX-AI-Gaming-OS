using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>A failure of the optional local OpenAI-compatible provider, with a stable state and an honest message.</summary>
public sealed class OpenAiCompatibleProviderException : Exception
{
    public OpenAiCompatibleProviderException(string state, string message, int httpStatus = StatusCodes.Status502BadGateway)
        : base(message)
    {
        State = state;
        HttpStatus = httpStatus;
    }

    public string State { get; }
    public int HttpStatus { get; }
}

/// <summary>
/// Generic provider for any OpenAI-compatible server the user already runs on this PC or on the private LAN
/// (vLLM, KoboldCpp, the LM Studio server, text-generation-webui, TabbyAPI, ...). The server is USER_MANAGED:
/// PaRuX only talks to it, and only when the user switched "use for chat" on. It never starts, stops, updates or
/// configures the server.
///
/// Boundaries, in order:
/// - the address must be localhost or a literal loopback/private-LAN IP over http(s) (a public address is refused,
///   there is no remote opt-in here), with no credentials, query or fragment;
/// - every connection is re-validated at connect time, so a host name or DNS answer cannot reach a public address,
///   and redirects are never followed;
/// - an optional API key is stored encrypted for the current user and is sent only to the configured address.
/// </summary>
public sealed class OpenAiCompatibleProviderService
{
    internal const string EngineId = "openai-compatible-local";
    private const string SecretPurpose = "PaRuX.OpenAiCompatibleProvider.v1";
    private const int MaxModels = 200;
    private const int MaxBodyBytes = 1 << 20;
    private const int MaxStreamChars = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);

    private readonly string _stateDirectory;
    private readonly ISecretProtector _protector;
    private readonly ValidatedRemoteConnector.HostResolver? _resolver;
    private readonly object _gate = new();

    public OpenAiCompatibleProviderService() : this(ServerState.StateDirectory) { }

    internal OpenAiCompatibleProviderService(string stateDirectory)
        : this(stateDirectory, SecretProtection.For(stateDirectory), null) { }

    internal OpenAiCompatibleProviderService(
        string stateDirectory, ISecretProtector protector, ValidatedRemoteConnector.HostResolver? resolver)
    {
        _stateDirectory = stateDirectory;
        _protector = protector;
        _resolver = resolver;
    }

    /// <summary>How long a chat may stay silent before the first byte, and between later bytes.</summary>
    internal TimeSpan FirstByteTimeout { get; set; } = TimeSpan.FromMinutes(3);
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    private string ConfigPath => Path.Combine(_stateDirectory, "openai-compatible-provider.json");

    // ---------------------------------------------------------------- configuration

    public OpenAiCompatibleProviderStatus GetStatus()
    {
        Config? config = Load();
        if (config == null) return new OpenAiCompatibleProviderStatus();
        return new OpenAiCompatibleProviderStatus
        {
            Configured = true,
            DisplayName = config.DisplayName,
            BaseUrl = config.BaseUrl,
            HasApiKey = !string.IsNullOrEmpty(config.ProtectedApiKey),
            SelectedModel = config.SelectedModel,
            UseForChat = config.UseForChat,
            IsLanHost = !IsLoopbackHost(config.BaseUrl)
        };
    }

    /// <summary>Validates and stores the provider. Throws <see cref="InvalidDataException"/> with a user-facing reason.</summary>
    public OpenAiCompatibleProviderStatus Save(OpenAiCompatibleProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryNormalizeBaseUrl(request.BaseUrl, out string baseUrl, out string error))
            throw new InvalidDataException(error);
        string? apiKey = ValidateApiKey(request.ApiKey);
        string? model = NormalizeModel(request.SelectedModel);
        string name = string.IsNullOrWhiteSpace(request.DisplayName)
            ? "OpenAI-compatible server"
            : Truncate(request.DisplayName.Trim(), 60);
        if (ContainsControl(name)) throw new InvalidDataException("The name cannot contain control characters.");

        lock (_gate)
        {
            Config? existing = Load();
            // A stored key belongs to the address it was entered for. Pointing the provider at another address
            // drops it unless a new key is supplied, so a credential cannot follow the provider to a host it was
            // never given to.
            string? protectedKey = existing?.ProtectedApiKey;
            if (request.ApiKey != null)
                protectedKey = apiKey == null ? null : _protector.Protect(SecretPurpose, apiKey);
            else if (existing == null || !string.Equals(existing.BaseUrl, baseUrl, StringComparison.Ordinal))
                protectedKey = null;

            Directory.CreateDirectory(_stateDirectory);
            var config = new Config
            {
                DisplayName = name,
                BaseUrl = baseUrl,
                ProtectedApiKey = protectedKey,
                SelectedModel = model,
                UseForChat = request.UseForChat
            };
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temp, ConfigPath, overwrite: true);
        }
        return GetStatus();
    }

    /// <summary>Removes the provider and its stored key. Chat returns to PaRuX's own runtime.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(ConfigPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Address rule shared by saving and testing. Accepts <c>http(s)://localhost|loopback|private-LAN-IP[:port]</c>,
    /// optionally ending in <c>/v1</c>; returns the normalized <c>scheme://host:port</c>.
    /// </summary>
    internal static bool TryNormalizeBaseUrl(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the server address, for example http://127.0.0.1:8000.";
            return false;
        }
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Use an http:// or https:// address, for example http://127.0.0.1:8000.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "The address cannot contain a user name, password, query or fragment. Put an API key in the API key field.";
            return false;
        }
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 && !string.Equals(path, "/v1", StringComparison.OrdinalIgnoreCase))
        {
            error = "Give only the server address, optionally ending in /v1 (for example http://192.168.1.20:8000/v1).";
            return false;
        }

        bool localHost = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        bool isIp = IPAddress.TryParse(uri.Host, out IPAddress? address);
        if (!localHost && !isIp)
        {
            error = "Use localhost or the server's IP address (for example 192.168.1.20). Host names are not accepted "
                + "because they could resolve to an address outside your network.";
            return false;
        }
        if (isIp && !ExternalTextProviderService.IsLocalAddress(address!))
        {
            error = "That address is not on this PC or your private network. PaRuX only connects to a server it can reach locally.";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    private static bool IsLoopbackHost(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address)));

    private static string? ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        string trimmed = apiKey.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > 4096 || ContainsControl(trimmed) || trimmed.Any(char.IsWhiteSpace))
            throw new InvalidDataException("The API key must be a single line without spaces.");
        return trimmed;
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        string trimmed = model.Trim();
        if (trimmed.Length > 200 || ContainsControl(trimmed))
            throw new InvalidDataException("The model name is not valid.");
        return trimmed;
    }

    private static bool ContainsControl(string value) => value.Any(char.IsControl);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private Config? Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            Config? config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions);
            // A file edited by hand must not bypass the address rule.
            if (config == null || !TryNormalizeBaseUrl(config.BaseUrl, out string normalized, out _)) return null;
            config.BaseUrl = normalized;
            return config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private string? ReadKey(Config config)
    {
        if (string.IsNullOrEmpty(config.ProtectedApiKey)) return null;
        try { return _protector.Unprotect(SecretPurpose, config.ProtectedApiKey); }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- testing and model listing

    /// <summary>
    /// Probes <c>GET /v1/models</c>. With no candidate it tests the stored provider; with a candidate it tests that
    /// address without saving it (the stored key is used only when the address is unchanged).
    /// </summary>
    public async Task<OpenAiCompatibleTestResult> TestAsync(OpenAiCompatibleProviderRequest? candidate, CancellationToken ct)
    {
        Config? stored = Load();
        string baseUrl;
        string? apiKey;
        string? selected;
        if (candidate == null)
        {
            if (stored == null) return Fail(OpenAiCompatibleStates.InvalidConfig, "No server is configured yet.");
            baseUrl = stored.BaseUrl;
            apiKey = ReadKey(stored);
            selected = stored.SelectedModel;
        }
        else
        {
            if (!TryNormalizeBaseUrl(candidate.BaseUrl, out baseUrl, out string error))
                return Fail(OpenAiCompatibleStates.InvalidConfig, error);
            try { selected = NormalizeModel(candidate.SelectedModel); apiKey = ValidateApiKey(candidate.ApiKey); }
            catch (InvalidDataException ex) { return Fail(OpenAiCompatibleStates.InvalidConfig, ex.Message); }
            if (candidate.ApiKey == null)
                apiKey = stored != null && string.Equals(stored.BaseUrl, baseUrl, StringComparison.Ordinal) ? ReadKey(stored) : null;
        }

        OpenAiCompatibleTestResult result = await QueryModelsAsync(baseUrl, apiKey, ct).ConfigureAwait(false);
        if (result.State == OpenAiCompatibleStates.Ok)
        {
            if (selected != null && !result.Models.Contains(selected, StringComparer.Ordinal))
            {
                result.State = OpenAiCompatibleStates.ModelNotServed;
                result.Message = "The server is reachable but does not list the model \"" + selected + "\". Pick one of the listed models.";
            }
            if (apiKey != null && baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopbackHost(baseUrl))
                result.Warning = "The API key is sent over plain HTTP on your network. Use this only on a network you trust.";
        }
        return result;
    }

    private async Task<OpenAiCompatibleTestResult> QueryModelsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        using HttpClient http = ExternalTextProviderService.CreateProbeClient(ListTimeout, _resolver);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models");
        ApplyAuthorization(request, apiKey);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                return Fail(OpenAiCompatibleStates.Unauthorized, apiKey == null
                    ? "The server requires an API key. Enter it and test again."
                    : "The server rejected the API key (HTTP " + status + ").");
            }
            if (status is >= 300 and < 400)
                return Fail(OpenAiCompatibleStates.NotOpenAiCompatible, "The server answered with a redirect (HTTP " + status + "), which PaRuX does not follow.");
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
                return status == 404
                    ? Fail(OpenAiCompatibleStates.NotOpenAiCompatible, "The server has no /v1/models endpoint, so it does not look OpenAI-compatible.")
                    : Fail(OpenAiCompatibleStates.ServerError, "The server answered HTTP " + status + (detail.Length > 0 ? ": " + detail : "."));
            }

            string body = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            List<string>? models = ParseModels(body);
            if (models == null)
                return Fail(OpenAiCompatibleStates.NotOpenAiCompatible, "The server answered, but not with an OpenAI-style model list.");
            if (models.Count == 0)
                return Fail(OpenAiCompatibleStates.NoModels, "The server is reachable but reports no model. Load a model in the server first.");
            return new OpenAiCompatibleTestResult
            {
                State = OpenAiCompatibleStates.Ok,
                Message = models.Count == 1 ? "Connected. 1 model available." : "Connected. " + models.Count + " models available.",
                Models = models
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(OpenAiCompatibleStates.Timeout, "The server did not answer within " + (int)ListTimeout.TotalSeconds + " seconds.");
        }
        catch (HttpRequestException)
        {
            return Fail(OpenAiCompatibleStates.Unreachable,
                "Nothing answered at " + baseUrl + ". Check that the server is running and listening on that address and port.");
        }
    }

    internal static List<string>? ParseModels(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root || root["data"] is not JsonArray data) return null;
            var ids = new List<string>();
            foreach (JsonNode? item in data)
            {
                string? id = item?["id"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || ContainsControl(id)) continue;
                if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
                if (ids.Count >= MaxModels) break;
            }
            return ids;
        }
        catch (JsonException) { return null; }
    }

    private static OpenAiCompatibleTestResult Fail(string state, string message) =>
        new() { State = state, Message = message };

    private static void ApplyAuthorization(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) break;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string body = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            string? message = null;
            try { message = JsonNode.Parse(body)?["error"] is { } error ? (error is JsonValue v ? v.ToString() : error["message"]?.ToString()) : null; }
            catch (JsonException) { }
            message ??= body;
            message = new string(message.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
            return message.Length <= 300 ? message : message[..300] + "…";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { return string.Empty; }
    }

    // ---------------------------------------------------------------- chat

    /// <summary>The resolved target of one chat turn.</summary>
    internal sealed record ChatTarget(string BaseUrl, string Model, string? ApiKey, string DisplayName, bool IsLan);

    /// <summary>True when the user switched the provider on for chat.</summary>
    public bool IsActiveForChat() => Load() is { UseForChat: true };

    /// <summary>
    /// Checks the server is reachable and serves a model before a chat turn starts, so a failure is reported with a
    /// real status instead of an empty stream. Throws <see cref="OpenAiCompatibleProviderException"/>.
    /// </summary>
    internal async Task<ChatTarget> ResolveChatTargetAsync(CancellationToken ct)
    {
        Config config = Load() ?? throw new OpenAiCompatibleProviderException(
            OpenAiCompatibleStates.InvalidConfig, "The local OpenAI-compatible server is not configured.", StatusCodes.Status409Conflict);
        string? apiKey = ReadKey(config);
        if (!string.IsNullOrEmpty(config.ProtectedApiKey) && apiKey == null)
        {
            throw new OpenAiCompatibleProviderException(OpenAiCompatibleStates.Unauthorized,
                "The stored API key cannot be read on this account. Enter the key again in Settings.", StatusCodes.Status409Conflict);
        }

        OpenAiCompatibleTestResult probe = await QueryModelsAsync(config.BaseUrl, apiKey, ct).ConfigureAwait(false);
        if (probe.State != OpenAiCompatibleStates.Ok)
        {
            throw new OpenAiCompatibleProviderException(probe.State,
                config.DisplayName + ": " + probe.Message,
                probe.State == OpenAiCompatibleStates.NoModels ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway);
        }

        string model;
        if (config.SelectedModel != null)
        {
            if (!probe.Models.Contains(config.SelectedModel, StringComparer.Ordinal))
            {
                throw new OpenAiCompatibleProviderException(OpenAiCompatibleStates.ModelNotServed,
                    config.DisplayName + " does not list the model \"" + config.SelectedModel + "\" any more. Pick another model in Settings.",
                    StatusCodes.Status409Conflict);
            }
            model = config.SelectedModel;
        }
        else
        {
            model = probe.Models[0];
        }
        return new ChatTarget(config.BaseUrl, model, apiKey, config.DisplayName, !IsLoopbackHost(config.BaseUrl));
    }

    /// <summary>Wraps a resolved target as an engine for <see cref="LocalInferenceChatBridge"/>.</summary>
    internal ILocalInferenceEngine CreateEngine(ChatTarget target) => new OpenAiCompatibleLocalInferenceEngine(this, target);

    /// <summary>
    /// Streams one chat completion. Never throws for server problems: they come back as a final chunk with an error so
    /// the caller can show the real reason. Cancellation propagates.
    /// </summary>
    internal async IAsyncEnumerable<LocalInferenceChunk> StreamChatAsync(
        ChatTarget target, LocalInferenceRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using HttpClient http = ExternalTextProviderService.CreateProbeClient(Timeout.InfiniteTimeSpan, _resolver);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(FirstByteTimeout);

        var messages = new JsonArray();
        foreach ((string role, string content) in request.Messages)
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        var payload = new JsonObject { ["model"] = target.Model, ["messages"] = messages, ["stream"] = true };
        if (request.MaxOutputTokens > 0) payload["max_tokens"] = request.MaxOutputTokens;
        if (!double.IsNaN(request.Temperature)) payload["temperature"] = request.Temperature;

        using var message = new HttpRequestMessage(HttpMethod.Post, target.BaseUrl + "/v1/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
        ApplyAuthorization(message, target.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage? response = null;
        string? failure = null;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            failure = target.DisplayName + " did not start answering in time.";
        }
        catch (HttpRequestException)
        {
            failure = "Lost the connection to " + target.DisplayName + " at " + target.BaseUrl + ". Check that the server is still running.";
        }
        if (failure != null)
        {
            yield return new LocalInferenceChunk(null, null, false, failure);
            yield break;
        }

        using (response)
        {
            if (!response!.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                string detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
                string text = status is 401 or 403
                    ? target.DisplayName + " rejected the API key (HTTP " + status + ")."
                    : target.DisplayName + " answered HTTP " + status + (detail.Length > 0 ? ": " + detail : ".");
                yield return new LocalInferenceChunk(null, null, false, text);
                yield break;
            }

            Stream? body = null;
            try { body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or HttpRequestException) { }
            if (body == null)
            {
                yield return new LocalInferenceChunk(null, null, false, "The connection to " + target.DisplayName + " was lost.");
                yield break;
            }

            using var reader = new StreamReader(body, Encoding.UTF8);
            int total = 0;
            bool finished = false;
            while (true)
            {
                string? line;
                string? readError = null;
                try { line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    line = null;
                    readError = target.DisplayName + " stopped answering (no data for " + (int)IdleTimeout.TotalSeconds + " seconds).";
                }
                catch (IOException)
                {
                    line = null;
                    readError = "The connection to " + target.DisplayName + " was lost while answering.";
                }
                if (readError != null)
                {
                    yield return new LocalInferenceChunk(null, null, false, readError);
                    yield break;
                }
                if (line == null) break;
                idle.CancelAfter(IdleTimeout);

                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
                if (data.Length == 0) continue;
                if (data == "[DONE]")
                {
                    yield return new LocalInferenceChunk(null, null, true);
                    yield break;
                }

                JsonNode? node;
                try { node = JsonNode.Parse(data); }
                catch (JsonException) { continue; }
                if (node is not JsonObject chunk) continue;

                if (chunk["error"] is { } errorNode)
                {
                    string errorText = errorNode is JsonValue v ? v.ToString() : errorNode["message"]?.ToString() ?? "unknown error";
                    yield return new LocalInferenceChunk(null, null, false, target.DisplayName + " reported an error: " + Truncate(errorText, 300));
                    yield break;
                }

                JsonNode? choice = chunk["choices"] is JsonArray choices && choices.Count > 0 ? choices[0] : null;
                JsonNode? delta = choice?["delta"];
                string? content = StringOf(delta?["content"]);
                string? reasoning = StringOf(delta?["reasoning_content"]) ?? StringOf(delta?["reasoning"]);
                if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(reasoning))
                {
                    total += (content?.Length ?? 0) + (reasoning?.Length ?? 0);
                    if (total > MaxStreamChars)
                    {
                        yield return new LocalInferenceChunk(null, null, false, target.DisplayName + " produced more output than PaRuX accepts for one answer.");
                        yield break;
                    }
                    yield return new LocalInferenceChunk(content, reasoning, false);
                }
                if (choice?["finish_reason"] is { } reason && reason is JsonValue && !string.IsNullOrEmpty(reason.ToString()))
                    finished = true;
            }

            // Some servers close the stream after the last chunk without "[DONE]". That is complete only when a
            // finish reason was seen; otherwise the answer was cut off and must not be shown as finished.
            yield return finished
                ? new LocalInferenceChunk(null, null, true)
                : new LocalInferenceChunk(null, null, false, target.DisplayName + " ended the answer before finishing it.");
        }
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private sealed class Config
    {
        public string DisplayName { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string? ProtectedApiKey { get; set; }
        public string? SelectedModel { get; set; }
        public bool UseForChat { get; set; }
    }
}

/// <summary>
/// Adapts one resolved OpenAI-compatible server to the engine-neutral serving boundary so chat flows through
/// <see cref="LocalInferenceChatBridge"/>. It is deliberately NOT registered in <see cref="LocalInferenceEngineRegistry"/>:
/// it is never chosen by AUTO or by Engine Race, only by the user's explicit "use for chat" switch.
/// </summary>
internal sealed class OpenAiCompatibleLocalInferenceEngine : ILocalInferenceEngine
{
    private readonly OpenAiCompatibleProviderService _owner;
    private readonly OpenAiCompatibleProviderService.ChatTarget _target;

    internal OpenAiCompatibleLocalInferenceEngine(
        OpenAiCompatibleProviderService owner, OpenAiCompatibleProviderService.ChatTarget target)
    {
        _owner = owner;
        _target = target;
    }

    public LocalInferenceEngineDescriptor Descriptor { get; } = new(
        OpenAiCompatibleProviderService.EngineId,
        "Local OpenAI-compatible server",
        EngineImplementationState.Integrated,
        new HashSet<ManagedModelFormat>(),
        LocalInferenceCapabilities.Text,
        new HashSet<string>(),
        PaRuXManaged: false,
        SupportsManagedUpdates: false,
        Notes: "USER_MANAGED server on this PC or the private LAN. PaRuX only streams chat to it when the user turned it on.");

    public Task<LocalInferenceEngineStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new LocalInferenceEngineStatus(
            OpenAiCompatibleProviderService.EngineId, Installed: true, Healthy: true, InstalledVersion: null, ActiveModelVariantId: _target.Model));

    public Task EnsureReadyAsync(string modelVariantId, CancellationToken cancellationToken) => Task.CompletedTask;

    public IAsyncEnumerable<LocalInferenceChunk> StreamAsync(LocalInferenceRequest request, CancellationToken cancellationToken) =>
        _owner.StreamChatAsync(_target, request, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
