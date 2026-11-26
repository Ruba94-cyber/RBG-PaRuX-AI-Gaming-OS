package com.parux.android.ui.chat

import androidx.annotation.StringRes
import com.parux.android.R
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * A non-2xx answer from the PaRuX Core for a chat turn. [detail] is the Core's own explanation,
 * already sanitized by [ChatErrors.serverDetail] (no absolute local paths, no secrets, bounded),
 * so it can be shown on the phone; [reason] picks the actionable guidance shown above it.
 */
data class ChatServerFailure(val httpStatus: Int, val detail: String?, val reason: ChatErrors.Reason)

/**
 * User-facing text for chat failures. Transport exceptions carry messages such as
 * "Failed to connect to /192.168.1.20:51124" that tell a phone user nothing about what to do;
 * the pairing screen already speaks in the user's terms, and chat now does the same.
 */
object ChatErrors {
    /** HTTP 401 from the PC means the paired-device token expired or was revoked; only pairing again fixes it. */
    const val PAIR_AGAIN =
        "This device is no longer paired with your PaRuX PC. Pair this device again from PaRuX Windows → Settings → Local Network."

    /** Longest Core explanation shown on the phone; the full text stays in the PC's own logs. */
    const val MAX_DETAIL_LENGTH = 280

    /**
     * Known Core failure families, mirroring the Windows client's RuntimeRecoveryGuidance so the
     * same Core error reads the same way on both clients.
     */
    enum class Reason { Unknown, GamingActive, AiOff, MissingModel, MissingRuntime, OutOfMemory, TimedOut }

    fun forHttpStatus(code: Int, fallback: String): String = if (code == 401) PAIR_AGAIN else fallback

    fun describe(e: Exception): String = when (e) {
        is retrofit2.HttpException ->
            if (e.code() == 401) PAIR_AGAIN
            else {
                val detail = try { serverDetail(e.response()?.errorBody()?.string()) } catch (_: Exception) { null }
                "PaRuX PC returned an error (HTTP ${e.code()})" + (detail?.let { ": $it" } ?: ".")
            }
        is javax.net.ssl.SSLException ->
            "Couldn't verify PaRuX PC's certificate. Pair again from PaRuX Windows → Settings → Local Network."
        is java.net.SocketTimeoutException ->
            "PaRuX PC took too long to answer. It may be loading a model; try again in a moment."
        is java.net.ConnectException, is java.net.NoRouteToHostException, is java.net.UnknownHostException ->
            "Can't reach your PaRuX PC. Make sure PaRuX is running on it and both devices are on the same network."
        is java.io.IOException -> e.message ?: "PaRuX PC is not reachable right now."
        else -> e.message ?: "Something went wrong."
    }

    /** Builds the typed failure for a rejected chat request from its status and (possibly empty) body. */
    fun serverFailure(httpStatus: Int, body: String?): ChatServerFailure {
        val detail = serverDetail(body)
        return ChatServerFailure(httpStatus, detail, classify(detail))
    }

    /**
     * Extracts the Core's explanation from an error body. The Core answers with several shapes:
     * `{"error":{"message":..,"remediation":..}}`, `{"error":"text","retry_hint":..}`,
     * `{"success":false,"error":"code","message":"text"}` and ProblemDetails `{"title","detail"}`.
     * A plain-text body is accepted too. The result is sanitized and bounded, or null when empty.
     */
    fun serverDetail(body: String?): String? {
        val raw = body?.trim().orEmpty()
        if (raw.isEmpty()) return null
        val root = try { Json.parseToJsonElement(raw) as? JsonObject } catch (_: Exception) { null }
        val text = if (root == null) {
            // HTML error pages from a proxy say nothing useful and would leak markup into the bubble;
            // JSON that is not an object (arrays, numbers) is not an explanation either.
            if (raw.startsWith("<") || raw.startsWith("[") || raw.startsWith("{")) null else raw
        } else {
            val error = root["error"]
            val errorObject = error as? JsonObject
            val errorText = (error as? JsonPrimitive)?.takeIf { it.isString }?.content?.takeIf { it.isNotBlank() }
            val message = errorObject?.string("message")
                ?: root.string("message")
                ?: errorText?.takeUnless { looksLikeCode(it) }
                ?: root.string("detail")
                ?: root.string("title")
                ?: errorText
            val remediation = errorObject?.string("remediation")
            listOfNotNull(message, remediation).joinToString(" ").takeIf { it.isNotBlank() }
        }
        return text?.let(::sanitize)?.takeIf { it.isNotBlank() }
    }

    private fun JsonObject.string(name: String): String? =
        (this[name] as? JsonPrimitive)?.takeIf { it.isString }?.content?.takeIf { it.isNotBlank() }

    private val codeLike = Regex("^[a-z0-9_.-]+$")
    private fun looksLikeCode(value: String) = codeLike.matches(value)

    // Drive-letter or UNC absolute path; group 1 keeps only the final file name. Directory segments may contain
    // spaces (user folders often do) but never a colon, so two paths in one message stay two matches.
    private val windowsPath = Regex("""(?i)(?:\b[a-z]:|\\\\[^\\\s"']+)\\(?:[^\\"'<>|:*?\r\n]+\\)*([^\\\s"'<>|:*?,;)]*)""")
    private val windowsForwardPath = Regex("""(?i)\b[a-z]:/(?:[^/"'<>|:*?\r\n]+/)*([^/\s"'<>|:*?,;)]*)""")
    private val unixPath = Regex("""(?<![\w.:/])/(?:home|Users|root|var|tmp|opt|usr|etc|mnt|data|storage|sdcard)(?:/[^/\s"',;)]+)*/([^/\s"',;)]+)""")
    private val bearer = Regex("""(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}""")
    private val keyValueSecret = Regex("""(?i)\b(api[_-]?key|access[_-]?token|token|secret|password|passwd|pwd|authorization)(\s*[=:]\s*)("[^"]*"|'[^']*'|\S+)""")
    private val longOpaque = Regex("""\b[A-Za-z0-9_\-+/]{40,}={0,2}""")
    private val controlChars = Regex("""[\u0000-\u001F\u007F]""")
    private val whitespace = Regex("""\s+""")

    /** Removes absolute local paths (keeps only the file name), credentials and control characters, then bounds the length. */
    fun sanitize(text: String): String {
        var s = controlChars.replace(text, " ")
        s = windowsPath.replace(s) { m -> m.groupValues[1].ifEmpty { "<path>" } }
        s = windowsForwardPath.replace(s) { m -> m.groupValues[1].ifEmpty { "<path>" } }
        s = unixPath.replace(s) { m -> m.groupValues[1].ifEmpty { "<path>" } }
        s = bearer.replace(s) { m -> m.groupValues[1] + " [redacted]" }
        s = keyValueSecret.replace(s) { m -> m.groupValues[1] + m.groupValues[2] + "[redacted]" }
        s = longOpaque.replace(s, "[redacted]")
        s = whitespace.replace(s, " ").trim()
        return if (s.length <= MAX_DETAIL_LENGTH) s else s.take(MAX_DETAIL_LENGTH - 1).trimEnd() + "…"
    }

    private fun has(source: String, value: String) = source.contains(value, ignoreCase = true)

    /** Same ordering rules as the Windows RuntimeRecoveryGuidance.Classify for the families shown on the phone. */
    fun classify(detail: String?): Reason {
        val raw = detail?.trim().orEmpty()
        if (raw.isEmpty()) return Reason.Unknown
        return when {
            has(raw, "game is currently active") || has(raw, "gaming mode is active") ||
                has(raw, "game is running") || has(raw, "Gaming Mode became active") -> Reason.GamingActive
            has(raw, "OFF mode") || has(raw, "local AI is switched off") -> Reason.AiOff
            (has(raw, "not found") && has(raw, ".gguf")) || has(raw, "model file not found") ||
                has(raw, "model does not exist") -> Reason.MissingModel
            has(raw, "runtime not installed") || has(raw, "runtime is not installed") || has(raw, "runtime is missing") ||
                has(raw, "runtime missing") || has(raw, "bundled CPU runtime") ||
                ((has(raw, "not found") || has(raw, "missing") || has(raw, "not installed")) && has(raw, "llama-server")) -> Reason.MissingRuntime
            has(raw, "out of memory") || has(raw, "CUDA_ERROR_OUT_OF_MEMORY") || has(raw, "failed to allocate memory") ||
                has(raw, "not enough VRAM") || has(raw, "insufficient VRAM") || has(raw, "not enough system memory") ||
                has(raw, "not enough free memory") -> Reason.OutOfMemory
            has(raw, "timed out") || has(raw, "did not become healthy") || has(raw, "did not become ready") -> Reason.TimedOut
            else -> Reason.Unknown
        }
    }

    @StringRes
    fun guidanceRes(reason: Reason): Int? = when (reason) {
        Reason.GamingActive -> R.string.chat_error_gaming_active
        Reason.AiOff -> R.string.chat_error_ai_off
        Reason.MissingModel -> R.string.chat_error_missing_model
        Reason.MissingRuntime -> R.string.chat_error_missing_runtime
        Reason.OutOfMemory -> R.string.chat_error_out_of_memory
        Reason.TimedOut -> R.string.chat_error_timed_out
        Reason.Unknown -> null
    }

    /**
     * Localized text for a rejected chat request: actionable guidance for a known family plus the
     * Core's sanitized cause, the cause alone when the family is unknown, and an explicit
     * "no reason given" text rather than a bare status code when the Core sent no explanation.
     * [text] resolves a string resource with format arguments (Context.getString in the app).
     */
    fun render(failure: ChatServerFailure, text: (Int, Array<out Any>) -> String): String {
        if (failure.httpStatus == 401) return PAIR_AGAIN
        val detail = failure.detail
        val guidance = guidanceRes(failure.reason)
        return when {
            guidance != null && detail != null ->
                text(guidance, emptyArray()) + "\n" + text(R.string.chat_error_details, arrayOf(failure.httpStatus, detail))
            guidance != null -> text(guidance, emptyArray())
            detail != null -> text(R.string.chat_error_server_detail, arrayOf(failure.httpStatus, detail))
            else -> text(R.string.chat_error_server_no_detail, arrayOf(failure.httpStatus))
        }
    }
}
