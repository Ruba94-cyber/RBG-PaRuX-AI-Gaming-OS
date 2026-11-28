package com.parux.android.core

import android.content.Context
import com.parux.android.core.network.PaRuXHttpClient
import com.parux.android.core.pairing.DeviceCredentialStore
import com.parux.android.core.pairing.PairingRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * A small hand-rolled service locator instead of a DI framework: this app has a handful of
 * singletons (credential store, one HTTP client, shared active Project scope), which does not
 * carry its weight in build time and boilerplate.
 */
class AppContainer(context: Context) {
    /** Process-safe application context used by durable local-only supervisors (WorkManager). */
    val appContext: Context = context.applicationContext

    val credentialStore = DeviceCredentialStore(appContext)
    val pairingRepository = PairingRepository(credentialStore)
    val activeProjectStore = ActiveProjectStore(appContext)

    private val ioScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _client = MutableStateFlow(PaRuXHttpClient.buildAuthenticated(credentialStore))

    init { probeRoute() }

    /** Prefer the LAN when the PC answers there, else the Internet tunnel (see EndpointRouter). */
    private fun probeRoute() {
        val client = _client.value ?: return
        val router = client.router ?: return
        ioScope.launch {
            if (router.hasBoth) router.refresh()
            refreshTunnelAddress(client)
        }
    }

    /** Reached the PC by any route: pick up its current tunnel address if it changed (token stays valid). */
    suspend fun refreshTunnelAddress(client: PaRuXHttpClient.AuthenticatedClient? = _client.value) {
        client ?: return
        try {
            val reported = client.api.remoteAddress().tunnelEndpoint
            val stored = credentialStore.load() ?: return
            val updated = com.parux.android.core.pairing.TunnelAddressRefresh.newAddressToStore(stored.tunnelEndpoint, reported) ?: run {
                if (reported.isNotBlank()) client.router?.clearTunnelStale()
                return
            }
            credentialStore.save(stored.copy(tunnelEndpoint = updated))
            _client.value = PaRuXHttpClient.buildAuthenticated(credentialStore)
            _client.value?.router?.let { if (it.hasBoth) it.refresh() }
        } catch (_: Exception) {
            // Not reachable right now, or an older PC without the route: keep the stored address.
        }
    }

    /** Null when not paired. Screens observe this instead of the credential store directly,
     * so a re-pair or forget-device is reflected everywhere at once. */
    val client: StateFlow<PaRuXHttpClient.AuthenticatedClient?> = _client.asStateFlow()

    fun refreshClient() {
        _client.value = PaRuXHttpClient.buildAuthenticated(credentialStore)
        probeRoute()
    }

    fun forgetDevice() {
        pairingRepository.forgetDevice()
        refreshClient()
    }
}
