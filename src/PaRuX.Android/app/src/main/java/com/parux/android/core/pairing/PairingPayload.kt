package com.parux.android.core.pairing

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * The QR payload PaRuX Windows renders in Settings > Remote access > Pair a device.
 *
 * `endpoint` + `fp` are the LAN address and its pinned certificate; `tunnel` is the public https address of the
 * optional Internet tunnel. Either route may be absent (a PC with the tunnel on and LAN off sends only `tunnel`).
 */
@Serializable
data class PairingPayload(
    val v: Int = 1,
    val endpoint: String = "",
    val code: String,
    val fp: String = "",
    val tunnel: String = ""
) {
    val hasLan: Boolean get() = endpoint.isNotBlank()
    val hasTunnel: Boolean get() = tunnel.isNotBlank()

    companion object {
        private val json = Json { ignoreUnknownKeys = true }

        /** Returns null for anything that is not a PaRuX pairing QR - callers show a generic
         * "not a PaRuX pairing code" message rather than a parser error.
         *
         * Every address is checked here, where untrusted scanned text becomes something the app will
         * connect to: the LAN address against [LanEndpointPolicy] (and it needs its fingerprint), the tunnel
         * address against [TunnelEndpointPolicy]. A payload with neither valid route is not a pairing code. */
        fun parse(scannedText: String): PairingPayload? = try {
            val payload = json.decodeFromString<PairingPayload>(scannedText)
            val lanOk = payload.hasLan && payload.fp.isNotBlank() && LanEndpointPolicy.isAllowedPairingEndpoint(payload.endpoint)
            val tunnelOk = payload.hasTunnel && TunnelEndpointPolicy.isAllowedTunnelEndpoint(payload.tunnel)
            val lanBad = payload.hasLan && !lanOk
            val tunnelBad = payload.hasTunnel && !tunnelOk
            if (payload.code.isBlank() || lanBad || tunnelBad || !(lanOk || tunnelOk)) null else payload
        } catch (_: Exception) {
            null
        }
    }
}
