package com.parux.android.ui.jobs

import com.parux.android.R
import androidx.compose.ui.res.stringResource
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import android.net.Uri
import android.util.Base64
import java.io.ByteArrayOutputStream
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.parux.android.core.network.MediaJobStatus

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun JobsScreen(viewModel: JobsViewModel) {
    val state by viewModel.state.collectAsState()
    val localCore by viewModel.localCoreConnection.collectAsState()
    val context = LocalContext.current
    var showCreateDialog by remember { mutableStateOf(false) }
    // Stop HTTP polling when leaving this tab, including Gaming/TV mode.
    // Compose disposal is separate from ViewModel lifetime and must own it.
    DisposableEffect(viewModel) {
        viewModel.startPolling(context.applicationContext)
        onDispose { viewModel.stopPolling() }
    }

    Scaffold(
        topBar = { TopAppBar(title = { Text(stringResource(R.string.jobs_title)) }) },
        floatingActionButton = {
            if (localCore != null) {
                FloatingActionButton(onClick = { showCreateDialog = true }) {
                    Icon(Icons.Default.Add, contentDescription = stringResource(R.string.jobs_new))
                }
            }
        }
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            if (localCore == null) {
                Text(stringResource(R.string.jobs_disconnected),
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall)
            } else if (state.errorMessage != null) {
                Text(state.errorMessage.orEmpty(),
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall)
            }
            if (state.jobs.isEmpty()) {
                Column(
                    Modifier.fillMaxSize().padding(32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    Text(if (localCore == null) stringResource(R.string.jobs_connect) else
                        if (state.errorMessage != null) stringResource(R.string.jobs_unavailable) else stringResource(R.string.jobs_none),
                        style = MaterialTheme.typography.titleMedium)
                    Text(
                        if (localCore == null) stringResource(R.string.jobs_pair_hint)
                        else stringResource(R.string.jobs_tap_hint),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            } else {
                LazyColumn(Modifier.fillMaxSize().padding(12.dp)) {
                    items(state.jobs, key = { it.id }) { job ->
                        JobRow(job, onCancel = { viewModel.cancelJob(job.id) })
                    }
                }
            }
        }
    }

    if (showCreateDialog) {
        CreateJobDialog(
            onDismiss = { if (!state.isSubmitting) showCreateDialog = false },
            isSubmitting = state.isSubmitting,
            submissionError = state.submissionError,
            onCreate = { type, prompt, sourceImageBase64, options ->
                viewModel.createJob(type, prompt, sourceImageBase64,
                    onAccepted = { showCreateDialog = false })
            }
        )
    }
}

private const val MAX_JOB_SOURCE_IMAGE_BYTES = 8 * 1024 * 1024

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CreateJobDialog(
    onDismiss: () -> Unit,
    onCreate: (String, String, String?, MediaAdvancedOptions) -> Unit,
    isSubmitting: Boolean,
    submissionError: String?
) {
    var type by remember { mutableStateOf("image") }
    var prompt by remember { mutableStateOf("") }
    var sourceImageBase64 by remember { mutableStateOf<String?>(null) }
    var sourceImageName by remember { mutableStateOf<String?>(null) }
    var imageError by remember { mutableStateOf<String?>(null) }
    var showAdvanced by remember { mutableStateOf(false) }
    var advanced by remember { mutableStateOf(MediaAdvancedOptions()) }
    val context = LocalContext.current
    val imagePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) {
            // An arbitrary ContentProvider can stream a huge payload even
            // when it reports a tiny size. Cap bytes while reading, *before*
            // converting to a much larger base64 string in phone RAM.
            sourceImageBase64 = null
            sourceImageName = null
            imageError = null
            try {
                val bytes = context.contentResolver.openInputStream(uri)?.use { input ->
                    val out = ByteArrayOutputStream()
                    val buffer = ByteArray(8192)
                    var total = 0
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        total += count
                        if (total > MAX_JOB_SOURCE_IMAGE_BYTES)
                            throw IllegalArgumentException(context.getString(R.string.jobs_img_big))
                        out.write(buffer, 0, count)
                    }
                    out.toByteArray()
                } ?: throw IllegalArgumentException(context.getString(R.string.jobs_img_unreadable))
                if (bytes.isEmpty()) throw IllegalArgumentException(context.getString(R.string.jobs_img_empty))
                sourceImageBase64 = Base64.encodeToString(bytes, Base64.NO_WRAP)
                sourceImageName = "image"
            } catch (e: Exception) {
                imageError = e.message ?: context.getString(R.string.jobs_img_other)
            }
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.jobs_generate)) },
        text = {
            Column {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(selected = type == "image", onClick = { type = "image" }, label = { Text(stringResource(R.string.jobs_image)) })
                    FilterChip(selected = type == "video", onClick = { type = "video" }, label = { Text(stringResource(R.string.jobs_video)) })
                    FilterChip(selected = type == "edit", onClick = { type = "edit" }, label = { Text(stringResource(R.string.jobs_edit)) })
                }
                if (type == "edit") {
                    TextButton(onClick = { imagePicker.launch("image/*") }, modifier = Modifier.padding(top = 8.dp)) {
                        Text(stringResource(if (sourceImageName != null) R.string.jobs_image_attached else R.string.jobs_pick_image))
                    }
                    imageError?.let { issue ->
                        Text(issue, color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodySmall)
                    }
                }
                submissionError?.let { issue ->
                    Text(issue, color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall)
                }
                OutlinedTextField(
                    value = prompt,
                    onValueChange = { prompt = it },
                    label = { Text(stringResource(if (type == "edit") R.string.jobs_edit_instruction else R.string.jobs_prompt)) },
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
                )
                TextButton(onClick = { showAdvanced = !showAdvanced }, modifier = Modifier.padding(top = 4.dp)) {
                    Text(stringResource(if (showAdvanced) R.string.media_adv_hide else R.string.media_adv_show))
                }
                if (showAdvanced) AdvancedOptionsSection(type, advanced) { advanced = it }
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onCreate(type, prompt, sourceImageBase64, advanced) },
                enabled = !isSubmitting && prompt.isNotBlank() && advanced.seedIsValid
                    && (type != "edit" || sourceImageBase64 != null)
            ) { Text(stringResource(if (isSubmitting) R.string.jobs_submitting else R.string.jobs_generate)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } }
    )
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AdvancedOptionsSection(type: String, options: MediaAdvancedOptions, onChange: (MediaAdvancedOptions) -> Unit) {
    Column(Modifier.fillMaxWidth()) {
        if (type != "edit") {
            Text(stringResource(R.string.media_aspect), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 4.dp))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                MediaAspect.values().forEach { aspect ->
                    FilterChip(
                        selected = options.aspect == aspect,
                        onClick = { onChange(options.copy(aspect = aspect)) },
                        label = {
                            Text(stringResource(when (aspect) {
                                MediaAspect.Default -> R.string.media_aspect_default
                                MediaAspect.Square -> R.string.media_aspect_square
                                MediaAspect.Wide -> R.string.media_aspect_wide
                                MediaAspect.Tall -> R.string.media_aspect_tall
                            }))
                        }
                    )
                }
            }
        }
        Text(stringResource(R.string.media_quality), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 8.dp))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            MediaQuality.values().forEach { quality ->
                FilterChip(
                    selected = options.quality == quality,
                    onClick = { onChange(options.copy(quality = quality)) },
                    label = {
                        Text(stringResource(when (quality) {
                            MediaQuality.Fast -> R.string.media_q_fast
                            MediaQuality.Balanced -> R.string.media_q_balanced
                            MediaQuality.Best -> R.string.media_q_best
                        }))
                    }
                )
            }
        }
        if (type == "video") {
            Text(stringResource(R.string.media_duration), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 8.dp))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(3, 5, 8).forEach { seconds ->
                    FilterChip(
                        selected = options.videoSeconds == seconds,
                        onClick = { onChange(options.copy(videoSeconds = seconds)) },
                        label = { Text(stringResource(R.string.media_seconds, seconds)) }
                    )
                }
            }
        }
        OutlinedTextField(
            value = options.seedText,
            onValueChange = { onChange(options.copy(seedText = it.filter { c -> c.isDigit() || c == '-' }.take(19))) },
            label = { Text(stringResource(R.string.media_seed)) },
            isError = !options.seedIsValid,
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
        )
    }
}

@Composable
private fun JobRow(job: MediaJobStatus, onCancel: () -> Unit) {
    Card(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Column(Modifier.padding(12.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("${job.type.replaceFirstChar { it.uppercase() }} · ${job.stage}", style = MaterialTheme.typography.titleSmall)
                Text(job.status, style = MaterialTheme.typography.labelMedium)
            }
            if (job.status == "running" || job.status == "cancelling") {
                LinearProgressIndicator(
                    progress = { (job.progressPercent / 100f).coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                )
                if (job.status == "running") job.etaSeconds?.takeIf { it in 1..86400 }?.let {
                    Text(stringResource(R.string.jobs_eta, it / 60, it % 60),
                        style = MaterialTheme.typography.labelSmall)
                }
            }
            // The Core's job error can quote absolute PC paths (e.g. a missing model file); show the cause on the phone
            // without the local directory layout.
            job.error?.let { Text(com.parux.android.ui.chat.ChatErrors.sanitize(it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            if (job.status == "queued" || job.status == "running") {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    TextButton(onClick = onCancel) { Text(stringResource(R.string.common_cancel)) }
                }
            }
        }
    }
}
