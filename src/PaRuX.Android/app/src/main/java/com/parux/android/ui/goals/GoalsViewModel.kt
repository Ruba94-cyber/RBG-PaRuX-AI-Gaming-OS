package com.parux.android.ui.goals

import com.parux.android.R
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.parux.android.core.AppContainer
import com.parux.android.core.network.CreateGoalModeTaskRequestDto
import com.parux.android.core.network.CreateGoalSpecificationRequestDto
import com.parux.android.core.network.GoalModeTaskResponseDto
import com.parux.android.core.network.GoalSpecificationVersionDto
import com.parux.android.core.network.GoalVerificationRecordDto
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class GoalsUiState(
    val latestSpecification: GoalSpecificationVersionDto? = null,
    val latestTask: GoalModeTaskResponseDto? = null,
    val verificationRecords: List<GoalVerificationRecordDto> = emptyList(),
    val loading: Boolean = false,
    val message: String? = null,
    val error: String? = null
)

class GoalsViewModel(private val container: AppContainer) : ViewModel() {
    private val _state = MutableStateFlow(GoalsUiState())
    val state: StateFlow<GoalsUiState> = _state.asStateFlow()

    fun createAndRun(goal: String, criteriaText: String, role: String) {
        val normalizedGoal = goal.trim()
        val criteria = criteriaText.lineSequence()
            .map { it.trim().removePrefix("-").removePrefix("•").trim() }
            .filter { it.isNotBlank() }
            .distinct()
            .take(24)
            .toList()
        if (normalizedGoal.isBlank()) {
            _state.value = _state.value.copy(error = container.appContext.getString(R.string.goals_err_required))
            return
        }
        if (criteria.isEmpty()) {
            _state.value = _state.value.copy(error = container.appContext.getString(R.string.goals_err_criteria))
            return
        }
        val client = container.client.value ?: return
        viewModelScope.launch {
            _state.value = _state.value.copy(loading = true, error = null, message = null)
            try {
                val specification = client.api.createGoal(
                    CreateGoalSpecificationRequestDto(
                        goal = normalizedGoal,
                        acceptanceCriteria = criteria,
                        changeReason = "created-from-android"
                    )
                )
                val task = client.api.createGoalTask(
                    CreateGoalModeTaskRequestDto(
                        goalId = specification.goalId,
                        goalVersion = specification.version,
                        role = role,
                        maxRepairAttempts = if (role == "coding") 2 else 1
                    )
                )
                _state.value = GoalsUiState(
                    latestSpecification = specification,
                    latestTask = task,
                    loading = false,
                    message = container.appContext.getString(R.string.goals_created, specification.version)
                )
                refreshVerification()
            } catch (e: Exception) {
                _state.value = _state.value.copy(
                    loading = false,
                    error = e.message ?: container.appContext.getString(R.string.goals_err_create)
                )
            }
        }
    }

    fun refreshVerification() {
        val taskId = _state.value.latestTask?.task?.id ?: return
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val detail = client.api.getGoalTaskDetail(taskId)
                val records = client.api.getGoalVerificationRecords(taskId)
                _state.value = _state.value.copy(
                    latestSpecification = detail.specification,
                    verificationRecords = records,
                    error = null
                )
            } catch (e: Exception) {
                _state.value = _state.value.copy(error = e.message ?: container.appContext.getString(R.string.goals_err_refresh))
            }
        }
    }

    fun dismissMessage() { _state.value = _state.value.copy(message = null) }
    fun dismissError() { _state.value = _state.value.copy(error = null) }
}
