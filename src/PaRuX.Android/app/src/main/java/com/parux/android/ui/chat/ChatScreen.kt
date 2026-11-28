package com.parux.android.ui.chat

import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material.icons.filled.Visibility
import com.parux.android.R
import androidx.compose.ui.res.stringResource
import android.Manifest
import android.content.ContentResolver
import android.content.pm.PackageManager
import android.net.Uri
import android.util.Base64
import java.io.ByteArrayOutputStream
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MicOff
import androidx.compose.material.icons.filled.Send
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.parux.android.core.network.ChatRoutingPreference
import com.parux.android.core.network.ConversationSummary
import com.parux.android.core.network.ManagedChatModelInfo
import com.parux.android.core.network.ProjectDto

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(viewModel: ChatViewModel) {
    val state by viewModel.state.collectAsState()
    val localCore by viewModel.localCoreConnection.collectAsState()
    var showHistory by remember { mutableStateOf(false) }
    // Text is safe to preserve through Activity recreation. The base64 image
    // is deliberately NOT stored in savedInstanceState (Android binder limit).
    var composerText by rememberSaveable { mutableStateOf("") }
    var pendingImage by remember { mutableStateOf<PendingImage?>(null) }
    var attachmentError by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val listState = rememberLazyListState()

    LaunchedEffect(Unit) {
        viewModel.refreshHealthAndProfile()
        viewModel.refreshModelRouting()
        viewModel.refreshConversationList()
        viewModel.refreshSkills()
        viewModel.refreshProjects()
    }
    LaunchedEffect(state.current.messages.size) {
        if (state.current.messages.isNotEmpty()) listState.animateScrollToItem(state.current.messages.size - 1)
    }

    val imagePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) {
            val image = uriToPendingImage(context.contentResolver, uri)
            if (image == null) {
                attachmentError = context.getString(R.string.chat_attach_failed)
            } else {
                pendingImage = image
                attachmentError = null
            }
        }
    }
    val microphonePermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) viewModel.startVoiceMode() else viewModel.microphonePermissionDenied()
    }

    fun toggleVoice() {
        if (state.voiceModeEnabled) {
            if (state.voicePhase == LiveVoicePhase.Speaking) viewModel.interruptVoicePlayback()
            else viewModel.stopVoiceMode()
            return
        }
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            viewModel.startVoiceMode()
        } else microphonePermission.launch(Manifest.permission.RECORD_AUDIO)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(androidx.compose.ui.res.stringResource(com.parux.android.R.string.app_display_name)) },
                actions = {
                    IconButton(onClick = { viewModel.setIncognito(!state.incognito) }) {
                        Icon(
                            if (state.incognito) Icons.Default.VisibilityOff else Icons.Default.Visibility,
                            contentDescription = stringResource(if (state.incognito) R.string.chat_incognito_on else R.string.chat_incognito_off),
                            tint = if (state.incognito) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    IconButton(onClick = { showHistory = true }) { Icon(Icons.Default.History, contentDescription = stringResource(R.string.chat_history)) }
                    IconButton(onClick = { viewModel.newConversation() }) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.chat_new)) }
                }
            )
        }
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize().imePadding()) {
            if (state.gamingMode) GamingBanner()
            if (state.incognito) {
                Surface(color = MaterialTheme.colorScheme.secondaryContainer, modifier = Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.chat_incognito_banner),
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSecondaryContainer)
                }
            }
            if (localCore == null) {
                Surface(color = MaterialTheme.colorScheme.surfaceVariant,
                    modifier = Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.chat_not_connected),
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            ProfileRow(current = state.aiProfile, onChange = viewModel::setAiProfile)
            ModelSelectorRow(
                preference = state.modelRouting,
                models = state.managedModels,
                onAuto = viewModel::setAutoModelRouting,
                onManual = viewModel::setManualModelRouting
            )
            ProjectSelectorRow(state.projects, state.selectedProjectId, viewModel::setProject)
            SkillSelectorRow(state.skills, state.forcedSkillLabel) { id, label -> viewModel.setForcedSkill(id, label) }
            if (state.voiceModeEnabled) {
                LiveVoiceStatusBar(state.voicePhase, state.voiceLastTranscript, viewModel::interruptVoicePlayback, viewModel::stopVoiceMode)
            }

            if (state.current.messages.isEmpty()) {
                EmptyState(Modifier.weight(1f), state.selectedProjectId != null)
            } else {
                LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = listState, contentPadding = PaddingValues(12.dp)) {
                    val lastAnswerId = state.current.messages.lastOrNull { it.role == "assistant" }?.id
                    items(state.current.messages, key = { it.id }) { message ->
                        MessageBubble(
                            message,
                            canRegenerate = message.id == lastAnswerId && !state.isStreaming,
                            onRegenerate = { viewModel.regenerateLast() }
                        )
                    }
                }
            }

            state.errorMessage?.let { message ->
                Surface(color = MaterialTheme.colorScheme.errorContainer, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(message, color = MaterialTheme.colorScheme.onErrorContainer, modifier = Modifier.weight(1f))
                        TextButton(onClick = viewModel::dismissError) { Text(stringResource(R.string.common_dismiss)) }
                    }
                }
            }

            attachmentError?.let { problem ->
                Text(problem, modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }

            pendingImage?.let { image ->
                Row(Modifier.padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.chat_attached, image.fileName), style = MaterialTheme.typography.labelMedium, modifier = Modifier.weight(1f))
                    TextButton(onClick = { pendingImage = null }) { Text(stringResource(R.string.common_remove)) }
                }
            }

            Composer(
                text = composerText,
                onTextChange = { composerText = it },
                isStreaming = state.isStreaming,
                hasAttachment = pendingImage != null,
                voiceModeEnabled = state.voiceModeEnabled,
                voicePhase = state.voicePhase,
                onVoiceClick = ::toggleVoice,
                onAttachClick = { imagePicker.launch("image/*") },
                onSend = {
                    val toSend = composerText
                    val image = pendingImage
                    val (skillId, stripped) = viewModel.parseSlashCommand(toSend.trimStart())
                    val accepted = if (skillId != null) viewModel.sendMessage(stripped, image, skillId)
                        else viewModel.sendMessage(toSend, image)
                    if (accepted) {
                        composerText = ""
                        pendingImage = null
                        attachmentError = null
                    }
                },
                onStop = viewModel::stopGeneration
            )
        }
    }

    if (showHistory) {
        HistorySheet(state.conversations, { showHistory = false }, viewModel::deleteConversation) { id ->
            viewModel.openConversation(id)
            showHistory = false
        }
    }
}

@Composable
private fun ModelSelectorRow(
    preference: ChatRoutingPreference,
    models: List<ManagedChatModelInfo>,
    onAuto: (String) -> Unit,
    onManual: (String) -> Unit
) {
    var expanded by remember { mutableStateOf(false) }
    var filter by remember { mutableStateOf("") }
    var sort by remember { mutableStateOf(MobileModelSort.Name) }
    val current = if (preference.mode == "manual") {
        val file = preference.manualModelFileName ?: stringResource(R.string.chat_model_missing)
        val alias = models.firstOrNull { it.fileName.equals(file, ignoreCase = true) }?.alias
        stringResource(R.string.chat_model_manual, alias?.takeIf { it.isNotBlank() } ?: file)
    } else {
        stringResource(R.string.chat_model_auto, when (preference.autoProfile) {
            "max_intelligence" -> stringResource(R.string.chat_auto_max)
            "responsive" -> stringResource(R.string.chat_auto_responsive)
            "custom" -> stringResource(R.string.chat_auto_custom)
            else -> stringResource(R.string.chat_auto_balanced)
        })
    }

    // One pure bounded policy for selection, search and order. No GPU load or
    // routing change occurs until the user taps an actual installed model.
    val modelPage = MobileModelBrowsePolicy.browse(
        models, filter, sort,
        preference.manualModelFileName.takeIf { preference.mode == "manual" })
    val visible = modelPage.visible
    val matchingCount = modelPage.totalMatches

    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp)) {
        Box {
            TextButton(onClick = { filter = ""; expanded = true }) { Text(stringResource(R.string.chat_model_label, current)) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                listOf(
                    "balanced" to stringResource(R.string.chat_model_auto, stringResource(R.string.chat_auto_balanced)),
                    "max_intelligence" to stringResource(R.string.chat_model_auto, stringResource(R.string.chat_auto_max)),
                    "responsive" to stringResource(R.string.chat_model_auto, stringResource(R.string.chat_auto_responsive)),
                    "custom" to stringResource(R.string.chat_model_auto, stringResource(R.string.chat_auto_custom_pc))
                ).forEach { (profile, label) ->
                    DropdownMenuItem(text = { Text(label) }, onClick = {
                        onAuto(profile)
                        expanded = false
                    })
                }
                if (models.size > 10) {
                    OutlinedTextField(
                        value = filter,
                        onValueChange = { filter = it },
                        placeholder = { Text(stringResource(R.string.chat_search_models)) },
                        singleLine = true,
                        modifier = Modifier.width(280.dp).padding(horizontal = 8.dp)
                    )
                }
                if (models.size > 1) {
                    Row(
                        Modifier.padding(horizontal = 8.dp),
                        horizontalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        FilterChip(
                            selected = sort == MobileModelSort.Name,
                            onClick = { sort = MobileModelSort.Name },
                            label = { Text(stringResource(R.string.chat_sort_name)) }
                        )
                        FilterChip(
                            selected = sort == MobileModelSort.SmallestFile,
                            onClick = { sort = MobileModelSort.SmallestFile },
                            label = { Text(stringResource(R.string.chat_sort_small)) }
                        )
                        FilterChip(
                            selected = sort == MobileModelSort.MeasuredSpeed,
                            onClick = { sort = MobileModelSort.MeasuredSpeed },
                            label = { Text(stringResource(R.string.chat_sort_speed)) }
                        )
                    }
                }
                if (matchingCount == 0) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.chat_no_models_match)) },
                        onClick = {}, enabled = false
                    )
                }
                visible.forEach { model ->
                    val display = model.alias?.takeIf { it.isNotBlank() }?.let { "$it · ${model.fileName}" } ?: model.fileName
                    val size = model.sizeBytes.takeIf { it > 0 }?.let {
                        " · ${"%.1f".format(it / 1073741824.0)} GiB"
                    } ?: ""
                    val speed = model.lastBenchmarkTokensPerSecond?.takeIf { it.isFinite() && it > 0 }?.let {
                        " · " + androidx.compose.ui.platform.LocalContext.current.getString(R.string.chat_tok_s, "%.1f".format(it))
                    } ?: ""
                    DropdownMenuItem(text = { Text(stringResource(R.string.chat_model_manual, "$display$size$speed")) }, onClick = {
                        onManual(model.fileName)
                        expanded = false
                    })
                }
                if (matchingCount > visible.size) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.chat_showing_of, visible.size, matchingCount)) },
                        onClick = {}, enabled = false
                    )
                }
            }
        }
    }
}

@Composable
private fun LiveVoiceStatusBar(phase: LiveVoicePhase, transcript: String?, onInterrupt: () -> Unit, onStop: () -> Unit) {
    Surface(tonalElevation = 2.dp, modifier = Modifier.fillMaxWidth()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Icon(Icons.Default.Mic, contentDescription = null)
            Column(Modifier.weight(1f)) {
                Text(stringResource(R.string.chat_live_voice, voicePhaseLabel(phase)), style = MaterialTheme.typography.labelLarge)
                transcript?.takeIf { it.isNotBlank() }?.let {
                    Text(stringResource(R.string.chat_heard, it.take(90) + (if (it.length > 90) "…" else "")), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            if (phase == LiveVoicePhase.Speaking) TextButton(onClick = onInterrupt) { Text(stringResource(R.string.chat_interrupt)) }
            TextButton(onClick = onStop) { Text(stringResource(R.string.common_stop)) }
        }
    }
}

@Composable
private fun voicePhaseLabel(phase: LiveVoicePhase): String = stringResource(when (phase) {
    LiveVoicePhase.Off -> R.string.chat_voice_off
    LiveVoicePhase.Checking -> R.string.chat_voice_checking
    LiveVoicePhase.Listening -> R.string.chat_voice_listening
    LiveVoicePhase.Transcribing -> R.string.chat_voice_transcribing
    LiveVoicePhase.Thinking -> R.string.chat_voice_thinking
    LiveVoicePhase.Speaking -> R.string.chat_voice_speaking
})

@Composable
private fun ProfileRow(current: String, onChange: (String) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        listOf("STANDARD", "UNCENSORED").forEach { option ->
            FilterChip(selected = current == option, onClick = { onChange(option) }, label = { Text(stringResource(if (option == "UNCENSORED") R.string.chat_profile_uncensored else R.string.chat_profile_standard)) })
        }
    }
}

@Composable
private fun ProjectSelectorRow(projects: List<ProjectDto>, selectedProjectId: String?, onSelect: (String?) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    val selected = projects.firstOrNull { it.id == selectedProjectId }
    val label = selected?.goal?.take(36) ?: stringResource(R.string.chat_project_none_short)
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp)) {
        Box {
            TextButton(onClick = { expanded = true }) { Text(stringResource(R.string.chat_project, label)) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                DropdownMenuItem(text = { Text(stringResource(R.string.chat_project_none)) }, onClick = { onSelect(null); expanded = false })
                projects.forEach { project ->
                    DropdownMenuItem(text = { Text(project.goal) }, onClick = { onSelect(project.id); expanded = false })
                }
            }
        }
    }
}

@Composable
private fun SkillSelectorRow(
    skills: List<com.parux.android.core.network.SkillManifest>,
    selectedLabel: String,
    onSelect: (String?, String) -> Unit
) {
    var expanded by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp)) {
        Box {
            TextButton(onClick = { expanded = true }) { Text(stringResource(R.string.chat_skill, selectedLabel)) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                val autoLabel = stringResource(R.string.settings_auto)
                DropdownMenuItem(text = { Text(autoLabel) }, onClick = { onSelect(null, autoLabel); expanded = false })
                skills.sortedBy { it.displayName }.forEach { skill ->
                    DropdownMenuItem(text = { Text(skill.displayName) }, onClick = { onSelect(skill.id, skill.displayName); expanded = false })
                }
            }
        }
    }
}

@Composable
private fun GamingBanner() {
    Surface(color = MaterialTheme.colorScheme.tertiaryContainer, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp)) {
            Text(stringResource(R.string.settings_gaming_mode), style = MaterialTheme.typography.titleSmall)
            Text(stringResource(R.string.chat_gaming_body), style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun EmptyState(modifier: Modifier = Modifier, projectScoped: Boolean = false) {
    Column(modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
        Text(stringResource(if (projectScoped) R.string.chat_empty_title_project else R.string.chat_empty_title), style = MaterialTheme.typography.titleMedium)
        Text(
            stringResource(if (projectScoped) R.string.chat_empty_body_project else R.string.chat_empty_body),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun MessageBubble(message: UiMessage, canRegenerate: Boolean = false, onRegenerate: () -> Unit = {}) {
    val isUser = message.role == "user"
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = if (isUser) Arrangement.End else Arrangement.Start) {
        Column(horizontalAlignment = if (isUser) Alignment.End else Alignment.Start) {
            if (message.switchedToUncensored) {
                Text(stringResource(R.string.chat_switched_uncensored), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.tertiary, modifier = Modifier.padding(bottom = 2.dp))
            }
            message.modelRouteLabel?.let {
                Text(stringResource(R.string.chat_model_prefix, it), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(bottom = 2.dp))
            }
            // Above the bubble, not below it: the user should know the turn left their PC before
            // they read the answer, not after.
            message.cloudFallbackNotice?.let {
                Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(bottom = 2.dp))
            }
            Card(
                colors = androidx.compose.material3.CardDefaults.cardColors(
                    containerColor = when {
                        message.isError -> MaterialTheme.colorScheme.errorContainer
                        isUser -> MaterialTheme.colorScheme.primaryContainer
                        else -> MaterialTheme.colorScheme.surfaceVariant
                    }
                )
            ) {
                Box(Modifier.padding(12.dp)) {
                    if (message.content.isEmpty() && message.isStreaming) CircularProgressIndicator(Modifier.height(16.dp).width(16.dp), strokeWidth = 2.dp)
                    else MarkdownText(message.content)
                }
            }
            if (!isUser && !message.isStreaming && message.content.isNotBlank()) {
                val clipboard = androidx.compose.ui.platform.LocalClipboardManager.current
                var copied by remember(message.id) { mutableStateOf(false) }
                Row {
                    TextButton(onClick = {
                        clipboard.setText(androidx.compose.ui.text.AnnotatedString(message.content))
                        copied = true
                    }) { Text(stringResource(if (copied) R.string.chat_copied else R.string.common_copy), style = MaterialTheme.typography.labelSmall) }
                    if (canRegenerate) TextButton(onClick = onRegenerate) {
                        Text(stringResource(R.string.chat_regenerate), style = MaterialTheme.typography.labelSmall)
                    }
                }
            }
            message.route?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            message.modelRouteReason?.takeIf { it.isNotBlank() }?.let {
                Text(stringResource(R.string.chat_why, it.take(180) + (if (it.length > 180) "…" else "")), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 2.dp))
            }
            if (message.skillUsed != null || message.ragSources.isNotEmpty()) {
                val skillPart = message.skillUsed?.let { stringResource(R.string.chat_skill, it) }
                val sourcesPart = if (message.ragSources.isNotEmpty()) stringResource(R.string.chat_skill_sources, message.ragSources.joinToString(", ")) else null
                Text(listOfNotNull(skillPart, sourcesPart).joinToString("  ·  "), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 2.dp))
            }
        }
    }
}

@Composable
private fun Composer(
    text: String,
    onTextChange: (String) -> Unit,
    isStreaming: Boolean,
    hasAttachment: Boolean,
    voiceModeEnabled: Boolean,
    voicePhase: LiveVoicePhase,
    onVoiceClick: () -> Unit,
    onAttachClick: () -> Unit,
    onSend: () -> Unit,
    onStop: () -> Unit
) {
    Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        IconButton(onClick = onAttachClick) { Icon(Icons.Default.AttachFile, contentDescription = stringResource(R.string.chat_attach_image)) }
        IconButton(onClick = onVoiceClick) {
            Icon(
                if (voiceModeEnabled && voicePhase != LiveVoicePhase.Speaking) Icons.Default.MicOff else Icons.Default.Mic,
                contentDescription = when {
                    !voiceModeEnabled -> stringResource(R.string.chat_voice_start)
                    voicePhase == LiveVoicePhase.Speaking -> stringResource(R.string.chat_voice_interrupt)
                    else -> stringResource(R.string.chat_voice_stop)
                }
            )
        }
        OutlinedTextField(
            value = text,
            onValueChange = onTextChange,
            modifier = Modifier.weight(1f),
            placeholder = { Text(stringResource(if (voiceModeEnabled) R.string.chat_voice_active_hint else R.string.chat_message_hint)) },
            maxLines = 6
        )
        Spacer(Modifier.width(4.dp))
        if (isStreaming) IconButton(onClick = onStop) { Icon(Icons.Default.Stop, contentDescription = stringResource(R.string.common_stop)) }
        else IconButton(onClick = onSend, enabled = text.isNotBlank() || hasAttachment) { Icon(Icons.Default.Send, contentDescription = stringResource(R.string.common_send)) }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun HistorySheet(
    conversations: List<ConversationSummary>,
    onDismiss: () -> Unit,
    onDelete: (String) -> Unit,
    onSelect: (String) -> Unit
) {
    val sheetState = rememberModalBottomSheetState()
    var pendingDelete by remember { mutableStateOf<ConversationSummary?>(null) }
    pendingDelete?.let { target ->
        androidx.compose.material3.AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text(stringResource(R.string.chat_delete_confirm_title)) },
            text = { Text(stringResource(R.string.chat_delete_confirm_body)) },
            confirmButton = { TextButton(onClick = { onDelete(target.id); pendingDelete = null }) { Text(stringResource(R.string.chat_delete)) } },
            dismissButton = { TextButton(onClick = { pendingDelete = null }) { Text(stringResource(R.string.common_cancel)) } }
        )
    }
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        Column(Modifier.padding(16.dp)) {
            Text(stringResource(R.string.chat_conversations), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(bottom = 8.dp))
            if (conversations.isEmpty()) Text(stringResource(R.string.chat_no_conversations), color = MaterialTheme.colorScheme.onSurfaceVariant)
            LazyColumn {
                items(conversations, key = { it.id }) { conversation ->
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Surface(onClick = { onSelect(conversation.id) }, modifier = Modifier.weight(1f)) {
                            Text(conversation.title, Modifier.padding(vertical = 12.dp))
                        }
                        IconButton(onClick = { pendingDelete = conversation }) {
                            Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.chat_delete_conversation))
                        }
                    }
                }
            }
        }
    }
}

private const val MAX_CHAT_IMAGE_BYTES = 8 * 1024 * 1024

private fun uriToPendingImage(resolver: ContentResolver, uri: Uri): PendingImage? {
    return try {
    val mimeType = resolver.getType(uri) ?: return null
    if (!mimeType.startsWith("image/")) return null
    // Content providers may not supply a trustworthy length. Bound the
    // streamed bytes before base64 expansion to protect low-memory phones.
    val bytes = resolver.openInputStream(uri)?.use { input ->
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        var size = 0
        while (true) {
            val n = input.read(buffer)
            if (n < 0) break
            size += n
            if (size > MAX_CHAT_IMAGE_BYTES) return null
            output.write(buffer, 0, n)
        }
        output.toByteArray()
    } ?: return null
    if (bytes.isEmpty()) return null
    PendingImage(
        fileName = "attachment.${mimeType.substringAfterLast('/')}",
        mediaType = mimeType,
        base64Data = Base64.encodeToString(bytes, Base64.NO_WRAP)
    )
    } catch (_: Exception) {
        null
    }
}
