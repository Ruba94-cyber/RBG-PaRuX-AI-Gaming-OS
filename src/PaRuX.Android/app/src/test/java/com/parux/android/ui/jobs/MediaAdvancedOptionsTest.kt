package com.parux.android.ui.jobs

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MediaAdvancedOptionsTest {
    @Test
    fun defaultsProduceTheSameRequestAsThePromptOnlyPath() {
        val request = MediaAdvancedOptions().toRequest("image", "a cat", null)
        assertEquals(1024, request.width)
        assertEquals(1024, request.height)
        assertEquals(30, request.steps)
        assertEquals(-1L, request.seed)
        assertEquals(5.0, request.durationSeconds, 0.0)
    }

    @Test
    fun aspectMapsToDifferentSizesForImageAndVideoAndIsIgnoredForEdits() {
        val wide = MediaAdvancedOptions(aspect = MediaAspect.Wide)
        assertEquals(1280 to 720, wide.toRequest("image", "p", null).let { it.width to it.height })
        val square = MediaAdvancedOptions(aspect = MediaAspect.Square)
        assertEquals(768 to 768, square.toRequest("video", "p", null).let { it.width to it.height })
        assertEquals(1024 to 1024, wide.toRequest("edit", "p", "abc").let { it.width to it.height })
    }

    @Test
    fun qualityAndSeedAndVideoLengthAreForwarded() {
        val options = MediaAdvancedOptions(quality = MediaQuality.Best, seedText = " 12345 ", videoSeconds = 8)
        val video = options.toRequest("video", "p", null)
        assertEquals(45, video.steps)
        assertEquals(12345L, video.seed)
        assertEquals(8.0, video.durationSeconds, 0.0)
        assertEquals(5.0, options.toRequest("image", "p", null).durationSeconds, 0.0)
    }

    @Test
    fun editKeepsItsSourceImage() {
        assertEquals("b64", MediaAdvancedOptions().toRequest("edit", "p", "b64").sourceImageBase64)
    }

    @Test
    fun seedValidation() {
        assertEquals(-1L, MediaAdvancedOptions(seedText = "").seedOrNull())
        assertEquals(0L, MediaAdvancedOptions(seedText = "0").seedOrNull())
        assertNull(MediaAdvancedOptions(seedText = "-5").seedOrNull())
        assertNull(MediaAdvancedOptions(seedText = "12ab").seedOrNull())
        assertNull(MediaAdvancedOptions(seedText = "1".repeat(19)).seedOrNull())
        assertTrue(MediaAdvancedOptions(seedText = "1".repeat(18)).seedIsValid)
        assertFalse(MediaAdvancedOptions(seedText = "x").seedIsValid)
    }
}
