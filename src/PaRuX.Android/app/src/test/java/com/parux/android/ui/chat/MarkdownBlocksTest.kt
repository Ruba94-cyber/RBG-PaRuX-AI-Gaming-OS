package com.parux.android.ui.chat

import org.junit.Assert.assertEquals
import org.junit.Test

class MarkdownBlocksTest {
    @Test
    fun aReplyEndingWithNewlinesDoesNotRenderATrailingEmptyLine() {
        assertEquals(listOf(Block.Text("Hello there.")), splitFencedBlocks("Hello there.\n"))
        assertEquals(listOf(Block.Text("Hello there.")), splitFencedBlocks("Hello there.\n\n \n"))
        // Even a reply without a final newline used to gain one, because every line was followed by "\n".
        assertEquals(listOf(Block.Text("Hello there.")), splitFencedBlocks("Hello there."))
    }

    @Test
    fun leadingBlankLinesAreDroppedButInnerParagraphBreaksStay() {
        assertEquals(listOf(Block.Text("One.\n\nTwo.")), splitFencedBlocks("\n\nOne.\n\nTwo.\n"))
    }

    @Test
    fun codeBlocksKeepIndentationAndLoseOnlyOuterBlankLines() {
        val blocks = splitFencedBlocks("Intro:\n\n```kotlin\n\n    val x = 1\n\n```\n\nAfter.\n")
        assertEquals(
            listOf(Block.Text("Intro:"), Block.Code("kotlin", "    val x = 1"), Block.Text("After.")),
            blocks
        )
    }

    @Test
    fun blankOnlyTextProducesNoBlocks() {
        assertEquals(emptyList<Block>(), splitFencedBlocks("\n \n"))
    }

    @Test
    fun theFinishedTurnIsStoredWithoutOuterBlankLines() {
        assertEquals("Ciao!", finalAssistantText("Ciao!\n"))
        assertEquals("Ciao!", finalAssistantText("\n\nCiao!\n\n"))
        assertEquals("  indented", finalAssistantText("\n  indented\n"))
        assertEquals("a\n\nb", finalAssistantText("a\n\nb"))
        assertEquals("", finalAssistantText("\n"))
    }
}
