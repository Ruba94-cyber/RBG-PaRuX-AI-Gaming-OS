using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The generic local OpenAI-compatible provider: address rules, key handling, model listing, honest failure states
/// and chat streaming through the inference bridge. All servers are loopback fakes with synthetic content.
/// </summary>
public sealed class OpenAiCompatibleProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-oai-" + Guid.NewGuid().ToString("N"));
    private readonly List<FakeServer> _servers = new();

    public OpenAiCompatibleProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (FakeServer server in _servers) server.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private OpenAiCompatibleProviderService NewService() =>
        new(_dir, new ReversibleProtector(), resolver: null);

    private FakeServer Start(Func<HttpListenerContext, Task> handler)
    {
        var server = new FakeServer(handler);
        _servers.Add(server);
        return server;
    }

    private static Task Json(HttpListenerContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
        return Task.CompletedTask;
    }

    private static async Task Sse(HttpListenerContext context, params string[] lines)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        foreach (string line in lines)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n\n");
            await context.Response.OutputStream.WriteAsync(bytes);
            await context.Response.OutputStream.FlushAsync();
        }
        context.Response.Close();
    }

    private const string ModelList = "{\"object\":\"list\",\"data\":[{\"id\":\"alpha-7b\"},{\"id\":\"beta-13b\"}]}";

    private static string Chunk(string field, string text) =>
        "data: {\"choices\":[{\"delta\":{\"" + field + "\":\"" + text + "\"}}]}";

    private static OpenAiCompatibleProviderRequest Request(string url, string? key = null, string? model = null, bool use = true) =>
        new() { DisplayName = "Fake", BaseUrl = url, ApiKey = key, SelectedModel = model, UseForChat = use };

    // ------------------------------------------------------------------ address rules

    [Theory]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000")]
    [InlineData("http://127.0.0.1:8000/v1", "http://127.0.0.1:8000")]
    [InlineData("http://localhost:5001/", "http://localhost:5001")]
    [InlineData("http://localhost:1234/V1/", "http://localhost:1234")]
    [InlineData("http://192.168.1.20:5000", "http://192.168.1.20:5000")]
    [InlineData("https://10.0.0.8:8443/v1", "https://10.0.0.8:8443")]
    [InlineData("http://[::1]:8000", "http://[::1]:8000")]
    public void Loopback_and_private_lan_addresses_are_accepted(string input, string expected)
    {
        Assert.True(OpenAiCompatibleProviderService.TryNormalizeBaseUrl(input, out string normalized, out string error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("https://api.openai.com")]
    [InlineData("http://8.8.8.8:8000")]
    [InlineData("http://example.com:8000")]
    [InlineData("http://my-nas.local:8000")]
    [InlineData("http://user:secret@127.0.0.1:8000")]
    [InlineData("http://127.0.0.1:8000?key=abc")]
    [InlineData("http://127.0.0.1:8000/#frag")]
    [InlineData("http://127.0.0.1:8000/api/chat")]
    [InlineData("http://172.32.0.1:8000")]
    [InlineData("http://0.0.0.0:8000")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://169.254.1.20:8000")]
    [InlineData("http://[fe80::1]:8000")]
    public void Public_named_or_ambiguous_addresses_are_refused_with_a_reason(string input)
    {
        Assert.False(OpenAiCompatibleProviderService.TryNormalizeBaseUrl(input, out string normalized, out string error));
        Assert.Equal(string.Empty, normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ------------------------------------------------------------------ configuration and secrets

    [Fact]
    public void Save_stores_status_without_the_key_and_encrypts_it_on_disk()
    {
        OpenAiCompatibleProviderService service = NewService();
        OpenAiCompatibleProviderStatus status = service.Save(Request("http://127.0.0.1:8000/v1", key: "sk-test-123", model: "alpha-7b"));

        Assert.True(status.Configured);
        Assert.True(status.HasApiKey);
        Assert.False(status.IsLanHost);
        Assert.Equal("http://127.0.0.1:8000", status.BaseUrl);
        Assert.Equal("USER_MANAGED", status.Ownership);
        Assert.Equal("alpha-7b", status.SelectedModel);
        Assert.True(status.UseForChat);

        string disk = File.ReadAllText(Path.Combine(_dir, "openai-compatible-provider.json"));
        Assert.DoesNotContain("sk-test-123", disk);
        Assert.True(service.IsActiveForChat());
    }

    [Fact]
    public void Lan_address_is_reported_as_lan()
    {
        OpenAiCompatibleProviderStatus status = NewService().Save(Request("http://192.168.1.20:5000"));
        Assert.True(status.IsLanHost);
    }

    [Fact]
    public void Save_refuses_an_unsafe_address_and_stores_nothing()
    {
        OpenAiCompatibleProviderService service = NewService();
        Assert.Throws<InvalidDataException>(() => service.Save(Request("https://api.openai.com", key: "sk-x")));
        Assert.False(service.GetStatus().Configured);
        Assert.False(File.Exists(Path.Combine(_dir, "openai-compatible-provider.json")));
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("bad\nkey")]
    public void Save_refuses_a_key_that_could_inject_headers(string key)
    {
        Assert.Throws<InvalidDataException>(() => NewService().Save(Request("http://127.0.0.1:8000", key: key)));
    }

    [Fact]
    public async Task Stored_key_is_kept_for_the_same_address_replaced_cleared_and_dropped_on_a_new_address()
    {
        string? seenAuth = null;
        FakeServer server = Start(ctx => { seenAuth = ctx.Request.Headers["Authorization"]; return Json(ctx, 200, ModelList); });
        OpenAiCompatibleProviderService service = NewService();

        service.Save(Request(server.Url, key: "sk-one"));
        service.Save(Request(server.Url, key: null));           // null keeps
        Assert.True(service.GetStatus().HasApiKey);
        await service.TestAsync(null, CancellationToken.None);
        Assert.Equal("Bearer sk-one", seenAuth);

        service.Save(Request(server.Url, key: "sk-two"));       // replaces
        await service.TestAsync(null, CancellationToken.None);
        Assert.Equal("Bearer sk-two", seenAuth);

        service.Save(Request(server.Url, key: ""));             // clears
        Assert.False(service.GetStatus().HasApiKey);

        service.Save(Request(server.Url, key: "sk-three"));
        service.Save(Request("http://127.0.0.1:59999", key: null)); // new address drops the old key
        Assert.False(service.GetStatus().HasApiKey);
    }

    [Fact]
    public void A_hand_edited_config_with_a_public_address_is_ignored()
    {
        File.WriteAllText(Path.Combine(_dir, "openai-compatible-provider.json"),
            "{\"displayName\":\"x\",\"baseUrl\":\"https://api.openai.com\",\"useForChat\":true}");
        OpenAiCompatibleProviderService service = NewService();
        Assert.False(service.GetStatus().Configured);
        Assert.False(service.IsActiveForChat());
    }

    [Fact]
    public void Clear_removes_the_provider_and_chat_stops_using_it()
    {
        OpenAiCompatibleProviderService service = NewService();
        service.Save(Request("http://127.0.0.1:8000", key: "sk-x"));
        service.Clear();
        Assert.False(service.GetStatus().Configured);
        Assert.False(service.IsActiveForChat());
        service.Clear(); // idempotent
    }

    [Fact]
    public void Chat_is_off_unless_the_user_turned_it_on()
    {
        OpenAiCompatibleProviderService service = NewService();
        service.Save(Request("http://127.0.0.1:8000", use: false));
        Assert.True(service.GetStatus().Configured);
        Assert.False(service.IsActiveForChat());
    }

    // ------------------------------------------------------------------ testing and listing

    [Fact]
    public async Task Test_lists_models_and_sends_the_key_only_to_the_configured_address()
    {
        string? auth = null;
        string? path = null;
        FakeServer server = Start(ctx => { auth = ctx.Request.Headers["Authorization"]; path = ctx.Request.Url!.AbsolutePath; return Json(ctx, 200, ModelList); });

        OpenAiCompatibleTestResult result = await NewService().TestAsync(Request(server.Url + "/v1", key: "sk-abc"), CancellationToken.None);

        Assert.Equal(OpenAiCompatibleStates.Ok, result.State);
        Assert.Equal(new[] { "alpha-7b", "beta-13b" }, result.Models);
        Assert.Equal("Bearer sk-abc", auth);
        Assert.Equal("/v1/models", path);
    }

    [Fact]
    public async Task Test_reports_unauthorized_with_and_without_a_key()
    {
        FakeServer server = Start(ctx => Json(ctx, 401, "{\"error\":\"nope\"}"));
        OpenAiCompatibleProviderService service = NewService();

        OpenAiCompatibleTestResult without = await service.TestAsync(Request(server.Url), CancellationToken.None);
        OpenAiCompatibleTestResult with = await service.TestAsync(Request(server.Url, key: "sk-bad"), CancellationToken.None);

        Assert.Equal(OpenAiCompatibleStates.Unauthorized, without.State);
        Assert.Contains("requires an API key", without.Message);
        Assert.Equal(OpenAiCompatibleStates.Unauthorized, with.State);
        Assert.Contains("rejected the API key", with.Message);
        Assert.DoesNotContain("sk-bad", with.Message);
    }

    [Fact]
    public async Task Test_reports_unreachable_when_nothing_listens()
    {
        int port = FreePort();
        OpenAiCompatibleTestResult result = await NewService().TestAsync(Request("http://127.0.0.1:" + port), CancellationToken.None);
        Assert.Equal(OpenAiCompatibleStates.Unreachable, result.State);
        Assert.Contains("Nothing answered", result.Message);
    }

    [Theory]
    [InlineData(404, "{}", OpenAiCompatibleStates.NotOpenAiCompatible)]
    [InlineData(200, "<html>hello</html>", OpenAiCompatibleStates.NotOpenAiCompatible)]
    [InlineData(200, "{\"models\":[]}", OpenAiCompatibleStates.NotOpenAiCompatible)]
    [InlineData(200, "{\"data\":[]}", OpenAiCompatibleStates.NoModels)]
    [InlineData(500, "{\"error\":{\"message\":\"boom\"}}", OpenAiCompatibleStates.ServerError)]
    public async Task Test_classifies_what_the_server_answered(int status, string body, string expected)
    {
        FakeServer server = Start(ctx => Json(ctx, status, body));
        OpenAiCompatibleTestResult result = await NewService().TestAsync(Request(server.Url), CancellationToken.None);
        Assert.Equal(expected, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Test_does_not_follow_a_redirect()
    {
        int targetHits = 0;
        FakeServer target = Start(ctx => { targetHits++; return Json(ctx, 200, ModelList); });
        FakeServer redirector = Start(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = target.Url + "/v1/models";
            ctx.Response.Close();
            return Task.CompletedTask;
        });

        OpenAiCompatibleTestResult result = await NewService().TestAsync(Request(redirector.Url), CancellationToken.None);

        Assert.Equal(OpenAiCompatibleStates.NotOpenAiCompatible, result.State);
        Assert.Equal(0, targetHits);
    }

    [Fact]
    public async Task Test_flags_a_selected_model_the_server_does_not_list()
    {
        FakeServer server = Start(ctx => Json(ctx, 200, ModelList));
        OpenAiCompatibleTestResult result = await NewService().TestAsync(Request(server.Url, model: "missing"), CancellationToken.None);
        Assert.Equal(OpenAiCompatibleStates.ModelNotServed, result.State);
        Assert.Equal(2, result.Models.Count);
    }

    [Fact]
    public async Task Test_of_an_invalid_candidate_never_dials_out()
    {
        OpenAiCompatibleTestResult result = await NewService().TestAsync(Request("https://api.openai.com"), CancellationToken.None);
        Assert.Equal(OpenAiCompatibleStates.InvalidConfig, result.State);
    }

    [Fact]
    public async Task Test_with_nothing_configured_says_so()
    {
        OpenAiCompatibleTestResult result = await NewService().TestAsync(null, CancellationToken.None);
        Assert.Equal(OpenAiCompatibleStates.InvalidConfig, result.State);
    }

    [Fact]
    public async Task Test_can_be_cancelled()
    {
        var gate = new TaskCompletionSource();
        FakeServer server = Start(async ctx => { await gate.Task; ctx.Response.Close(); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewService().TestAsync(Request(server.Url), cts.Token));
        gate.SetResult();
    }

    [Fact]
    public void Model_list_parser_is_bounded_and_tolerant()
    {
        Assert.Null(OpenAiCompatibleProviderService.ParseModels("not json"));
        Assert.Null(OpenAiCompatibleProviderService.ParseModels("[]"));
        Assert.Equal(new[] { "a" }, OpenAiCompatibleProviderService.ParseModels("{\"data\":[{\"id\":\"a\"},{\"id\":\"a\"},{\"x\":1},{\"id\":42},{\"id\":\"\"}]}"));
        string many = "{\"data\":[" + string.Join(",", Enumerable.Range(0, 500).Select(i => "{\"id\":\"m" + i + "\"}")) + "]}";
        Assert.Equal(200, OpenAiCompatibleProviderService.ParseModels(many)!.Count);
    }

    // ------------------------------------------------------------------ chat target

    [Fact]
    public async Task Chat_target_uses_the_selected_model_or_the_first_listed_one()
    {
        FakeServer server = Start(ctx => Json(ctx, 200, ModelList));
        OpenAiCompatibleProviderService service = NewService();

        service.Save(Request(server.Url));
        Assert.Equal("alpha-7b", (await service.ResolveChatTargetAsync(CancellationToken.None)).Model);

        service.Save(Request(server.Url, model: "beta-13b"));
        OpenAiCompatibleProviderService.ChatTarget target = await service.ResolveChatTargetAsync(CancellationToken.None);
        Assert.Equal("beta-13b", target.Model);
        Assert.False(target.IsLan);
    }

    [Fact]
    public async Task Chat_target_fails_honestly_when_the_model_server_or_key_is_wrong()
    {
        OpenAiCompatibleProviderService service = NewService();

        FakeServer ok = Start(ctx => Json(ctx, 200, ModelList));
        service.Save(Request(ok.Url, model: "gone"));
        var missing = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(() => service.ResolveChatTargetAsync(CancellationToken.None));
        Assert.Equal(OpenAiCompatibleStates.ModelNotServed, missing.State);
        Assert.Equal(StatusCodes.Status409Conflict, missing.HttpStatus);

        service.Save(Request("http://127.0.0.1:" + FreePort()));
        var down = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(() => service.ResolveChatTargetAsync(CancellationToken.None));
        Assert.Equal(OpenAiCompatibleStates.Unreachable, down.State);
        Assert.Equal(StatusCodes.Status502BadGateway, down.HttpStatus);

        FakeServer locked = Start(ctx => Json(ctx, 401, "{}"));
        service.Save(Request(locked.Url));
        var denied = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(() => service.ResolveChatTargetAsync(CancellationToken.None));
        Assert.Equal(OpenAiCompatibleStates.Unauthorized, denied.State);

        FakeServer empty = Start(ctx => Json(ctx, 200, "{\"data\":[]}"));
        service.Save(Request(empty.Url));
        var none = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(() => service.ResolveChatTargetAsync(CancellationToken.None));
        Assert.Equal(OpenAiCompatibleStates.NoModels, none.State);
    }

    [Fact]
    public async Task Chat_target_without_a_config_is_a_conflict_not_a_crash()
    {
        var ex = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(() => NewService().ResolveChatTargetAsync(CancellationToken.None));
        Assert.Equal(OpenAiCompatibleStates.InvalidConfig, ex.State);
    }

    // ------------------------------------------------------------------ streaming through the bridge

    private async Task<(string Sse, LocalInferenceChatStreamResult Result, string? RequestBody, string? Auth)> RunChat(
        Func<HttpListenerContext, Task> chatHandler, Action<OpenAiCompatibleProviderService>? tune = null,
        ChatCompletionRequest? chat = null, CancellationToken ct = default)
    {
        string? body = null;
        string? auth = null;
        FakeServer server = Start(async ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath == "/v1/models") { await Json(ctx, 200, ModelList); return; }
            using var reader = new StreamReader(ctx.Request.InputStream);
            body = await reader.ReadToEndAsync();
            auth = ctx.Request.Headers["Authorization"];
            await chatHandler(ctx);
        });
        OpenAiCompatibleProviderService service = NewService();
        tune?.Invoke(service);
        service.Save(Request(server.Url, key: "sk-chat", model: "alpha-7b"));
        OpenAiCompatibleProviderService.ChatTarget target = await service.ResolveChatTargetAsync(ct);

        chat ??= new ChatCompletionRequest
        {
            Messages = new List<ChatCompletionMessage>
            {
                new() { Role = "system", Content = "Be brief." },
                new() { Role = "user", Content = "Say hello." }
            }
        };
        var http = new DefaultHttpContext();
        var buffer = new MemoryStream();
        http.Response.Body = buffer;
        LocalInferenceChatStreamResult result = await LocalInferenceChatBridge.StreamExternalProviderAsync(
            service.CreateEngine(target),
            LocalInferenceChatBridge.BuildExternalProviderRequest(chat, target.Model),
            http.Response, ct);
        return (Encoding.UTF8.GetString(buffer.ToArray()), result, body, auth);
    }

    [Fact]
    public async Task Chat_streams_content_and_reasoning_through_the_bridge_and_completes()
    {
        var run = await RunChat(ctx => Sse(ctx,
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":null}}]}",
            Chunk("reasoning_content", "think"),
            Chunk("content", "Hel"),
            Chunk("content", "lo"),
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]"));

        Assert.True(run.Result.Completed);
        Assert.Contains("\"reasoning_content\":\"think\"", run.Sse);
        Assert.Contains("\"content\":\"Hel\"", run.Sse);
        Assert.Contains("\"content\":\"lo\"", run.Sse);
        Assert.EndsWith("data: [DONE]\n\n", run.Sse);
        Assert.Equal("Bearer sk-chat", run.Auth);
        Assert.Contains("\"model\":\"alpha-7b\"", run.RequestBody);
        Assert.Contains("\"stream\":true", run.RequestBody);
        Assert.Contains("Say hello.", run.RequestBody);
        // Nothing PaRuX-specific is imposed on another server's limits.
        Assert.DoesNotContain("max_tokens", run.RequestBody);
        Assert.DoesNotContain("temperature", run.RequestBody);
    }

    [Fact]
    public async Task Chat_forwards_only_the_sampling_values_the_caller_set()
    {
        var chat = new ChatCompletionRequest
        {
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "hi" } },
            MaxTokens = 64,
            Temperature = 0.2
        };
        var run = await RunChat(ctx => Sse(ctx, Chunk("content", "x"), "data: [DONE]"), chat: chat);
        Assert.Contains("\"max_tokens\":64", run.RequestBody);
        Assert.Contains("\"temperature\":0.2", run.RequestBody);
    }

    [Fact]
    public async Task Chat_treats_a_finish_reason_without_done_as_complete()
    {
        var run = await RunChat(ctx => Sse(ctx, Chunk("content", "ok"),
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}"));
        Assert.True(run.Result.Completed);
        Assert.EndsWith("data: [DONE]\n\n", run.Sse);
    }

    [Fact]
    public async Task Chat_reports_a_cut_off_answer_instead_of_showing_it_as_finished()
    {
        var run = await RunChat(ctx => Sse(ctx, Chunk("content", "partial")));
        Assert.False(run.Result.Completed);
        Assert.Contains("\"content\":\"partial\"", run.Sse);
        Assert.Contains("openai_compatible_provider_error", run.Sse);
        Assert.Contains("ended the answer before finishing", run.Sse);
    }

    [Fact]
    public async Task Chat_surfaces_an_http_error_from_the_server_with_its_message()
    {
        var run = await RunChat(ctx => Json(ctx, 400, "{\"error\":{\"message\":\"context length exceeded\"}}"));
        Assert.False(run.Result.Completed);
        Assert.False(run.Result.CanFallbackToBaseline); // never silently answered by another model
        Assert.DoesNotContain("context length exceeded", run.Sse); // the server's text is not reflected to chat callers
        Assert.Contains("HTTP 400", run.Sse);
    }

    [Fact]
    public async Task Chat_surfaces_an_error_event_sent_inside_the_stream()
    {
        var run = await RunChat(ctx => Sse(ctx, Chunk("content", "a"), "data: {\"error\":{\"message\":\"out of memory\"}}"));
        Assert.False(run.Result.Completed);
        Assert.DoesNotContain("out of memory", run.Sse);
        Assert.Contains("reported an error", run.Sse);
    }

    [Fact]
    public async Task Chat_reports_a_stalled_server_after_the_idle_timeout()
    {
        var gate = new TaskCompletionSource();
        var run = await RunChat(async ctx =>
        {
            context_start(ctx);
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Chunk("content", "a") + "\n\n"));
            await ctx.Response.OutputStream.FlushAsync();
            await Task.WhenAny(gate.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            try { ctx.Response.Abort(); } catch { }
        }, tune: service => service.IdleTimeout = TimeSpan.FromMilliseconds(300));
        gate.TrySetResult();

        Assert.False(run.Result.Completed);
        Assert.Contains("stopped answering", run.Sse);

        static void context_start(HttpListenerContext c) { c.Response.StatusCode = 200; c.Response.ContentType = "text/event-stream"; }
    }

    [Fact]
    public async Task Chat_cancellation_propagates_and_stops_reading()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        Task<(string, LocalInferenceChatStreamResult, string?, string?)> chat = RunChat(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Chunk("content", "a") + "\n\n"));
            await ctx.Response.OutputStream.FlushAsync();
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(10));
            try { ctx.Response.Abort(); } catch { }
        }, ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat);
    }

    [Fact]
    public async Task Chat_reports_a_connection_lost_before_any_answer()
    {
        var run = await RunChat(ctx => { ctx.Response.Abort(); return Task.CompletedTask; });
        Assert.False(run.Result.Completed);
        Assert.Contains("openai_compatible_provider_error", run.Sse);
    }

    [Fact]
    public void Bridge_request_leaves_server_defaults_unless_set()
    {
        var chat = new ChatCompletionRequest { Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "q" } } };
        LocalInferenceRequest request = LocalInferenceChatBridge.BuildExternalProviderRequest(chat, "m");
        Assert.Equal(0, request.MaxOutputTokens);
        Assert.True(double.IsNaN(request.Temperature));
        Assert.Equal("m", request.ModelVariantId);

        chat.MaxCompletionTokens = 10;
        chat.MaxTokens = 20;
        Assert.Equal(10, LocalInferenceChatBridge.BuildExternalProviderRequest(chat, "m").MaxOutputTokens);
    }

    [Fact]
    public void The_external_engine_is_not_part_of_the_auto_registry()
    {
        var registry = new LocalInferenceEngineRegistry();
        Assert.Null(registry.FindDescriptor(OpenAiCompatibleProviderService.EngineId));
    }

    // ------------------------------------------------------------------ helpers

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public FakeServer(Func<HttpListenerContext, Task> handler)
        {
            int port = FreePort();
            Url = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        try { await handler(context); }
                        catch { try { context.Response.Abort(); } catch { } }
                    });
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Abort(); } catch { }
        }
    }

    private sealed class ReversibleProtector : ISecretProtector
    {
        public string Description => "test-only reversible encoding";

        public string Protect(string purpose, string plaintext) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(purpose + "\u0000" + plaintext));

        public string? Unprotect(string purpose, string blob)
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
                string prefix = purpose + "\u0000";
                return decoded.StartsWith(prefix, StringComparison.Ordinal) ? decoded[prefix.Length..] : null;
            }
            catch (FormatException) { return null; }
        }
    }
}
