package com.parux.android.ui.studio

import androidx.compose.ui.res.stringResource
import com.parux.android.R
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.parux.android.core.network.AgentTaskDto
import com.parux.android.core.network.MobileArtifactDto
import com.parux.android.core.network.PcComputerUseApproval
import com.parux.android.core.network.ProjectDto
import com.parux.android.core.network.TimelineEntry
import com.parux.android.core.network.WorkflowDto

/**
 * Mobile command center for the Windows PC. It is intentionally supervisory/orchestration-first:
 * no Windows filesystem path, shell, mouse or keyboard is exposed to Android directly. Coding and
 * Computer Use requests become durable Core tasks and are executed by the already-running Windows
 * workers, with their status/log/result visible here. Generated files are downloaded only by
 * durable Artifact id/version through the Core-owned mobile transfer boundary.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PcStudioScreen(viewModel: PcStudioViewModel) {
    val state by viewModel.state.collectAsState()
    val context = LocalContext.current
    var section by remember { mutableStateOf("Work") }
    var showProjectDialog by remember { mutableStateOf(false) }
    var workflowToRun by remember { mutableStateOf<WorkflowDto?>(null) }
    var pendingArtifact by remember { mutableStateOf<MobileArtifactDto?>(null) }
    val createDocument = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
        val artifact = pendingArtifact
        pendingArtifact = null
        if (uri != null && artifact != null) viewModel.downloadArtifact(context, artifact, uri)
    }

    LaunchedEffect(Unit) { viewModel.startPolling() }
    LaunchedEffect(section, state.selectedProjectId) {
        if (section == "Knowledge" && state.selectedProjectId != null) viewModel.refreshKnowledge()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.studio_title)) },
                actions = {
                    IconButton(onClick = {
                        viewModel.refresh()
                        if (section == "Knowledge") viewModel.refreshKnowledge()
                    }) {
                        Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.studio_refresh))
                    }
                }
            )
        }
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            if (state.loading || state.actionInFlight || (section == "Knowledge" && state.knowledgeLoading)) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
            }

            state.message?.let { message ->
                Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp)) {
                    Row(Modifier.fillMaxWidth().padding(12.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(message, modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                        TextButton(onClick = viewModel::dismissMessage) { Text("OK") }
                    }
                }
            }
            state.error?.let { error ->
                Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp)) {
                    Row(Modifier.fillMaxWidth().padding(12.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(error, modifier = Modifier.weight(1f), color = MaterialTheme.colorScheme.error)
                        TextButton(onClick = viewModel::dismissError) { Text(stringResource(R.string.common_dismiss)) }
                    }
                }
            }

            if (state.pendingApprovals.isNotEmpty()) {
                val first = state.pendingApprovals.first()
                Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp)) {
                    Column(Modifier.padding(12.dp)) {
                        Text(stringResource(R.string.studio_cu_needed), style = MaterialTheme.typography.titleSmall)
                        Text(
                            if (state.pendingApprovals.size == 1)
                                first.goal
                            else
                                stringResource(R.string.studio_cu_waiting, state.pendingApprovals.size),
                            style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(top = 4.dp)
                        )
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                            TextButton(onClick = { viewModel.requestComputerUseApproval(first) }) {
                                Text(stringResource(R.string.common_review_approval))
                            }
                        }
                    }
                }
            }

            val pendingUserInputTasks = state.tasks.filter(PcStudioViewModel::isUserInputPending)
            if (pendingUserInputTasks.isNotEmpty()) {
                val first = pendingUserInputTasks.first()
                Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp)) {
                    Column(Modifier.padding(12.dp)) {
                        Text(stringResource(R.string.studio_needs_input), style = MaterialTheme.typography.titleSmall)
                        Text(
                            if (pendingUserInputTasks.size == 1)
                                first.goal
                            else
                                stringResource(R.string.studio_paused_tasks, pendingUserInputTasks.size),
                            style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(top = 4.dp)
                        )
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                            TextButton(onClick = { viewModel.requestUserQuestion(first) }) {
                                Text(stringResource(R.string.studio_answer_question))
                            }
                        }
                    }
                }
            }

            LazyRow(
                Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(listOf("Work", "Knowledge", "Files", "Workflows", "Activity")) { label ->
                    FilterChip(selected = section == label, onClick = { section = label }, label = { Text(stringResource(when (label) { "Work" -> R.string.studio_work; "Knowledge" -> R.string.studio_knowledge; "Files" -> R.string.studio_files; "Workflows" -> R.string.studio_workflows_title; else -> R.string.studio_activity })) })
                }
            }

            when (section) {
                "Knowledge" -> ProjectKnowledgeSection(
                    state = state,
                    onSelectProject = viewModel::selectProject,
                    onRefresh = viewModel::refreshKnowledge
                )
                "Files" -> FilesSection(
                    state = state,
                    onSelectProject = viewModel::selectProject,
                    onSave = { artifact ->
                        pendingArtifact = artifact
                        createDocument.launch(suggestedArtifactFileName(artifact))
                    }
                )
                stringResource(R.string.studio_workflows_title) -> WorkflowSection(state.workflows, onRun = { workflowToRun = it })
                "Activity" -> ActivitySection(state.timeline)
                else -> WorkSection(
                    state = state,
                    onCreateProject = { showProjectDialog = true },
                    onSelectProject = viewModel::selectProject,
                    onStartTask = viewModel::startPcTask,
                    onCancelTask = viewModel::cancelTask,
                    onShowLogs = viewModel::showTaskLogs,
                    onShowResult = viewModel::showTaskResult,
                    onContinue = viewModel::requestContinuation,
                    onReviewApproval = viewModel::requestComputerUseApproval,
                    onAnswerQuestion = viewModel::requestUserQuestion
                )
            }
        }
    }

    if (showProjectDialog) {
        TextInputDialog(
            title = stringResource(R.string.studio_new_project),
            label = stringResource(R.string.common_goal),
            confirmLabel = stringResource(R.string.common_create),
            onDismiss = { showProjectDialog = false },
            onConfirm = {
                viewModel.createProject(it)
                showProjectDialog = false
            }
        )
    }

    workflowToRun?.let { workflow ->
        TextInputDialog(
            title = stringResource(R.string.studio_run_workflow, workflow.name, workflow.versionNumber),
            label = stringResource(R.string.studio_input_optional),
            confirmLabel = stringResource(R.string.common_run),
            allowBlank = true,
            onDismiss = { workflowToRun = null },
            onConfirm = {
                viewModel.runWorkflow(workflow.id, it)
                workflowToRun = null
            }
        )
    }

    state.continuationParent?.let { parent ->
        TextInputDialog(
            title = stringResource(R.string.studio_continue_title, parent.role ?: stringResource(R.string.common_task), parent.id.take(8)),
            label = stringResource(R.string.studio_do_next),
            confirmLabel = stringResource(R.string.studio_continue_pc),
            onDismiss = viewModel::dismissContinuation,
            onConfirm = { viewModel.continueTask(parent, it) }
        )
    }

    state.approvalTask?.let { task ->
        AlertDialog(
            onDismissRequest = viewModel::dismissComputerUseApproval,
            title = { Text(stringResource(R.string.studio_allow_once)) },
            text = {
                Column {
                    Text(task.goal, style = MaterialTheme.typography.titleSmall)
                    Text(
                        stringResource(R.string.studio_approve_body),
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(top = 10.dp)
                    )
                    Text(
                        stringResource(R.string.studio_uac_note),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 8.dp)
                    )
                }
            },
            confirmButton = {
                TextButton(
                    onClick = { viewModel.approveComputerUseOnce(task) },
                    enabled = !state.actionInFlight
                ) { Text(stringResource(R.string.studio_approve_once)) }
            },
            dismissButton = {
                TextButton(
                    onClick = { viewModel.denyComputerUse(task) },
                    enabled = !state.actionInFlight
                ) { Text(stringResource(R.string.common_deny)) }
            }
        )
    }

    val questionTask = state.userQuestionTask
    val question = state.userQuestion
    if (questionTask != null && question != null) {
        var answer by remember(question.id) { mutableStateOf(question.options.firstOrNull().orEmpty()) }
        AlertDialog(
            onDismissRequest = viewModel::dismissUserQuestion,
            title = { Text(stringResource(R.string.studio_needs_input)) },
            text = {
                LazyColumn(Modifier.heightIn(max = 520.dp)) {
                    item {
                        Text(questionTask.goal, style = MaterialTheme.typography.labelMedium)
                        Text(question.question, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 10.dp))
                    }
                    if (question.options.isNotEmpty()) {
                        item {
                            Column(Modifier.fillMaxWidth().padding(top = 10.dp)) {
                                question.options.forEach { option ->
                                    FilterChip(
                                        selected = answer == option,
                                        onClick = { answer = option },
                                        label = { Text(option) },
                                        modifier = Modifier.padding(vertical = 2.dp)
                                    )
                                }
                            }
                        }
                    }
                    if (question.allowOther) {
                        item {
                            OutlinedTextField(
                                value = answer,
                                onValueChange = { answer = it },
                                modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                                label = { Text(if (question.options.isEmpty()) stringResource(R.string.studio_your_answer) else stringResource(R.string.studio_option_or_custom)) },
                                minLines = 2
                            )
                        }
                    }
                    item {
                        Text(
                            stringResource(R.string.studio_answer_note),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = 10.dp)
                        )
                    }
                }
            },
            confirmButton = {
                TextButton(
                    onClick = { viewModel.answerUserQuestion(answer) },
                    enabled = answer.isNotBlank() && !state.actionInFlight
                ) { Text(stringResource(R.string.studio_answer_resume)) }
            },
            dismissButton = {
                TextButton(onClick = viewModel::dismissUserQuestion, enabled = !state.actionInFlight) { Text(stringResource(R.string.common_later)) }
            }
        )
    }

    if (state.showTaskLogs && state.selectedTaskId != null) {
        AlertDialog(
            onDismissRequest = viewModel::dismissTaskLogs,
            title = { Text(stringResource(R.string.studio_task_log)) },
            text = {
                if (state.selectedTaskLogs.isEmpty()) {
                    Text(stringResource(R.string.studio_no_log), style = MaterialTheme.typography.bodySmall)
                } else {
                    LazyColumn(Modifier.heightIn(max = 520.dp)) {
                        items(state.selectedTaskLogs, key = { it.id }) { log ->
                            Column(Modifier.padding(vertical = 4.dp)) {
                                Text(log.timestampUtc.take(19).replace('T', ' '), style = MaterialTheme.typography.labelSmall)
                                Text(log.message, style = MaterialTheme.typography.bodySmall)
                            }
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = viewModel::dismissTaskLogs) { Text(stringResource(R.string.common_close)) }
            }
        )
    }

    state.taskResult?.let { result ->
        AlertDialog(
            onDismissRequest = viewModel::dismissTaskResult,
            title = { Text(stringResource(R.string.studio_task_result, result.taskId.take(8))) },
            text = {
                LazyColumn(Modifier.heightIn(max = 560.dp)) {
                    item {
                        Text(result.goal, style = MaterialTheme.typography.titleSmall)
                        Text(
                            "${result.role} · ${result.status}" +
                                (result.verificationStatus?.let { stringResource(R.string.studio_verification_suffix, it) } ?: ""),
                            style = MaterialTheme.typography.labelMedium,
                            modifier = Modifier.padding(top = 4.dp)
                        )
                        result.stage?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                    }
                    result.summary?.let { summary -> item { ResultBlock(stringResource(R.string.studio_summary), summary) } }
                    result.stepsTaken?.let { steps -> item { ResultBlock(stringResource(R.string.studio_desktop_actions), stringResource(R.string.studio_steps, steps)) } }
                    result.branch?.let { branch -> item { ResultBlock(stringResource(R.string.studio_branch), branch) } }
                    result.worktree?.let { worktree -> item { ResultBlock(stringResource(R.string.studio_worktree), worktree) } }
                    result.diff?.let { diff -> item { ResultBlock(stringResource(R.string.studio_diff), diff) } }
                    result.rawFallback?.let { raw -> item { ResultBlock(stringResource(R.string.studio_raw), raw) } }
                    if (result.summary == null && result.diff == null && result.rawFallback == null) {
                        item {
                            Text(
                                stringResource(R.string.studio_no_result),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(top = 12.dp)
                            )
                        }
                    }
                }
            },
            confirmButton = {
                val task = state.tasks.firstOrNull { it.id == result.taskId }
                if (task != null) TextButton(onClick = { viewModel.requestContinuation(task) }) { Text(stringResource(R.string.common_continue)) }
            },
            dismissButton = { TextButton(onClick = viewModel::dismissTaskResult) { Text(stringResource(R.string.common_close)) } }
        )
    }
}

@Composable
private fun ResultBlock(title: String, value: String) {
    Column(Modifier.fillMaxWidth().padding(top = 12.dp)) {
        Text(title, style = MaterialTheme.typography.labelLarge)
        Text(value, style = MaterialTheme.typography.bodySmall)
    }
}

@Composable
private fun WorkSection(
    state: PcStudioUiState,
    onCreateProject: () -> Unit,
    onSelectProject: (String?) -> Unit,
    onStartTask: (String, String, String?) -> Unit,
    onCancelTask: (String) -> Unit,
    onShowLogs: (String) -> Unit,
    onShowResult: (AgentTaskDto) -> Unit,
    onContinue: (AgentTaskDto) -> Unit,
    onReviewApproval: (AgentTaskDto) -> Unit,
    onAnswerQuestion: (AgentTaskDto) -> Unit
) {
    var goal by remember { mutableStateOf("") }
    var role by remember { mutableStateOf("coding") }

    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        item {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.studio_continue_work), style = MaterialTheme.typography.titleMedium)
                    Text(
                        stringResource(R.string.studio_work_intro),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                IconButton(onClick = onCreateProject) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.studio_new_project)) }
            }
        }

        item { ProjectScopeSelector(state.projects, state.selectedProjectId, stringResource(R.string.studio_no_project), onSelectProject) }

        item {
            Text(stringResource(R.string.studio_what_do), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 16.dp))
            OutlinedTextField(
                value = goal,
                onValueChange = { goal = it },
                modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
                minLines = 2,
                label = { Text(stringResource(R.string.studio_objective)) }
            )
            LazyRow(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                items(
                    listOf(
                        "coding" to R.string.role_code_pc,
                        "computer-use" to R.string.role_control_pc,
                        "ui-verification" to R.string.role_check_screen,
                        "planner" to R.string.role_plan,
                        "research" to R.string.tools_research,
                        "review" to R.string.role_review
                    )
                ) { option ->
                    FilterChip(selected = role == option.first, onClick = { role = option.first }, label = { Text(stringResource(option.second)) })
                }
            }
            Button(
                onClick = {
                    onStartTask(goal.trim(), role, state.selectedProjectId)
                    goal = ""
                },
                enabled = goal.isNotBlank() && !state.actionInFlight,
                modifier = Modifier.fillMaxWidth().padding(top = 10.dp)
            ) { Text(if (role == "computer-use") stringResource(R.string.studio_run_pc) else stringResource(R.string.studio_start_task)) }
            if (role == "computer-use") {
                Text(
                    stringResource(R.string.studio_work_note),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }
        }

        item { Text(stringResource(R.string.studio_projects), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 22.dp, bottom = 4.dp)) }
        items(state.projects, key = { "project-${it.id}" }) { project ->
            ProjectCard(project, state.tasks, selected = state.selectedProjectId == project.id, onSelect = { onSelectProject(project.id) })
        }

        item { Text(stringResource(R.string.studio_recent_tasks), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 22.dp, bottom = 4.dp)) }
        items(state.tasks.take(30), key = { "task-${it.id}" }) { task ->
            TaskCard(
                task,
                onCancel = { onCancelTask(task.id) },
                onLogs = { onShowLogs(task.id) },
                onResult = { onShowResult(task) },
                onContinue = { onContinue(task) },
                onReviewApproval = { onReviewApproval(task) },
                onAnswerQuestion = { onAnswerQuestion(task) }
            )
        }
    }
}

@Composable
private fun FilesSection(
    state: PcStudioUiState,
    onSelectProject: (String?) -> Unit,
    onSave: (MobileArtifactDto) -> Unit
) {
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        item {
            Text(stringResource(R.string.studio_files_title), style = MaterialTheme.typography.titleMedium)
            Text(
                stringResource(R.string.studio_files_caption),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 8.dp)
            )
            ProjectScopeSelector(state.projects, state.selectedProjectId, stringResource(R.string.studio_all_files), onSelectProject)
        }

        if (state.artifacts.isEmpty()) {
            item {
                Text(
                    if (state.selectedProjectId == null) stringResource(R.string.studio_no_artifacts)
                    else stringResource(R.string.studio_no_artifacts_project),
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 16.dp)
                )
            }
        }

        items(state.artifacts, key = { "artifact-${it.id}-${it.versionNumber}" }) { artifact ->
            val downloading = state.downloadingArtifactId == artifact.id
            Card(Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
                Column(Modifier.padding(12.dp)) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(artifact.title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                        Text("v${artifact.versionNumber}", style = MaterialTheme.typography.labelMedium)
                    }
                    Text(
                        buildString {
                            append(artifact.artifactType.ifBlank { "file" })
                            if (artifact.status.isNotBlank()) append(" · ${artifact.status}")
                            artifact.sizeBytes?.let { append(" · ${formatBytes(it)}") }
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 3.dp)
                    )
                    artifact.fileName?.let { Text(it, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 3.dp)) }
                    if (downloading) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 8.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                        if (artifact.downloadable) {
                            TextButton(onClick = { onSave(artifact) }, enabled = !downloading) {
                                Text(if (downloading) stringResource(R.string.studio_saving) else stringResource(R.string.studio_save_device))
                            }
                        } else {
                            Text(
                                stringResource(R.string.studio_metadata_only),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(top = 8.dp)
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ProjectScopeSelector(
    projects: List<ProjectDto>,
    selectedProjectId: String?,
    noProjectLabel: String,
    onSelectProject: (String?) -> Unit
) {
    Text(stringResource(R.string.common_project), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
    if (projects.isEmpty()) {
        Text(stringResource(R.string.common_no_projects), style = MaterialTheme.typography.bodySmall)
    } else {
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            item {
                FilterChip(
                    selected = selectedProjectId == null,
                    onClick = { onSelectProject(null) },
                    label = { Text(noProjectLabel) }
                )
            }
            items(projects, key = { it.id }) { project ->
                FilterChip(
                    selected = selectedProjectId == project.id,
                    onClick = { onSelectProject(project.id) },
                    label = { Text(project.goal.take(32)) }
                )
            }
        }
    }
}

@Composable
private fun ProjectCard(project: ProjectDto, tasks: List<AgentTaskDto>, selected: Boolean, onSelect: () -> Unit) {
    val projectTasks = tasks.filter { it.id in project.taskIds }
    val active = projectTasks.count { it.status !in setOf("completed", "failed", "cancelled") }
    Card(onClick = onSelect, modifier = Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
        Column(Modifier.padding(12.dp)) {
            Text(project.goal, style = MaterialTheme.typography.titleSmall)
            Text(
                stringResource(R.string.studio_project_counts, projectTasks.size, active) + (if (selected) " · " + stringResource(R.string.studio_selected) else ""),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun TaskCard(
    task: AgentTaskDto,
    onCancel: () -> Unit,
    onLogs: () -> Unit,
    onResult: () -> Unit,
    onContinue: () -> Unit,
    onReviewApproval: () -> Unit,
    onAnswerQuestion: () -> Unit
) {
    val terminal = task.status in setOf("completed", "failed", "cancelled")
    val hasResult = !task.resultJson.isNullOrBlank() || task.verificationStatus != null || task.worktreePath != null
    val approvalPending = PcComputerUseApproval.isPending(task)
    val userQuestionPending = PcStudioViewModel.isUserInputPending(task)
    Card(Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
        Column(Modifier.padding(12.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(task.role ?: "auto", style = MaterialTheme.typography.labelLarge)
                Text(task.status, style = MaterialTheme.typography.labelMedium)
            }
            Text(task.goal, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 4.dp))
            task.stage?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            task.verificationStatus?.let {
                Text(stringResource(R.string.studio_verification, it), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (!terminal && task.progressPercent > 0) {
                LinearProgressIndicator(
                    progress = { task.progressPercent.coerceIn(0, 100) / 100f },
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                )
            }
            if (approvalPending) {
                Text(
                    stringResource(R.string.studio_paused_cu),
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 8.dp)
                )
            }
            if (userQuestionPending) {
                Text(
                    stringResource(R.string.studio_paused_agent),
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 8.dp)
                )
            }
            task.error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                if (approvalPending) TextButton(onClick = onReviewApproval) { Text(stringResource(R.string.common_review_approval)) }
                if (userQuestionPending) TextButton(onClick = onAnswerQuestion) { Text(stringResource(R.string.common_answer)) }
                TextButton(onClick = onLogs) { Text(stringResource(R.string.studio_logs)) }
                if (hasResult || task.status == "verifying") TextButton(onClick = onResult) { Text(stringResource(R.string.studio_result)) }
                if (terminal || task.status == "verifying") TextButton(onClick = onContinue) { Text(stringResource(R.string.common_continue)) }
                if (!terminal && !approvalPending) TextButton(onClick = onCancel) { Text(stringResource(R.string.common_cancel)) }
            }
        }
    }
}

@Composable
private fun WorkflowSection(workflows: List<WorkflowDto>, onRun: (WorkflowDto) -> Unit) {
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        item {
            Text(stringResource(R.string.studio_workflows_title), style = MaterialTheme.typography.titleMedium)
            Text(
                stringResource(R.string.studio_workflows_caption),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 8.dp)
            )
        }
        if (workflows.isEmpty()) item { Text(stringResource(R.string.studio_no_workflows)) }
        items(workflows, key = { it.id }) { workflow ->
            Card(Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
                Column(Modifier.padding(12.dp)) {
                    Text("${workflow.name} · v${workflow.versionNumber}", style = MaterialTheme.typography.titleSmall)
                    workflow.description?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                    Text(stringResource(R.string.studio_steps_count, workflow.steps.size), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                        TextButton(onClick = { onRun(workflow) }) { Text(stringResource(R.string.common_run)) }
                    }
                }
            }
        }
    }
}

@Composable
private fun ActivitySection(timeline: List<TimelineEntry>) {
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        item {
            Text(stringResource(R.string.studio_activity_title), style = MaterialTheme.typography.titleMedium)
            Text(
                stringResource(R.string.studio_activity_caption),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 8.dp)
            )
        }
        if (timeline.isEmpty()) item { Text(stringResource(R.string.studio_no_activity)) }
        items(timeline, key = { "${it.category}-${it.relatedId}-${it.timestamp}" }) { entry ->
            Card(Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
                Column(Modifier.padding(10.dp)) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(entry.category.replace('_', ' '), style = MaterialTheme.typography.labelMedium)
                        Text(entry.timestamp.take(19).replace('T', ' '), style = MaterialTheme.typography.labelSmall)
                    }
                    Text(entry.title, style = MaterialTheme.typography.bodyMedium)
                    entry.detail?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
        }
    }
}

private fun suggestedArtifactFileName(artifact: MobileArtifactDto): String =
    artifact.fileName?.takeIf { it.isNotBlank() }
        ?: artifact.title.takeIf { it.isNotBlank() }
        ?: "parux-artifact-${artifact.id.take(8)}"

private fun formatBytes(bytes: Long): String {
    if (bytes < 1024) return "$bytes B"
    val kb = bytes / 1024.0
    if (kb < 1024) return "%.1f KB".format(kb)
    val mb = kb / 1024.0
    if (mb < 1024) return "%.1f MB".format(mb)
    return "%.2f GB".format(mb / 1024.0)
}

@Composable
private fun TextInputDialog(
    title: String,
    label: String,
    confirmLabel: String,
    allowBlank: Boolean = false,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit
) {
    var value by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            OutlinedTextField(value = value, onValueChange = { value = it }, label = { Text(label) }, minLines = 2)
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(value.trim()) }, enabled = allowBlank || value.isNotBlank()) { Text(confirmLabel) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } }
    )
}