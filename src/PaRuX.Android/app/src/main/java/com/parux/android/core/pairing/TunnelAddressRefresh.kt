package com.parux.android.core.pairing

/**
 * A quick tunnel gets a new public name every time the PC restarts it, but the device token stays valid. Whenever this phone
 * reaches the PC (LAN or tunnel) it asks for the current address and stores it, so nobody has to pair again.
 */
object TunnelAddressRefresh {

    /** The address to store, or null to keep what is stored (nothing reported, not a valid tunnel address, or unchanged). */
    fun newAddressToStore(stored: String, reported: String?): String? {
        val candidate = reported?.trim().orEmpty()
        if (candidate.isEmpty() || !TunnelEndpointPolicy.isAllowedTunnelEndpoint(candidate)) return null
        val normalized = TunnelEndpointPolicy.normalize(candidate)
        return if (normalized.equals(TunnelEndpointPolicy.normalize(stored), ignoreCase = true)) null else normalized
    }
}
