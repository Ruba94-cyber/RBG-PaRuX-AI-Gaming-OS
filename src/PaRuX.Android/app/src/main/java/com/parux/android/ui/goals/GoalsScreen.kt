package com.parux.android.ui.goals

import androidx.compose.ui.res.stringResource
import com.parux.android.R
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GoalsScreen(viewModel: GoalsViewModel) {
    val state by viewModel.state.collectAsState()
    var goal by remember { mutableStateOf("") }
    var criteria by remember { mutableStateOf("") }
    var role by remember { mutableStateOf("coding") }

    Scaffold(topBar = { TopAppBar(title = { Text(stringResource(R.string.goals_title)) }) }) { padding ->
        LazyColumn(Modifier.padding(padding).fillMaxSize().padding(horizontal = 16.dp)) {
            if (state.loading) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }

            item {
                Text(
                    stringResource(R.string.goals_define_done),
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.padding(top = 14.dp)
                )
                Text(
                    stringResource(R.string.goals_caption),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp, bottom = 12.dp)
                )
                OutlinedTextField(
                    value = goal,
                    onValueChange = { goal = it },
                    label = { Text(stringResource(R.string.common_goal)) },
                    minLines = 2,
                    modifier = Modifier.fillMaxWidth()
                )
                OutlinedTextField(
                    value = criteria,
                    onValueChange = { criteria = it },
                    label = { Text(stringResource(R.string.goals_criteria)) },
                    minLines = 4,
                    modifier = Modifier.fillMaxWidth().padding(top = 10.dp)
                )
                Text(stringResource(R.string.goals_worker), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 12.dp))
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 6.dp)) {
                    items(
                        listOf(
                            "coding" to R.string.role_code,
                            "planner" to R.string.role_plan,
                            "research" to R.string.tools_research,
                            "review" to R.string.role_review,
                            "computer-use" to R.string.role_pc
                        )
                    ) { option ->
                        FilterChip(
                            selected = role == option.first,
                            onClick = { role = option.first },
                            label = { Text(stringResource(option.second)) }
                        )
                    }
                }
                Button(
                    onClick = {
                        viewModel.createAndRun(goal, criteria, role)
                        goal = ""
                        criteria = ""
                    },
                    enabled = !state.loading && goal.isNotBlank() && criteria.isNotBlank(),
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
                ) { Text(stringResource(R.string.goals_create)) }
            }

            state.message?.let { message ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 12.dp)) {
                        Row(Modifier.fillMaxWidth().padding(12.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                            Text(message, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                            TextButton(onClick = viewModel::dismissMessage) { Text("OK") }
                        }
                    }
                }
            }
            state.error?.let { error ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 12.dp)) {
                        Row(Modifier.fillMaxWidth().padding(12.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                            Text(error, color = MaterialTheme.colorScheme.error, modifier = Modifier.weight(1f))
                            TextButton(onClick = viewModel::dismissError) { Text(stringResource(R.string.common_dismiss)) }
                        }
                    }
                }
            }

            state.latestSpecification?.let { specification ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 16.dp)) {
                        Column(Modifier.padding(14.dp)) {
                            Text(stringResource(R.string.goals_latest, specification.version), style = MaterialTheme.typography.titleSmall)
                            Text(specification.goal, modifier = Modifier.padding(top = 4.dp))
                            Text(
                                "sha256 ${specification.contentSha256.take(12)}…",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            specification.acceptanceCriteria.sortedBy { it.order }.forEach { criterion ->
                                Text(
                                    "✓ ${criterion.text}",
                                    style = MaterialTheme.typography.bodySmall,
                                    modifier = Modifier.padding(top = 4.dp)
                                )
                            }
                        }
                    }
                }
            }

            state.latestTask?.let { response ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 10.dp)) {
                        Column(Modifier.padding(14.dp)) {
                            Text(stringResource(R.string.goals_windows_task), style = MaterialTheme.typography.titleSmall)
                            Text(
                                "${response.task.role ?: "auto"} · ${response.task.status}" +
                                    (response.task.stage?.let { " · $it" } ?: ""),
                                style = MaterialTheme.typography.bodySmall,
                                modifier = Modifier.padding(top = 4.dp)
                            )
                            Text(stringResource(R.string.goals_task_id, response.task.id.take(10)), style = MaterialTheme.typography.labelSmall)
                            TextButton(onClick = viewModel::refreshVerification) { Text(stringResource(R.string.goals_refresh_verification)) }
                        }
                    }
                }
            }

            if (state.verificationRecords.isNotEmpty()) {
                item { Text(stringResource(R.string.goals_verification), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 18.dp)) }
                items(state.verificationRecords, key = { it.id }) { record ->
                    Card(Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
                        Column(Modifier.padding(12.dp)) {
                            Text(
                                stringResource(R.string.goals_pass, record.verificationPass, stringResource(if (record.allRequiredSatisfied) R.string.goals_accepted else R.string.goals_not_accepted)),
                                color = if (record.allRequiredSatisfied) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error,
                                style = MaterialTheme.typography.labelLarge
                            )
                            record.criteria.forEach { criterion ->
                                Text(
                                    "${if (criterion.satisfied) "✓" else "✗"} ${criterion.evidence}",
                                    style = MaterialTheme.typography.bodySmall,
                                    modifier = Modifier.padding(top = 4.dp)
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
