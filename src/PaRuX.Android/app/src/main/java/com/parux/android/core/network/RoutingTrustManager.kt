package com.parux.android.core.network

import java.net.Socket
import java.security.KeyStore
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509ExtendedTrustManager
import javax.net.ssl.X509TrustManager

/**
 * One TLS trust decision per destination, so a single client can use both of a PC's addresses:
 * the LAN host is trusted ONLY by the certificate fingerprint pinned at pairing (no CA consulted),
 * every other host (the Internet tunnel) must present a normal publicly trusted certificate.
 * A host that is neither the LAN host nor the tunnel host is refused outright.
 */
class RoutingTrustManager(
    private val lanHost: String?,
    private val pinned: PinnedCertificateTrustManager?,
    private val tunnelHost: String?,
    private val system: X509TrustManager = defaultSystemTrustManager()
) : X509ExtendedTrustManager() {

    enum class Decision { PINNED, SYSTEM, REFUSE }

    fun decide(host: String?): Decision = when {
        host == null -> Decision.REFUSE
        lanHost != null && pinned != null && host.equals(lanHost, ignoreCase = true) -> Decision.PINNED
        tunnelHost != null && host.equals(tunnelHost, ignoreCase = true) -> Decision.SYSTEM
        else -> Decision.REFUSE
    }

    private fun check(chain: Array<out X509Certificate>?, authType: String?, host: String?) {
        when (decide(host)) {
            Decision.PINNED -> pinned!!.checkServerTrusted(chain, authType)
            Decision.SYSTEM -> system.checkServerTrusted(chain, authType)
            Decision.REFUSE -> throw CertificateException("PaRuX will not connect to this address.")
        }
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) =
        check(chain, authType, (socket as? SSLSocket)?.handshakeSession?.peerHost ?: socket?.inetAddress?.hostName)

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) =
        check(chain, authType, engine?.peerHost)

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        // No destination known: cannot choose between pin and public CA, so fail closed.
        throw CertificateException("PaRuX needs the destination host to verify a certificate.")
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) =
        throw CertificateException("Client certificates are not used by PaRuX.")

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) =
        throw CertificateException("Client certificates are not used by PaRuX.")

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("Client certificates are not used by PaRuX.")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    companion object {
        fun defaultSystemTrustManager(): X509TrustManager {
            val factory = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
            factory.init(null as KeyStore?)
            return factory.trustManagers.filterIsInstance<X509TrustManager>().first()
        }
    }
}
