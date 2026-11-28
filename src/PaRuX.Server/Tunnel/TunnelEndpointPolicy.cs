namespace PaRuX.Server.Tunnel;

/// <summary>
/// What a request that arrived through the Internet tunnel may reach. Strictly the paired-device capability boundary
/// (<see cref="PairedDeviceEndpointPolicy"/>) plus the two unauthenticated pairing steps. Everything else (desktop owner,
/// browser-session, API-key, Ollama/OpenAI provider and gateway routes, the served web client) is refused here, before
/// authentication, so a route added later is unreachable from the Internet until someone lists it.
/// </summary>
internal static class TunnelEndpointPolicy
{
    internal const string PairCompletePath = "/api/v1/remote/pair/complete";
    internal const string PairPollPath = "/api/v1/remote/pair/poll";

    internal static bool IsPairingStep(string method, PathString path)
    {
        string p = Normalize(path.Value);
        return HttpMethods.IsPost(method)
            && (p.Equals(PairCompletePath, StringComparison.OrdinalIgnoreCase) || p.Equals(PairPollPath, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsAllowed(string method, PathString path) =>
        IsPairingStep(method, path)
        || PairedDeviceEndpointPolicy.IsAllowed(method, path)
        || PairedDeviceEndpointPolicy.IsLegacyComputerUseApprovalPath(method, path);

    private static string Normalize(string? value)
    {
        string path = string.IsNullOrWhiteSpace(value) ? "/" : value!;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}
