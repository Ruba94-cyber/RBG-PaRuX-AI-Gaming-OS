package com.parux.android.core.network

import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Chooses which of a paired PC's two addresses this app uses right now: the LAN address (preferred, pinned
 * certificate, nothing leaves the home network) or the Internet tunnel address (public certificate, used only
 * when the LAN is not reachable). The choice is observable so Settings can show which one is in use.
 *
 * [probe] answers "is this base URL reachable and really my PC?" and is injected so the policy is unit tested
 * without a network.
 */
class EndpointRouter(
    lanEndpoint: String?,
    tunnelEndpoint: String?,
    private val probe: (String) -> Boolean,
    private val nowMillis: () -> Long = System::currentTimeMillis
) {
    enum class Route { LAN, TUNNEL }

    val lan: String? = lanEndpoint?.trim()?.trimEnd('/')?.takeIf { it.isNotEmpty() }
    val tunnel: String? = tunnelEndpoint?.trim()?.trimEnd('/')?.takeIf { it.isNotEmpty() }

    private val _route = MutableStateFlow(if (lan != null) Route.LAN else Route.TUNNEL)
    val route: StateFlow<Route> = _route.asStateFlow()

    private val lastProbe = AtomicLong(0L)
    private val probing = java.util.concurrent.atomic.AtomicBoolean(false)

    /** Base URL requests currently go to. */
    fun activeBase(): String = if (_route.value == Route.LAN) lan ?: tunnel!! else tunnel ?: lan!!

    /** The address stored first at pairing; callers that built URLs from it are rewritten to [activeBase]. */
    fun primaryBase(): String = lan ?: tunnel!!

    val hasBoth: Boolean get() = lan != null && tunnel != null

    fun owns(scheme: String, host: String, port: Int): Boolean =
        listOfNotNull(lan, tunnel).any { base ->
            val u = java.net.URI(base)
            u.scheme.equals(scheme, true) && u.host.equals(host, true) && effectivePort(u) == port
        }

    /** Prefer LAN when it answers, else the tunnel. Blocking; run off the main thread. */
    fun refresh(): Route {
        lastProbe.set(nowMillis())
        val chosen = when {
            lan == null -> Route.TUNNEL
            tunnel == null -> Route.LAN
            probe(lan) -> Route.LAN
            else -> Route.TUNNEL
        }
        _route.value = chosen
        return chosen
    }

    /** A request to the LAN address failed to connect: use the tunnel from now on if there is one. */
    fun reportLanUnreachable(): Boolean {
        if (tunnel == null || lan == null || _route.value == Route.TUNNEL) return false
        _route.value = Route.TUNNEL
        lastProbe.set(nowMillis())
        return true
    }

    /** While on the tunnel, look for the LAN again at most once per [interval] without blocking the caller. */
    fun maybeReprobeInBackground(interval: Long = 60_000L, runner: (Runnable) -> Unit = { Thread(it, "parux-route-probe").apply { isDaemon = true }.start() }) {
        if (!hasBoth || _route.value != Route.TUNNEL) return
        if (nowMillis() - lastProbe.get() < interval) return
        if (!probing.compareAndSet(false, true)) return
        runner(Runnable { try { refresh() } finally { probing.set(false) } })
    }

    private fun effectivePort(u: java.net.URI): Int = if (u.port != -1) u.port else if (u.scheme.equals("https", true)) 443 else 80
}
