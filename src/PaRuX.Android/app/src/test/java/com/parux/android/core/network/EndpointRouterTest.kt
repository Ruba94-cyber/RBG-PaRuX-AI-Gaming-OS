package com.parux.android.core.network

import com.parux.android.core.network.EndpointRouter.Route
import com.parux.android.core.pairing.PairingPayload
import com.parux.android.core.pairing.TunnelEndpointPolicy
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class EndpointRouterTest {
    private val lan = "https://192.168.1.50:51124"
    private val tunnel = "https://quiet-fox-9.trycloudflare.com"

    @Test
    fun `prefers the LAN when it answers`() {
        val router = EndpointRouter(lan, tunnel, probe = { it == lan })
        assertEquals(Route.LAN, router.refresh())
        assertEquals(lan, router.activeBase())
    }

    @Test
    fun `falls back to the tunnel when the LAN does not answer and shows which is in use`() {
        val router = EndpointRouter(lan, tunnel, probe = { false })
        assertEquals(Route.TUNNEL, router.refresh())
        assertEquals(tunnel, router.activeBase())
        assertEquals(Route.TUNNEL, router.route.value)
    }

    @Test
    fun `comes back to the LAN when it answers again`() {
        var lanUp = false
        var now = 0L
        val router = EndpointRouter(lan, tunnel, probe = { lanUp }, nowMillis = { now })
        router.refresh()
        assertEquals(Route.TUNNEL, router.route.value)
        lanUp = true
        now += 61_000
        router.maybeReprobeInBackground(runner = { it.run() })
        assertEquals(Route.LAN, router.route.value)
    }

    @Test
    fun `does not reprobe more than once per interval`() {
        var probes = 0
        var now = 0L
        val router = EndpointRouter(lan, tunnel, probe = { probes++; false }, nowMillis = { now })
        router.refresh()
        now += 10_000
        router.maybeReprobeInBackground(runner = { it.run() })
        assertEquals(1, probes)
    }

    @Test
    fun `a LAN connect failure switches to the tunnel without a probe`() {
        val router = EndpointRouter(lan, tunnel, probe = { true })
        assertTrue(router.reportLanUnreachable())
        assertEquals(tunnel, router.activeBase())
        assertFalse(router.reportLanUnreachable())
    }

    @Test
    fun `a tunnel-only or LAN-only pairing never switches`() {
        val tunnelOnly = EndpointRouter(null, tunnel, probe = { false })
        assertEquals(Route.TUNNEL, tunnelOnly.refresh())
        assertEquals(tunnel, tunnelOnly.primaryBase())
        assertFalse(tunnelOnly.reportLanUnreachable())
        val lanOnly = EndpointRouter(lan, null, probe = { false })
        assertEquals(Route.LAN, lanOnly.refresh())
        assertFalse(lanOnly.reportLanUnreachable())
    }

    @Test
    fun `owns only the two paired addresses`() {
        val router = EndpointRouter(lan, tunnel, probe = { true })
        assertTrue(router.owns("https", "192.168.1.50", 51124))
        assertTrue(router.owns("https", "quiet-fox-9.trycloudflare.com", 443))
        assertFalse(router.owns("https", "evil.example", 443))
        assertFalse(router.owns("http", "quiet-fox-9.trycloudflare.com", 80))
    }

    @Test
    fun `trust is pinned for the LAN host, public for the tunnel host, and refused for anything else`() {
        val tm = RoutingTrustManager("192.168.1.50", PinnedCertificateTrustManager("AA"), "quiet-fox-9.trycloudflare.com")
        assertEquals(RoutingTrustManager.Decision.PINNED, tm.decide("192.168.1.50"))
        assertEquals(RoutingTrustManager.Decision.SYSTEM, tm.decide("Quiet-Fox-9.trycloudflare.com"))
        assertEquals(RoutingTrustManager.Decision.REFUSE, tm.decide("attacker.example"))
        assertEquals(RoutingTrustManager.Decision.REFUSE, tm.decide(null))
    }

    @Test
    fun `tunnel endpoint policy accepts https host names only`() {
        assertTrue(TunnelEndpointPolicy.isAllowedTunnelEndpoint("https://quiet-fox-9.trycloudflare.com"))
        assertTrue(TunnelEndpointPolicy.isAllowedTunnelEndpoint("https://parux.example.com/"))
        assertTrue(TunnelEndpointPolicy.isQuickTunnel("https://quiet-fox-9.trycloudflare.com"))
        assertFalse(TunnelEndpointPolicy.isQuickTunnel("https://parux.example.com"))
        for (bad in listOf(
            "http://quiet-fox-9.trycloudflare.com", "https://192.168.1.5", "https://8.8.8.8", "https://[::1]",
            "https://user@x.trycloudflare.com", "https://a.trycloudflare.com/path", "https://a.trycloudflare.com?x=1",
            "https://localhost", "https://parux.local", "", "not a url"
        )) assertFalse(bad, TunnelEndpointPolicy.isAllowedTunnelEndpoint(bad))
    }

    @Test
    fun `a QR may carry the LAN route, the tunnel route or both`() {
        val both = PairingPayload.parse(
            """{"v":1,"endpoint":"https://192.168.1.50:51124","code":"1","fp":"AB","tunnel":"https://quiet-fox-9.trycloudflare.com"}"""
        )
        assertNotNull(both)
        assertTrue(both!!.hasLan && both.hasTunnel)
        val tunnelOnly = PairingPayload.parse("""{"v":1,"code":"1","tunnel":"https://quiet-fox-9.trycloudflare.com"}""")
        assertNotNull(tunnelOnly)
        assertFalse(tunnelOnly!!.hasLan)
    }

    @Test
    fun `a QR with a hostile tunnel or no route is not a pairing code`() {
        assertNull(PairingPayload.parse("""{"v":1,"code":"1","tunnel":"http://quiet-fox-9.trycloudflare.com"}"""))
        assertNull(PairingPayload.parse("""{"v":1,"code":"1","tunnel":"https://10.0.0.5"}"""))
        assertNull(PairingPayload.parse("""{"v":1,"code":"1"}"""))
        // A valid tunnel must not launder a bad LAN address.
        assertNull(
            PairingPayload.parse(
                """{"v":1,"endpoint":"http://192.168.1.50:51124","code":"1","fp":"AB","tunnel":"https://quiet-fox-9.trycloudflare.com"}"""
            )
        )
        // LAN without its fingerprint cannot be pinned.
        assertNull(PairingPayload.parse("""{"v":1,"endpoint":"https://192.168.1.50:51124","code":"1"}"""))
    }
}
