using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Gaming hard-off, refusal rules, error-text handling and shortcut target checks added after security review.</summary>
public sealed class OpenAiCompatibleSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-oai-safe-" + Guid.NewGuid().ToString("N"));
    private readonly List<HttpListener> _listeners = new();

    public OpenAiCompatibleSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (HttpListener l in _listeners) try { l.Abort(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Protector : ISecretProtector
    {
        public string Description => "test";
        public string Protect(string purpose, string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(purpose + "\0" + plaintext));
        public string? Unprotect(string purpose, string blob) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(blob)) is var d && d.StartsWith(purpose + "\0") ? d[(purpose.Length + 1)..] : null;
    }

    private (string Url, Func<int> Requests) Server(Func<HttpListenerContext, Task> handler)
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var listener = new HttpListener();
        string url = "http://127.0.0.1:" + port;
        listener.Prefixes.Add(url + "/");
        listener.Start();
        _listeners.Add(listener);
        int count = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                Interlocked.Increment(ref count);
                _ = Task.Run(async () => { try { await handler(ctx); } catch { try { ctx.Response.Abort(); } catch { } } });
            }
        });
        return (url, () => Volatile.Read(ref count));
    }

    private static Task Json(HttpListenerContext c, int status, string body)
    {
        c.Response.StatusCode = status;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        c.Response.OutputStream.Write(bytes, 0, bytes.Length);
        c.Response.Close();
        return Task.CompletedTask;
    }

    private OpenAiCompatibleProviderService Service(string url, Func<bool>? gaming = null)
    {
        var service = new OpenAiCompatibleProviderService(_dir, new Protector(), null)
        {
            GamingActive = gaming ?? (() => false),
            GamingPollInterval = TimeSpan.FromMilliseconds(30)
        };
        service.Save(new OpenAiCompatibleProviderRequest { BaseUrl = url, DisplayName = "Fake", UseForChat = true });
        return service;
    }

    // ------------------------------------------------------------------ M1: Gaming hard-off

    [Fact]
    public async Task While_gaming_is_active_chat_is_refused_with_409_and_nothing_is_sent()
    {
        (string url, Func<int> requests) = Server(c => Json(c, 200, "{\"data\":[{\"id\":\"m\"}]}"));
        OpenAiCompatibleProviderService service = Service(url, () => true);

        var ex = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(() => service.ResolveChatTargetAsync(CancellationToken.None));

        Assert.Equal(OpenAiCompatibleStates.GamingActive, ex.State);
        Assert.Equal(StatusCodes.Status409Conflict, ex.HttpStatus);
        Assert.Contains("Gaming Mode is active", ex.Message);
        Assert.Equal(0, requests());
    }

    [Fact]
    public async Task While_gaming_is_active_the_owner_test_does_not_dial_out_either()
    {
        (string url, Func<int> requests) = Server(c => Json(c, 200, "{\"data\":[{\"id\":\"m\"}]}"));
        OpenAiCompatibleProviderService service = Service(url, () => true);

        OpenAiCompatibleTestResult stored = await service.TestAsync(null, CancellationToken.None);
        OpenAiCompatibleTestResult candidate = await service.TestAsync(new OpenAiCompatibleProviderRequest { BaseUrl = url }, CancellationToken.None);

        Assert.Equal(OpenAiCompatibleStates.GamingActive, stored.State);
        Assert.Equal(OpenAiCompatibleStates.GamingActive, candidate.State);
        Assert.Equal(0, requests());
    }

    [Fact]
    public async Task Gaming_starting_mid_stream_cancels_the_stream_with_a_clear_message()
    {
        bool gaming = false;
        var firstChunkSent = new TaskCompletionSource();
        (string url, _) = Server(async c =>
        {
            if (c.Request.Url!.AbsolutePath == "/v1/models") { await Json(c, 200, "{\"data\":[{\"id\":\"m\"}]}"); return; }
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/event-stream";
            await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"));
            await c.Response.OutputStream.FlushAsync();
            firstChunkSent.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(20));
            try { c.Response.Abort(); } catch { }
        });
        OpenAiCompatibleProviderService service = Service(url, () => Volatile.Read(ref gaming));
        var target = await service.ResolveChatTargetAsync(CancellationToken.None);
        var request = new LocalInferenceRequest(target.Model, new List<(string, string)> { ("user", "hi") }, 0, double.NaN, true);

        var chunks = new List<LocalInferenceChunk>();
        var pump = Task.Run(async () =>
        {
            await foreach (LocalInferenceChunk chunk in service.StreamChatAsync(target, request, CancellationToken.None))
                chunks.Add(chunk);
        });
        await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Write(ref gaming, true);
        await pump.WaitAsync(TimeSpan.FromSeconds(10)); // ends quickly, not after 20 s

        Assert.Contains(chunks, c => c.Content == "hi");
        LocalInferenceChunk last = chunks[^1];
        Assert.False(last.Completed);
        Assert.Contains("Gaming Mode started", last.Error);
    }

    [Fact]
    public async Task A_stream_that_starts_while_gaming_is_active_ends_at_once()
    {
        (string url, _) = Server(async c =>
        {
            if (c.Request.Url!.AbsolutePath == "/v1/models") { await Json(c, 200, "{\"data\":[{\"id\":\"m\"}]}"); return; }
            await Task.Delay(TimeSpan.FromSeconds(20));
            try { c.Response.Abort(); } catch { }
        });
        bool gaming = false;
        OpenAiCompatibleProviderService service = Service(url, () => Volatile.Read(ref gaming));
        var target = await service.ResolveChatTargetAsync(CancellationToken.None);
        Volatile.Write(ref gaming, true);

        var chunks = new List<LocalInferenceChunk>();
        await foreach (LocalInferenceChunk c in service.StreamChatAsync(
            target, new LocalInferenceRequest(target.Model, new List<(string, string)> { ("user", "x") }, 0, double.NaN, true), CancellationToken.None)
            .WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
            chunks.Add(c);

        Assert.Contains("Gaming Mode started", Assert.Single(chunks).Error);
    }

    // ------------------------------------------------------------------ refusal rules (also tool use)

    [Fact]
    public void Refusal_rules_cover_gaming_tool_use_images_documents_and_continue()
    {
        Assert.Null(ChatProxyService.OpenAiCompatibleRefusal(false, false, false, false, false));

        var gaming = ChatProxyService.OpenAiCompatibleRefusal(true, true, true, true, true);
        Assert.Equal(OpenAiCompatibleStates.GamingActive, gaming!.Value.Type); // gaming wins over every other reason

        Assert.Contains("Tool use", ChatProxyService.OpenAiCompatibleRefusal(false, false, true, false, false)!.Value.Message);
        Assert.Contains("Images", ChatProxyService.OpenAiCompatibleRefusal(false, true, false, false, false)!.Value.Message);
        Assert.Contains("Document", ChatProxyService.OpenAiCompatibleRefusal(false, false, false, true, false)!.Value.Message);
        Assert.Contains("Continuing", ChatProxyService.OpenAiCompatibleRefusal(false, false, false, false, true)!.Value.Message);
        Assert.Equal("openai_compatible_unsupported", ChatProxyService.OpenAiCompatibleRefusal(false, false, true, false, false)!.Value.Type);
    }

    // ------------------------------------------------------------------ m2: server text not reflected

    [Fact]
    public async Task Chat_target_failures_do_not_carry_the_servers_error_text()
    {
        (string url, _) = Server(c => Json(c, 500, "{\"error\":{\"message\":\"secret internal path C:\\\\models\\\\x\"}}"));
        OpenAiCompatibleProviderService service = Service(url);

        var ex = await Assert.ThrowsAsync<OpenAiCompatibleProviderException>(() => service.ResolveChatTargetAsync(CancellationToken.None));

        Assert.Equal(OpenAiCompatibleStates.ServerError, ex.State);
        Assert.DoesNotContain("secret internal path", ex.Message);
    }

    [Fact]
    public async Task The_owner_test_still_shows_the_servers_detail_bounded()
    {
        (string url, _) = Server(c => Json(c, 500, "{\"error\":{\"message\":\"" + new string('x', 900) + "\"}}"));
        OpenAiCompatibleProviderService service = Service(url);

        OpenAiCompatibleTestResult result = await service.TestAsync(null, CancellationToken.None);

        Assert.Equal(OpenAiCompatibleStates.ServerError, result.State);
        Assert.Contains("xxx", result.Message);
        Assert.True(result.Message.Length < 400);
    }

    [Theory]
    [InlineData("169.254.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:169.254.1.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("127.0.0.1", false)]
    public void Link_local_classifier(string address, bool expected) =>
        Assert.Equal(expected, OpenAiCompatibleProviderService.IsLinkLocal(IPAddress.Parse(address)));

    // ------------------------------------------------------------------ m3: shortcut target

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\RBG.PaRuX_4.4.0.0_x64__abc\PaRuX.exe", ShortcutExecutableProblem.MicrosoftStore)]
    [InlineData(@"c:\program files\windowsapps\x\PaRuX.exe", ShortcutExecutableProblem.MicrosoftStore)]
    [InlineData(@"C:\Tools\notepad.exe", ShortcutExecutableProblem.NotPaRuXExe)]
    [InlineData(@"C:\Tools\PaRuX.App.exe", ShortcutExecutableProblem.NotPaRuXExe)]
    [InlineData(@"PaRuX.exe", ShortcutExecutableProblem.NotFound)]
    [InlineData("", ShortcutExecutableProblem.NotFound)]
    [InlineData(null, ShortcutExecutableProblem.NotFound)]
    [InlineData(@"C:\definitely\missing\PaRuX.exe", ShortcutExecutableProblem.NotFound)]
    public void Shortcut_target_validation_is_a_pure_check(string? path, ShortcutExecutableProblem expected)
    {
        Assert.Equal(expected, GameShortcuts.CheckExecutable(path));
        Assert.Equal(expected == ShortcutExecutableProblem.None, GameShortcuts.ExecutableProblem(path) is null);
    }

    [Fact]
    public void An_existing_paRuX_exe_outside_windows_apps_is_accepted()
    {
        string exe = Path.Combine(_dir, "PaRuX.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        Assert.Equal(ShortcutExecutableProblem.None, GameShortcuts.CheckExecutable(exe));
    }

    [Fact]
    public void A_store_install_gets_no_desktop_shortcut_and_no_steam_entry()
    {
        string desktop = Path.Combine(_dir, "desktop");
        Directory.CreateDirectory(desktop);
        string store = @"C:\Program Files\WindowsApps\RBG.PaRuX_1.0.0.0_x64__x\PaRuX.exe";

        GameShortcutResult lnk = GameShortcuts.CreateInFolder(desktop, "Game", "gkey", store);

        Assert.Equal(GameShortcutOutcome.InvalidRequest, lnk.Outcome);
        Assert.Contains("Microsoft Store", lnk.Message);
        Assert.Empty(Directory.GetFiles(desktop));

        string config = Path.Combine(_dir, "steam", "userdata", "1", "config");
        Directory.CreateDirectory(config);
        string vdf = Path.Combine(config, "shortcuts.vdf");
        var entry = new SteamShortcutEntry("Game", "\"" + store + "\"", "\"C:\\x\"", "--play gkey", store);
        SteamShortcutResult steam = SteamShortcutsFile.TryAppend(vdf, entry, () => false);
        Assert.Equal(SteamShortcutOutcome.InvalidRequest, steam.Outcome);
        Assert.False(File.Exists(vdf));
    }
}
