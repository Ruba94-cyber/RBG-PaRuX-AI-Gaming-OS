using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Corporate Lite reaches the network only through the default proxy, which CorporateNetworkGuard points at a dead
/// loopback port. A client built with UseProxy = false skips that guard, so each one needs a reason it cannot leave
/// the machine in Corporate. This gate makes adding one a deliberate, reviewed act.
/// </summary>
public sealed class CorporateEgressSourceContractTests
{
    /// <summary>Every file that sets UseProxy = false today, with why that cannot egress in Corporate.</summary>
    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/PaRuX.Contracts/PaRuXControlPlaneHttpClient.cs"] = "loopback control-plane endpoint only (TryParseLocalEndpoint requires IsLoopback)",
        ["src/PaRuX.Server/CandidateTextQualityRunner.cs"] = "talks to the managed local llama-server",
        ["src/PaRuX.Server/ConnectedPeerAttestation.cs"] = "ResolveLoopbackTargets refuses any non-loopback host",
        ["src/PaRuX.Server/LocalSearxngSearchProvider.cs"] = "endpoint must be http://127.0.0.1:<port>/",
        ["src/PaRuX.Server/Orchestration/Gaming/Downloads/ExternalTorrentClientTransport.cs"] = "BaseAddress is 127.0.0.1; /api/v1/gaming is refused in Corporate",
        ["src/PaRuX.Server/Orchestration/OpenCodeLocalProbe.cs"] = "explicit IPv4 loopback root URLs only",
        ["src/PaRuX.Server/Orchestration/OpenCodeSessionObserver.cs"] = "explicit IPv4 loopback root URLs only",
        ["src/PaRuX.Server/ValidatedRemoteConnector.cs"] = "connect-time rule refuses non-loopback addresses when CorporateMode is active",
        ["src/PaRuX.Server/WebSearchProviders.cs"] = "only used by WebSearchService, which throws while the Internet switch is off (always off in Corporate)",
    };

    private static readonly Regex UseProxyOff = new(@"UseProxy\s*=\s*false", RegexOptions.Compiled);

    [Fact]
    public void No_new_UseProxy_false_client_appears_outside_the_reviewed_allowlist()
    {
        string root = RepoRoot();
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
            if (UseProxyOff.IsMatch(File.ReadAllText(file)) && !Allowlist.ContainsKey(relative))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "UseProxy = false bypasses the Corporate dead-proxy guard. Route the client through the default proxy, or prove it is "
            + "loopback-only or blocked in Corporate and add it to the allowlist: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData(true, "127.0.0.1", true)]
    [InlineData(true, "::1", true)]
    [InlineData(true, "8.8.8.8", false)]
    [InlineData(true, "192.168.1.20", false)]
    [InlineData(false, "8.8.8.8", true)]
    [InlineData(false, "192.168.1.20", true)]
    public void Remote_connector_in_Corporate_dials_loopback_only(bool corporate, string address, bool expected) =>
        Assert.Equal(expected, ValidatedRemoteConnector.CorporateAllows(corporate, IPAddress.Parse(address)));

    [Fact]
    public async Task Corporate_refusal_surfaces_as_an_unsafe_address_error_before_any_socket_is_opened()
    {
        bool callerRuleCalled = false;
        Func<string, IPAddress, bool> callerRule = (_, _) => { callerRuleCalled = true; return true; };
        Func<string, IPAddress, bool> corporateRule = (host, address) =>
            ValidatedRemoteConnector.CorporateAllows(true, address) && callerRule(host, address);

        await Assert.ThrowsAsync<HttpRequestException>(() => ValidatedRemoteConnector.ResolveAllowedAsync(
            "example.test", (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), corporateRule, CancellationToken.None));
        Assert.False(callerRuleCalled);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
