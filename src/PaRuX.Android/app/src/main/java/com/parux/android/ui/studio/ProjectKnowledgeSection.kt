package com.parux.android.ui.studio

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
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.parux.android.core.network.ProjectDto

@androidx.compose.runtime.Composable
fun ProjectKnowledgeSection(
    state: PcStudioUiState,
    onSelectProject: (String?) -> Unit,
    onRefresh: () -> Unit
) {
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        item {
            Text(stringResource(R.string.pk_title), style = MaterialTheme.typography.titleMedium)
            Text(
                stringResource(R.string.pk_caption),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 8.dp)
            )
            KnowledgeProjectSelector(state.projects, state.selectedProjectId, onSelectProject)
        }

        if (state.selectedProjectId == null) {
            item {
                Text(
                    stringResource(R.string.pk_select),
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 16.dp)
                )
            }
        } else {
            if (state.knowledgeLoading) {
                item { LinearProgressIndicator(Modifier.fillMaxWidth().padding(vertical = 12.dp)) }
            }

            val pack = state.knowledge
            if (pack == null && !state.knowledgeLoading) {
                item {
                    Button(onClick = onRefresh, modifier = Modifier.padding(top = 12.dp)) { Text(stringResource(R.string.pk_load)) }
                }
            } else if (pack != null) {
                item {
                    Card(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
                        Column(Modifier.padding(12.dp)) {
                            Text(pack.goal, style = MaterialTheme.typography.titleSmall)
                            Text(
                                stringResource(R.string.pk_counts, pack.items.size, pack.usedCharacters, pack.maxCharacters)
                                    + if (pack.truncated) stringResource(R.string.pk_truncated) else "",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(top = 4.dp)
                            )
                            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                                androidx.compose.material3.TextButton(onClick = onRefresh) { Text(stringResource(R.string.common_refresh)) }
                            }
                        }
                    }
                }

                if (pack.items.isEmpty()) {
                    item {
                        Text(
                            stringResource(R.string.pk_empty),
                            style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(top = 8.dp)
                        )
                    }
                }

                items(pack.items, key = { it.identity }) { knowledgeItem ->
                    Card(Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
                        Column(Modifier.padding(12.dp)) {
                            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                                Text(knowledgeItem.kind.replaceFirstChar { it.uppercase() }, style = MaterialTheme.typography.labelLarge)
                                knowledgeItem.status?.let { Text(it, style = MaterialTheme.typography.labelMedium) }
                            }
                            Text(knowledgeItem.title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 3.dp))
                            Text(knowledgeItem.content, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 6.dp))
                            Text(
                                knowledgeItem.identity,
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(top = 6.dp)
                            )
                        }
                    }
                }
            }
        }
    }
}

@androidx.compose.runtime.Composable
private fun KnowledgeProjectSelector(
    projects: List<ProjectDto>,
    selectedProjectId: String?,
    onSelectProject: (String?) -> Unit
) {
    Text(stringResource(R.string.common_project), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 8.dp, bottom = 4.dp))
    if (projects.isEmpty()) {
        Text(stringResource(R.string.common_no_projects), style = MaterialTheme.typography.bodySmall)
        return
    }
    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        item {
            FilterChip(
                selected = selectedProjectId == null,
                onClick = { onSelectProject(null) },
                label = { Text(stringResource(R.string.chat_project_none_short)) }
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
