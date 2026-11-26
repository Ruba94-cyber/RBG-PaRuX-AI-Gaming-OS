package com.parux.android.ui.chat

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ChatErrorsTest {
    @Test
    fun anExpiredPairedTokenTellsTheUserToPairAgain() {
        val response = retrofit2.Response.error<Any>(
            401, okhttp3.ResponseBody.create(null, ""))
        val text = ChatErrors.describe(retrofit2.HttpException(response))
        assertTrue(text.contains("Pair this device again"))
        assertEquals(ChatErrors.PAIR_AGAIN, ChatErrors.forHttpStatus(401, "x"))
        assertEquals("x", ChatErrors.forHttpStatus(500, "x"))
    }

    @Test
    fun unreachablePcIsExplainedInTheUsersTermsWithoutTheRawAddress() {
        val text = ChatErrors.describe(java.net.ConnectException("Failed to connect to /192.168.1.20:51124"))
        assertTrue(text.startsWith("Can't reach your PaRuX PC"))
        assertFalse(text.contains("192.168.1"))
    }

    @Test
    fun timeoutsAndCertificateFailuresHaveTheirOwnGuidance() {
        assertTrue(ChatErrors.describe(java.net.SocketTimeoutException("timeout")).contains("took too long"))
        assertTrue(ChatErrors.describe(javax.net.ssl.SSLHandshakeException("bad cert")).contains("Pair again"))
    }

    // ---- Core error bodies (HTTP 4xx/5xx on /chat/completions) ----

    private val names = mapOf(
        com.parux.android.R.string.chat_error_missing_runtime to "missing_runtime",
        com.parux.android.R.string.chat_error_missing_model to "missing_model",
        com.parux.android.R.string.chat_error_details to "details",
        com.parux.android.R.string.chat_error_server_detail to "server_detail",
        com.parux.android.R.string.chat_error_server_no_detail to "server_no_detail",
        com.parux.android.R.string.chat_error_out_of_memory to "out_of_memory",
        com.parux.android.R.string.chat_error_gaming_active to "gaming_active",
        com.parux.android.R.string.chat_error_timed_out to "timed_out",
        com.parux.android.R.string.chat_error_ai_off to "ai_off"
    )
    private val fakeText: (Int, Array<out Any>) -> String =
        { id, args -> (listOf(names.getValue(id)) + args.map { it.toString() }).joinToString("|") }

    @Test
    fun runtimeUnavailableBodyKeepsTheCauseWithoutTheAbsolutePath() {
        val body = """{"error":{"message":"File not found: C:\\Users\\example\\Desktop\\My Local Folder\\runtime\\llama-server.exe"}}"""
        val failure = ChatErrors.serverFailure(503, body)
        assertEquals("File not found: llama-server.exe", failure.detail)
        assertEquals(ChatErrors.Reason.MissingRuntime, failure.reason)
        val text = ChatErrors.render(failure, fakeText)
        assertEquals("missing_runtime\ndetails|503|File not found: llama-server.exe", text)
        assertFalse(text.contains("example"))
        assertFalse(text.contains("Desktop"))
        assertFalse(text.contains("Local Folder"))
    }

    @Test
    fun everyCoreErrorShapeIsUnderstood() {
        assertEquals("Model is loading.", ChatErrors.serverDetail("""{"error":"Model is loading.","retry_hint":"Retry after the local runtime is available."}"""))
        assertEquals("Runtime state unavailable.",
            ChatErrors.serverDetail("""{"success":false,"error":"text_runtime_state_unavailable","message":"Runtime state unavailable."}"""))
        assertEquals("Prompt too big. Shorten the prompt.",
            ChatErrors.serverDetail("""{"error":{"code":"prompt_exceeds_context","message":"Prompt too big.","remediation":"Shorten the prompt."}}"""))
        assertEquals("Bad input", ChatErrors.serverDetail("""{"title":"Bad Request","detail":"Bad input"}"""))
        assertEquals("text_runtime_state_unavailable", ChatErrors.serverDetail("""{"error":"text_runtime_state_unavailable"}"""))
        assertEquals("plain text reason", ChatErrors.serverDetail("plain text reason"))
    }

    @Test
    fun emptyOrUselessBodiesGiveNoDetailAndAnExplicitNoReasonText() {
        listOf(null, "", "   ", "<html><body>Bad gateway</body></html>", "{}", "[1,2]", """{"error":{}}""").forEach {
            assertEquals("body: $it", null, ChatErrors.serverDetail(it))
        }
        val failure = ChatErrors.serverFailure(503, "")
        assertEquals("server_no_detail|503", ChatErrors.render(failure, fakeText))
    }

    @Test
    fun unknownCausesAreShownVerbatimAfterSanitizing() {
        val failure = ChatErrors.serverFailure(500, """{"error":{"message":"Something odd happened in the router."}}""")
        assertEquals(ChatErrors.Reason.Unknown, failure.reason)
        assertEquals("server_detail|500|Something odd happened in the router.", ChatErrors.render(failure, fakeText))
    }

    @Test
    fun secretsAndPathsAreRedacted() {
        val s = ChatErrors.sanitize(
            "auth failed: Bearer abcdefghijklmnop123 api_key=sk-live-123 password: \"hunter2\" at /home/example/models/x.gguf " +
                "and \\\\NAS\\share\\models\\y.gguf and D:/Data/z.bin token " + "A".repeat(48))
        assertFalse(s, s.contains("abcdefghijklmnop123"))
        assertFalse(s, s.contains("sk-live-123"))
        assertFalse(s, s.contains("hunter2"))
        assertFalse(s, s.contains("/home"))
        assertFalse(s, s.contains("NAS"))
        assertFalse(s, s.contains("Data"))
        assertFalse(s, s.contains("A".repeat(48)))
        assertTrue(s, s.contains("x.gguf") && s.contains("y.gguf") && s.contains("z.bin"))
    }

    @Test
    fun mediaJobErrorsKeepTheirGuidanceButLoseTheLocalDirectoryLayout() {
        val s = ChatErrors.sanitize(
            "Generation did not start: the local media-assistant runtime is missing or failed to start (File not found: " +
                "D:\\Work Space\\My App\\.data\\LocalAI\\models\\Qwen3.5-4B-Q4_K_M.gguf). Open Runtime Manager to repair it.")
        assertEquals(
            "Generation did not start: the local media-assistant runtime is missing or failed to start (File not found: " +
                "Qwen3.5-4B-Q4_K_M.gguf). Open Runtime Manager to repair it.", s)
    }

    @Test
    fun longDetailsAreBoundedAndControlCharactersRemoved() {
        val s = ChatErrors.sanitize("line one\r\nline\u0007two " + "word ".repeat(200))
        assertTrue(s.length <= ChatErrors.MAX_DETAIL_LENGTH)
        assertTrue(s.endsWith("…"))
        assertTrue(s.startsWith("line one line two"))
    }

    @Test
    fun knownFamiliesGetTheirGuidance() {
        assertEquals(ChatErrors.Reason.MissingModel, ChatErrors.classify("File not found: model.gguf"))
        assertEquals(ChatErrors.Reason.OutOfMemory, ChatErrors.classify("CUDA_ERROR_OUT_OF_MEMORY while loading"))
        assertEquals(ChatErrors.Reason.GamingActive, ChatErrors.classify("A game is currently active."))
        assertEquals(ChatErrors.Reason.AiOff, ChatErrors.classify("PaRuX is in OFF mode."))
        assertEquals(ChatErrors.Reason.TimedOut, ChatErrors.classify("llama-server did not become healthy within 5 minutes."))
        assertEquals(ChatErrors.Reason.MissingRuntime, ChatErrors.classify("PaRuX llama-server runtime is not installed."))
        assertEquals("gaming_active", ChatErrors.render(ChatServerFailure(503, null, ChatErrors.Reason.GamingActive), fakeText))
        // 401 still means "pair again", whatever the body says.
        assertEquals(ChatErrors.PAIR_AGAIN, ChatErrors.render(ChatErrors.serverFailure(401, """{"error":"x"}"""), fakeText))
    }

    @Test
    fun otherFailuresKeepTheirMessage() {
        assertEquals("stream closed", ChatErrors.describe(java.io.IOException("stream closed")))
        assertEquals("Something went wrong.", ChatErrors.describe(IllegalStateException()))
    }
}
