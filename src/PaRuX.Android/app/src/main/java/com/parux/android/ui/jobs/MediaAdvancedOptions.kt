package com.parux.android.ui.jobs

import com.parux.android.core.network.MediaJobRequest

/** Output shape. [Default] leaves the PC's own defaults untouched, so the prompt-only path is unchanged. */
enum class MediaAspect(val imageSize: Pair<Int, Int>?, val videoSize: Pair<Int, Int>?) {
    Default(null, null),
    Square(1024 to 1024, 768 to 768),
    Wide(1280 to 720, 1280 to 720),
    Tall(720 to 1280, 720 to 1280)
}

/** Steps preset; [Balanced] equals the PC default (30). The server clamps steps to 8..60 anyway. */
enum class MediaQuality(val steps: Int) { Fast(20), Balanced(30), Best(45) }

/**
 * Optional "Advanced" choices for a phone-created generation job. Everything maps onto fields the Core
 * already accepts on `POST /api/v1/jobs`; nothing here reaches a model path or a desktop-only setting.
 */
data class MediaAdvancedOptions(
    val aspect: MediaAspect = MediaAspect.Default,
    val quality: MediaQuality = MediaQuality.Balanced,
    val seedText: String = "",
    val videoSeconds: Int = 5
) {
    /** Blank means "random" (-1). Returns null when the text is not a valid non-negative seed. */
    fun seedOrNull(): Long? {
        val trimmed = seedText.trim()
        if (trimmed.isEmpty()) return -1L
        if (trimmed.length > 18 || !trimmed.all { it in '0'..'9' }) return null
        return trimmed.toLong()
    }

    val seedIsValid: Boolean get() = seedOrNull() != null

    fun toRequest(type: String, prompt: String, sourceImageBase64: String?): MediaJobRequest {
        val base = MediaJobRequest(type = type, prompt = prompt, sourceImageBase64 = sourceImageBase64 ?: "")
        val size = when (type) {
            "image" -> aspect.imageSize
            "video" -> aspect.videoSize
            else -> null // an edit keeps the source image's own size
        }
        return base.copy(
            width = size?.first ?: base.width,
            height = size?.second ?: base.height,
            steps = quality.steps,
            seed = seedOrNull() ?: -1L,
            durationSeconds = if (type == "video") videoSeconds.toDouble() else base.durationSeconds
        )
    }
}
