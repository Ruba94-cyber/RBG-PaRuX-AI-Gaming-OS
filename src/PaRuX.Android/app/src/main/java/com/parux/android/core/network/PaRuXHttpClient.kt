package com.parux.android.core.network

import com.parux.android.core.pairing.DeviceCredentialStore
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory

/**
 * Builds the one authenticated, certificate-pinned HTTP client PaRuX Android uses to talk to
 * PaRuX Server. There is deliberately no other network path in this app: no direct connection
 * to a model server, media runtime, or filesystem share (PARUX_ANDROID_API.md).
 */
object PaRuXHttpClient {

    val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    /** Null when the app is not paired yet - callers route to the pairing screen instead.
     *
     * A credential may carry a LAN address (pinned certificate), an Internet tunnel address (public certificate), or
     * both. With both, the app prefers the LAN when it answers and falls back to the tunnel; [AuthenticatedClient.router]
     * says which one is in use. Callers keep building URLs from [AuthenticatedClient.endpoint]; the routing interceptor
     * rewrites them to the active address, so no call site changes. */
    fun buildAuthenticated(credentialStore: DeviceCredentialStore): AuthenticatedClient? {
        val credential = credentialStore.load() ?: return null
        val lanEndpoint = credential.endpoint.takeIf { it.isNotBlank() }
        val tunnelEndpoint = credential.tunnelEndpoint.takeIf { it.isNotBlank() }
        val pinned = lanEndpoint?.let { PinnedCertificateTrustManager(credential.certificateFingerprint) }
        val trustManager = RoutingTrustManager(
            lanHost = lanEndpoint?.let { java.net.URI(it).host },
            pinned = pinned,
            tunnelHost = tunnelEndpoint?.let { java.net.URI(it).host }
        )

        val probeClient = if (lanEndpoint != null && pinned != null) OkHttpClient.Builder()
            .sslSocketFactory(pinned.toSocketFactory(), pinned)
            .followRedirects(false)
            .followSslRedirects(false)
            .connectTimeout(2500, TimeUnit.MILLISECONDS)
            .readTimeout(3, TimeUnit.SECONDS)
            .build() else null
        val router = EndpointRouter(lanEndpoint, tunnelEndpoint, probe = { base ->
            try {
                probeClient!!.newCall(
                    Request.Builder().url(base.trimEnd('/') + "/api/v1/health")
                        .header("X-PaRuX-Device-Token", credential.deviceToken).build()
                ).execute().use { it.code in 200..499 }
            } catch (_: Exception) { false }
        })

        val okHttp = createRoutedOkHttpClient(credential.deviceToken, trustManager, router)

        val contentType = "application/json".toMediaType()
        val retrofit = Retrofit.Builder()
            .baseUrl(router.primaryBase() + "/")
            .client(okHttp)
            .addConverterFactory(json.asConverterFactory(contentType))
            .build()

        return AuthenticatedClient(
            okHttp = okHttp,
            api = retrofit.create(PaRuXApi::class.java),
            endpoint = router.primaryBase(),
            deviceToken = credential.deviceToken,
            router = router
        )
    }

    internal fun createRoutedOkHttpClient(
        deviceToken: String,
        trustManager: RoutingTrustManager,
        router: EndpointRouter
    ): OkHttpClient {
        val context = javax.net.ssl.SSLContext.getInstance("TLS")
        context.init(null, arrayOf(trustManager), java.security.SecureRandom())
        return OkHttpClient.Builder()
            .sslSocketFactory(context.socketFactory, trustManager)
            .followRedirects(false)
            .followSslRedirects(false)
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.SECONDS)
            .writeTimeout(60, TimeUnit.SECONDS)
            .addInterceptor { chain ->
                val original = chain.request()
                MobileEndpointBoundary.desktopOnlyReason(original.method, original.url.encodedPath)?.let { reason ->
                    throw IOException(reason)
                }
                router.maybeReprobeInBackground()
                fun routed(request: Request): Request {
                    val u = request.url
                    if (!router.owns(u.scheme, u.host, u.port)) return request
                    val base = router.activeBase().toHttpUrl()
                    return request.newBuilder()
                        .url(u.newBuilder().scheme(base.scheme).host(base.host).port(base.port).build())
                        .addHeader("X-PaRuX-Device-Token", deviceToken)
                        .build()
                }
                try {
                    chain.proceed(routed(original))
                } catch (e: java.net.ConnectException) {
                    if (router.reportLanUnreachable()) chain.proceed(routed(original)) else throw e
                } catch (e: java.net.UnknownHostException) {
                    if (router.reportLanUnreachable()) chain.proceed(routed(original)) else throw e
                } catch (e: java.net.SocketTimeoutException) {
                    // Only a connect-phase timeout proves the request never reached the PC.
                    if (e.message?.contains("connect", ignoreCase = true) == true && router.reportLanUnreachable())
                        chain.proceed(routed(original)) else throw e
                } catch (e: javax.net.ssl.SSLHandshakeException) {
                    // A different network that reuses the LAN address presents another certificate: the pin refuses it.
                    if (router.reportLanUnreachable()) chain.proceed(routed(original)) else throw e
                }
            }
            .build()
    }

    internal fun createAuthenticatedOkHttpClient(
        deviceToken: String,
        trustManager: PinnedCertificateTrustManager
    ): OkHttpClient = OkHttpClient.Builder()
        .sslSocketFactory(trustManager.toSocketFactory(), trustManager)
        .followRedirects(false)
        .followSslRedirects(false)
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.SECONDS)   // streaming chat/video responses can run for minutes
        .writeTimeout(60, TimeUnit.SECONDS)
        .addInterceptor { chain ->
            val original = chain.request()
            MobileEndpointBoundary.desktopOnlyReason(original.method, original.url.encodedPath)?.let { reason ->
                throw IOException(reason)
            }
            val request: Request = original.newBuilder()
                .addHeader("X-PaRuX-Device-Token", deviceToken)
                .build()
            chain.proceed(request)
        }
        .build()

    /** Unauthenticated client used only for the one pre-auth call: pairing completion. It still
     * pins the certificate fingerprint shown in the QR - the pairing code is single-use and
     * short-lived, but the TLS connection carrying it is not exempt from pinning. */
    fun buildForPairing(endpoint: String, certificateFingerprintHex: String): PaRuXApi {
        val trustManager = PinnedCertificateTrustManager(certificateFingerprintHex)
        val okHttp = OkHttpClient.Builder()
            .sslSocketFactory(trustManager.toSocketFactory(), trustManager)
            .followRedirects(false)
            .followSslRedirects(false)
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS)
            .build()
        val contentType = "application/json".toMediaType()
        val retrofit = Retrofit.Builder()
            .baseUrl(endpoint.trimEnd('/') + "/")
            .client(okHttp)
            .addConverterFactory(json.asConverterFactory(contentType))
            .build()
        return retrofit.create(PaRuXApi::class.java)
    }

    /** Unauthenticated client for pairing through the Internet tunnel: the tunnel's public certificate is verified
     * normally (system CAs and host name); there is no fingerprint to pin because Cloudflare owns the certificate. */
    fun buildForTunnelPairing(endpoint: String): PaRuXApi {
        val okHttp = OkHttpClient.Builder()
            .followRedirects(false)
            .followSslRedirects(false)
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(20, TimeUnit.SECONDS)
            .build()
        val retrofit = Retrofit.Builder()
            .baseUrl(endpoint.trimEnd('/') + "/")
            .client(okHttp)
            .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
            .build()
        return retrofit.create(PaRuXApi::class.java)
    }

    data class AuthenticatedClient(
        val okHttp: OkHttpClient,
        val api: PaRuXApi,
        val endpoint: String,
        val deviceToken: String,
        val router: EndpointRouter? = null
    )
}
