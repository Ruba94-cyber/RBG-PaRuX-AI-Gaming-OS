package com.parux.android.core.pairing

/**
 * The Internet (Cloudflare tunnel) address a pairing QR may carry. Like [LanEndpointPolicy] this is the
 * boundary where untrusted scanned text becomes something the app will connect to: `https` only, a DNS
 * name (never an IP literal, which would bypass the public certificate check), no userinfo, path, query
 * or fragment. PaRuX Windows advertises either a `*.trycloudflare.com` quick tunnel or the hostname the
 * person configured for their own named tunnel.
 */
object TunnelEndpointPolicy {

    fun isAllowedTunnelEndpoint(endpoint: String?): Boolean {
        val value = endpoint?.trim().orEmpty()
        if (value.isEmpty()) return false
        val uri = try { java.net.URI(value) } catch (_: Exception) { return false }
        if (!"https".equals(uri.scheme, ignoreCase = true)) return false
        if (uri.userInfo != null) return false
        if (!uri.query.isNullOrEmpty() || !uri.fragment.isNullOrEmpty()) return false
        if (!(uri.path.isNullOrEmpty() || uri.path == "/")) return false
        val host = uri.host ?: return false
        if (uri.port != -1 && uri.port !in 1..65535) return false
        if (!host.contains('.') || host.endsWith(".local", ignoreCase = true)) return false
        // An IP literal (v4 or v6) is not a tunnel address.
        if (host.all { it.isDigit() || it == '.' } || host.contains(':')) return false
        return host.all { it.isLetterOrDigit() || it == '-' || it == '.' }
    }

    /** Quick tunnels are the common case; anything else is the person's own named tunnel. */
    fun isQuickTunnel(endpoint: String?): Boolean =
        isAllowedTunnelEndpoint(endpoint) &&
            java.net.URI(endpoint!!.trim()).host.endsWith(".trycloudflare.com", ignoreCase = true)

    fun normalize(endpoint: String): String = endpoint.trim().trimEnd('/')
}
