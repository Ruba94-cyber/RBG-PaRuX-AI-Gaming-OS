using System.Text.RegularExpressions;

namespace PaRuX.Server.Tunnel;

internal enum TunnelOutputSignal { None, Connected, NetworkFailure, TokenRejected }

/// <summary>Reads cloudflared's console output. Pure and stateless so every format it accepts is unit tested.</summary>
internal static class TunnelOutputParser
{
    // A quick tunnel is announced as https://<words-with-hyphens>.trycloudflare.com. "api" is Cloudflare's own endpoint
    // that cloudflared logs while requesting the tunnel, never a tunnel.
    private static readonly Regex QuickUrl = new(
        @"https://(?<label>[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)\.trycloudflare\.com(?![A-Za-z0-9.-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LongSecret = new(@"[A-Za-z0-9+/=_-]{40,}", RegexOptions.Compiled);

    internal static bool TryParseQuickTunnelUrl(string? line, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrEmpty(line) || line.Length > 4096) return false;
        foreach (Match match in QuickUrl.Matches(line))
        {
            string label = match.Groups["label"].Value;
            if (label.Equals("api", StringComparison.OrdinalIgnoreCase)) continue;
            url = "https://" + label.ToLowerInvariant() + ".trycloudflare.com";
            return true;
        }
        return false;
    }

    internal static TunnelOutputSignal Classify(string? line)
    {
        if (string.IsNullOrEmpty(line)) return TunnelOutputSignal.None;
        if (line.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase)) return TunnelOutputSignal.Connected;
        if (line.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || line.Contains("tunnel token", StringComparison.OrdinalIgnoreCase) && line.Contains("not valid", StringComparison.OrdinalIgnoreCase)
            || line.Contains("invalid tunnel secret", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Invalid tunnel token", StringComparison.OrdinalIgnoreCase))
            return TunnelOutputSignal.TokenRejected;
        if (line.Contains("failed to request quick Tunnel", StringComparison.OrdinalIgnoreCase)
            || line.Contains("no such host", StringComparison.OrdinalIgnoreCase)
            || line.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase)
            || line.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase)
            || line.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) && line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Unable to establish connection", StringComparison.OrdinalIgnoreCase))
            return TunnelOutputSignal.NetworkFailure;
        return TunnelOutputSignal.None;
    }

    /// <summary>Normalizes the public address a person typed for a named tunnel: https only, host only, no credentials.</summary>
    internal static bool TryNormalizePublicUrl(string? value, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.HostNameType != UriHostNameType.Dns
            || !uri.Host.Contains('.', StringComparison.Ordinal)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        url = "https://" + uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
        return true;
    }

    /// <summary>Removes anything that looks like a credential before a line is ever kept or shown.</summary>
    internal static string Redact(string? line) => string.IsNullOrEmpty(line) ? string.Empty : LongSecret.Replace(line, "***");
}
