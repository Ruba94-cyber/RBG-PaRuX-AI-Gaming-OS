package com.parux.android.core.pairing

import android.os.Build
import com.parux.android.core.network.PaRuXHttpClient
import com.parux.android.core.network.PairingApprovalPending
import com.parux.android.core.network.PairingCompleteRequest
import com.parux.android.core.network.PairingCompleteResponse
import com.parux.android.core.network.PairingPollRequest

sealed interface PairingResult {
    data object Success : PairingResult
    /** [unreachable] is true only when the PC could not be reached at all (not for a wrong/expired code). */
    data class Failure(val message: String, val unreachable: Boolean = false) : PairingResult
}

class PairingRepository(private val credentialStore: DeviceCredentialStore) {

    companion object {
        /** How long a tunnel pairing waits for the PC owner to approve on the PC. */
        internal const val APPROVAL_WAIT_MILLIS = 150_000L

        /**
         * Why an endpoint must not be used, or null when it may be.
         *
         * A named decision rather than an inline `if`: it is applied before the request and again to the endpoint the
         * server hands back before anything is persisted, and it is the one part of this class that can be tested
         * without an Android Context, since DeviceCredentialStore needs Keystore and SharedPreferences.
         */
        internal fun rejectionReasonFor(endpoint: String?): String? =
            if (LanEndpointPolicy.isAllowedPairingEndpoint(endpoint)) null
            else "That address is not one PaRuX will pair over. Pairing uses HTTPS on a private " +
                "LAN address, exactly as shown in PaRuX Windows → Settings → Remote access."

        /** True when the LAN route must be tried first and may fall through to the tunnel. */
        internal fun routeOrder(payload: PairingPayload): List<String> = buildList {
            if (payload.hasLan) add("lan")
            if (payload.hasTunnel) add("tunnel")
        }
    }

    /**
     * Pairs over the LAN when the QR carries a LAN address and it answers; otherwise (or when it does not) over the
     * Internet tunnel address if the QR carries one. A tunnel pairing is not instant: the PC owner has to approve the
     * new device on the PC, so [onAwaitingApproval] is called once and the poll then waits up to [APPROVAL_WAIT_MILLIS].
     */
    suspend fun completePairing(payload: PairingPayload, onAwaitingApproval: () -> Unit = {}): PairingResult {
        // Enforced here, not only in PairingPayload.parse, because the "no camera?" manual-entry form constructs a
        // PairingPayload directly from typed text and never goes through the parser. Every entry point passes here
        // before anything leaves the device.
        if (payload.hasTunnel && !TunnelEndpointPolicy.isAllowedTunnelEndpoint(payload.tunnel))
            return PairingResult.Failure("That Internet address is not one PaRuX will pair over (https with a host name only).")
        val lanAllowed = payload.hasLan && payload.fp.isNotBlank() && LanEndpointPolicy.isAllowedPairingEndpoint(payload.endpoint)
        if (payload.hasLan && !lanAllowed && !payload.hasTunnel)
            return PairingResult.Failure(rejectionReasonFor(payload.endpoint) ?: "Not a PaRuX pairing code.")
        if (!lanAllowed && !payload.hasTunnel) return PairingResult.Failure("Not a PaRuX pairing code.")

        val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim().ifBlank { "Android device" }
        if (lanAllowed) {
            when (val lan = pairOverLan(payload, deviceName)) {
                PairingResult.Success -> return PairingResult.Success
                is PairingResult.Failure ->
                    // Only "could not reach it" falls through to the tunnel; a wrong or expired code is final.
                    if (!payload.hasTunnel || !lan.unreachable) return lan
            }
        }
        return pairOverTunnel(payload, deviceName, onAwaitingApproval)
    }

    private suspend fun pairOverLan(payload: PairingPayload, deviceName: String): PairingResult {
        return try {
            val api = PaRuXHttpClient.buildForPairing(payload.endpoint, payload.fp)
            val response = api.completePairing(PairingCompleteRequest(code = payload.code, deviceName = deviceName))
            // The endpoint and fingerprint persisted come from whatever server answered, and this app then pins to them
            // for every future request, so the response has to satisfy the same rule as the QR.
            if (rejectionReasonFor(response.endpoint) != null) {
                return PairingResult.Failure(
                    "PaRuX PC answered with an address this app will not use " +
                        "(pairing is HTTPS on a private LAN address only). Nothing was saved."
                )
            }
            credentialStore.save(
                DeviceCredentialStore.Credential(
                    endpoint = response.endpoint,
                    deviceToken = response.deviceToken,
                    certificateFingerprint = response.certificateFingerprint,
                    deviceName = deviceName,
                    tunnelEndpoint = if (payload.hasTunnel) TunnelEndpointPolicy.normalize(payload.tunnel) else ""
                )
            )
            PairingResult.Success
        } catch (e: retrofit2.HttpException) {
            PairingResult.Failure(httpMessage(e.code()))
        } catch (e: javax.net.ssl.SSLHandshakeException) {
            PairingResult.Failure(
                "Couldn't verify PaRuX PC's certificate. Scan the QR code again from PaRuX Windows.", unreachable = true
            )
        } catch (e: java.io.IOException) {
            PairingResult.Failure(
                "Couldn't reach PaRuX PC. Check that both devices are on the same network.", unreachable = true
            )
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            PairingResult.Failure(e.message ?: "Pairing failed.")
        }
    }

    private suspend fun pairOverTunnel(payload: PairingPayload, deviceName: String, onAwaitingApproval: () -> Unit): PairingResult {
        val tunnel = TunnelEndpointPolicy.normalize(payload.tunnel)
        return try {
            val api = PaRuXHttpClient.buildForTunnelPairing(tunnel)
            val first = api.completePairingRaw(PairingCompleteRequest(code = payload.code, deviceName = deviceName))
            if (first.code() != 202) return PairingResult.Failure(httpMessage(first.code()))
            val pending = PaRuXHttpClient.json.decodeFromString(
                PairingApprovalPending.serializer(), first.body()?.string().orEmpty()
            )
            onAwaitingApproval()
            val deadline = System.currentTimeMillis() + APPROVAL_WAIT_MILLIS
            while (System.currentTimeMillis() < deadline) {
                kotlinx.coroutines.delay(maxOf(1, pending.pollIntervalSeconds) * 1000L)
                val poll = api.pollPairing(PairingPollRequest(pending.requestId, pending.pollSecret))
                when (poll.code()) {
                    200 -> return finishTunnelPairing(poll.body()?.string().orEmpty(), tunnel, deviceName)
                    202 -> Unit
                    403 -> return PairingResult.Failure("The PC owner declined this pairing.")
                    429 -> return PairingResult.Failure("Too many attempts. Wait a few minutes and create a new code on the PC.")
                    else -> return PairingResult.Failure("The pairing request expired. Create a new code on the PC and try again.")
                }
            }
            PairingResult.Failure("Nobody approved this pairing on the PC in time. Create a new code and approve it on the PC.")
        } catch (e: retrofit2.HttpException) {
            PairingResult.Failure(httpMessage(e.code()))
        } catch (e: javax.net.ssl.SSLException) {
            PairingResult.Failure("Couldn't verify the secure connection to your PC through the Internet.")
        } catch (e: java.io.IOException) {
            PairingResult.Failure(
                "Couldn't reach your PC through the Internet. Check that the tunnel is on in PaRuX Windows and this phone is online."
            )
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            PairingResult.Failure(e.message ?: "Pairing failed.")
        }
    }

    private fun finishTunnelPairing(body: String, tunnel: String, deviceName: String): PairingResult {
        val done = PaRuXHttpClient.json.decodeFromString(PairingCompleteResponse.serializer(), body)
        // Whatever the server returns is persisted and trusted from now on: the tunnel address must be the one we paired
        // over (no redirecting this device elsewhere), and a LAN address must pass the LAN rule.
        val lan = done.endpoint.takeIf { it.isNotBlank() && rejectionReasonFor(it) == null }
        if (done.endpoint.isNotBlank() && lan == null)
            return PairingResult.Failure("PaRuX PC answered with an address this app will not use. Nothing was saved.")
        if (done.tunnelEndpoint.isNotBlank() && TunnelEndpointPolicy.normalize(done.tunnelEndpoint) != tunnel)
            return PairingResult.Failure("PaRuX PC answered with a different Internet address. Nothing was saved.")
        if (lan != null && done.certificateFingerprint.isBlank())
            return PairingResult.Failure("PaRuX PC did not send its certificate fingerprint. Nothing was saved.")
        credentialStore.save(
            DeviceCredentialStore.Credential(
                endpoint = lan ?: "",
                deviceToken = done.deviceToken,
                certificateFingerprint = if (lan != null) done.certificateFingerprint else "",
                deviceName = deviceName,
                tunnelEndpoint = tunnel
            )
        )
        return PairingResult.Success
    }

    private fun httpMessage(code: Int): String = when (code) {
        401 -> "That pairing code is invalid or has expired. Create a new one from PaRuX Windows."
        403 -> "PaRuX PC is not reachable on this network the way pairing expects (HTTPS, private LAN only)."
        429 -> "Too many failed attempts. Wait a few minutes, then create a new code on the PC."
        503 -> "The Internet tunnel on the PC is off. Turn it on in PaRuX Windows > Settings > Remote access."
        else -> "Pairing failed (HTTP $code)."
    }

    fun forgetDevice() = credentialStore.clear()

    fun isPaired(): Boolean = credentialStore.isPaired()
}
