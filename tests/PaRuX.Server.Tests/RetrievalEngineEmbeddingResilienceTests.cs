using System.Diagnostics;
using System.Net.Http;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Embedding llama-server stays reachable during indexing on a slow, busy PC. The server is a small PowerShell listener (no
/// model, no GPU). Root cause reproduced here: a /health answer slower than the former 500 ms limit made the next batch
/// kill and restart a live server.
/// </summary>
public sealed class RetrievalEngineEmbeddingResilienceTests : IDisposable
{
    private const string FakeServer = @"
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, __PORT__)
$l.Start()
while ($true) {
  $c = $l.AcceptTcpClient()
  $s = $c.GetStream()
  $buf = New-Object byte[] 65536
  $n = $s.Read($buf, 0, 65536)
  $req = [Text.Encoding]::ASCII.GetString($buf, 0, $n)
  if ($req -match 'Content-Length: (\d+)') {
    $need = [int]$Matches[1]
    $have = $n - ($req.IndexOf(""`r`n`r`n"") + 4)
    while ($have -lt $need) { $have += $s.Read($buf, 0, 65536) }
  }
  $p = '/'
  if ($req -match '^(GET|POST) (\S+)') { $p = $Matches[2] }
  if ($p -eq '/health') { Start-Sleep -Milliseconds __HEALTHMS__; $b = '{""status"":""ok""}' }
  elseif ($p -eq '/v1/embeddings') { $b = '{""data"":[{""index"":0,""embedding"":[3.0,4.0]}]}' }
  else { $b = '{}' }
  $bytes = [Text.Encoding]::UTF8.GetBytes($b)
  $h = [Text.Encoding]::ASCII.GetBytes(""HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n"")
  $s.Write($h, 0, $h.Length)
  $s.Write($bytes, 0, $bytes.Length)
  $s.Flush()
  $c.Close()
}";

    private readonly string _modelPath = Path.Combine(Path.GetTempPath(), "parux-embed-resilience-" + Guid.NewGuid().ToString("N") + ".gguf");
    private readonly List<LlamaRetrievalServer> _servers = new();
    private int _spawns;

    public RetrievalEngineEmbeddingResilienceTests()
    {
        Assert.True(OperatingSystem.IsWindows(), "The fake retrieval server uses Windows PowerShell.");
        File.WriteAllBytes(_modelPath, new byte[] { 1 });
    }

    public void Dispose()
    {
        foreach (LlamaRetrievalServer server in _servers)
            try { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { File.Delete(_modelPath); } catch { }
    }

    private LlamaRetrievalServer NewServer(int healthMs)
    {
        var server = new LlamaRetrievalServer(_modelPath, LlamaRetrievalMode.Embedding, () => false, new GamingHardOffRegistry(),
            _ => Task.FromResult(false))
        {
            LaunchOverrideForTests = port =>
            {
                Interlocked.Increment(ref _spawns);
                var info = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add(FakeServer.Replace("__PORT__", port.ToString()).Replace("__HEALTHMS__", healthMs.ToString()));
                return info;
            },
            RecoveryDelays = new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) }
        };
        _servers.Add(server);
        return server;
    }

    [Fact]
    public async Task SlowHealthAnswer_DoesNotKillOrRestartALiveServer()
    {
        // 900 ms is above the former 500 ms probe limit: the old code restarted the process on the second batch.
        LlamaRetrievalServer server = NewServer(healthMs: 900);
        var provider = new LlamaCppEmbeddingProvider(server, "test-embed", "r1", dimensions: 2);
        await provider.EmbedBatchAsync(new[] { "one" }, CancellationToken.None);
        await provider.EmbedBatchAsync(new[] { "two" }, CancellationToken.None);
        await provider.EmbedBatchAsync(new[] { "three" }, CancellationToken.None);
        Assert.Equal(1, _spawns);
        Assert.Equal(0, server.ActiveLeases);
    }

    [Fact]
    public async Task TransientConnectionFailures_AreRetriedThenSucceed()
    {
        LlamaRetrievalServer server = NewServer(healthMs: 0);
        int attempts = 0;
        string result = await server.RunResilientAsync(CancellationToken.None, (http, ct) =>
        {
            if (++attempts < 3) throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            return Task.FromResult("ok");
        });
        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task PersistentFailure_BecomesAFriendlyBilingualError_NotARawConnectionError()
    {
        LlamaRetrievalServer server = NewServer(healthMs: 0);
        int attempts = 0;
        var ex = await Assert.ThrowsAsync<RetrievalEngineUnavailableException>(() =>
            server.RunResilientAsync<string>(CancellationToken.None, (http, ct) =>
            {
                attempts++;
                throw new HttpRequestException("actively refused");
            }));
        Assert.Equal(4, attempts); // first try plus three bounded recoveries
        Assert.Contains("did not answer in time", ex.Message);
        Assert.Contains("non ha risposto in tempo", ex.Message);
        Assert.DoesNotContain("refused", ex.Message);
    }

    [Fact]
    public async Task CallerCancellation_IsNeverRetried()
    {
        LlamaRetrievalServer server = NewServer(healthMs: 0);
        using var cts = new CancellationTokenSource();
        int attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            server.RunResilientAsync<string>(cts.Token, (http, ct) =>
            {
                attempts++;
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("x");
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task LeaseIsHeldDuringTheRequest_AndReleasedAfter()
    {
        LlamaRetrievalServer server = NewServer(healthMs: 0);
        int during = -1;
        await server.RunAsync(CancellationToken.None, (http, ct) => { during = server.ActiveLeases; return Task.FromResult(0); });
        Assert.Equal(1, during);
        Assert.Equal(0, server.ActiveLeases);
    }

    [Fact]
    public async Task IndexingBatch_WhenEngineStaysUnreachable_KeepsGoingWithAFriendlyNotice()
    {
        var identity = new RetrievalEmbeddingIdentity("p", "m", "r", 2, "retrieval");
        var provider = new ResolvedRetrievalEmbeddingProvider(identity, 0f, IsAvailable: true, BaselineFallback: false,
            (texts, ct) => throw new RetrievalEngineUnavailableException(new HttpRequestException("refused")));
        var notices = new List<string>();
        IReadOnlyList<float[]?> vectors = await RagStore.EmbedResolvedBatchAsync(
            provider, new[] { "a", "b" }, CancellationToken.None, notices.Add);
        // No vector is recorded, so the file is not marked complete and the next run resumes it; no raw error escapes.
        Assert.All(vectors, v => Assert.Null(v));
        Assert.Single(notices);
        Assert.Contains("PaRuX riprenderà", notices[0]);
        Assert.Contains("continue from where it stopped", notices[0]);
    }
}
