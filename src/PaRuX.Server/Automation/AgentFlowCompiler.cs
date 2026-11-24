using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Automation;

/// <summary>
/// Compiles the no-code Agent Flow blocks (trigger, conditions, action) onto the existing automation definition and runs the
/// same validator every automation goes through. Nothing here executes anything: a flow is data, the AutomationEngine fires it,
/// and its action creates a normal orchestrator task (cancellable, logged, subject to Gaming hard-off like every agent task).
/// </summary>
public static class AgentFlowCompiler
{
    public const int MaximumConditions = 5;
    public const int MaximumNameCharacters = 120;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Placeholder = new(@"\{([A-Za-z]+)\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>The goal-template placeholders each trigger can really fill (the engine replaces exactly these).</summary>
    public static IReadOnlyList<string> PlaceholdersFor(string triggerType) => triggerType switch
    {
        AutomationTriggerType.FileCreated => new[] { "fileName", "folderPath" },
        AutomationTriggerType.GitCommit => new[] { "commitSha", "commitMessage", "repositoryPath" },
        AutomationTriggerType.ModelDownloadCompleted => new[] { "downloadFileName", "downloadLocalPath" },
        AutomationTriggerType.ComponentHealthChanged => new[] { "componentId", "componentHealth", "componentPreviousHealth" },
        AutomationTriggerType.TaskCompleted => new[] { "taskId", "taskGoal", "taskStatus", "taskRole" },
        AutomationTriggerType.CustomCondition => new[] { "conditionSignal", "conditionOperator", "conditionExpected", "conditionObserved", "conditionComponentId" },
        _ => Array.Empty<string>()
    };

    /// <summary>Throws <see cref="InvalidDataException"/> with a message a person can act on; returns the validated automation request.</summary>
    public static CreateAutomationRequest Compile(AgentFlowRequest flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        string name = (flow.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new InvalidDataException("Give the flow a name.");
        if (name.Length > MaximumNameCharacters || name.Any(char.IsControl)) throw new InvalidDataException("The flow name is too long or contains control characters.");
        AgentFlowTriggerBlock t = flow.Trigger ?? throw new InvalidDataException("A flow needs a trigger block.");
        if (!AutomationTriggerType.All.Contains(t.Type ?? string.Empty)) throw new InvalidDataException("Unknown trigger block: " + t.Type + ".");
        AgentFlowActionBlock a = flow.Action ?? throw new InvalidDataException("A flow needs an action block.");
        if (a.Type != AutomationActionType.CreateAgentTask) throw new InvalidDataException("Unknown action block: " + a.Type + ".");
        string goal = (a.Goal ?? string.Empty).Trim();
        if (goal.Length == 0) throw new InvalidDataException("The action block needs a goal for the agent.");
        if (goal.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new InvalidDataException("The goal contains control characters.");

        IReadOnlyList<string> allowed = PlaceholdersFor(t.Type!);
        foreach (Match m in Placeholder.Matches(goal))
            if (!allowed.Contains(m.Groups[1].Value, StringComparer.Ordinal))
                throw new InvalidDataException("{" + m.Groups[1].Value + "} cannot be filled by a " + t.Type + " trigger"
                    + (allowed.Count == 0 ? " (it offers no placeholders)." : ". Available: " + string.Join(", ", allowed.Select(p => "{" + p + "}")) + "."));

        List<AutomationGuard> conditions = (flow.Conditions ?? new List<AutomationGuard>()).ToList();
        if (conditions.Count > MaximumConditions) throw new InvalidDataException("A flow can have at most " + MaximumConditions + " condition blocks.");
        foreach (AutomationGuard g in conditions)
            if (g.Signal == AutomationGuardSignal.TriggerFileName && t.Type != AutomationTriggerType.FileCreated)
                throw new InvalidDataException("The file-name condition only works with a file-created trigger.");

        string triggerJson = JsonSerializer.Serialize<object>(t.Type switch
        {
            AutomationTriggerType.Schedule => new ScheduleTriggerConfig { IntervalSeconds = t.IntervalSeconds },
            AutomationTriggerType.FileCreated => new FileCreatedTriggerConfig { FolderPath = t.FolderPath ?? string.Empty, Filter = t.Filter ?? string.Empty },
            AutomationTriggerType.GitCommit => new GitCommitTriggerConfig { RepositoryPath = t.RepositoryPath ?? string.Empty, Branch = t.Branch ?? string.Empty },
            AutomationTriggerType.ModelDownloadCompleted => new ModelDownloadCompletedTriggerConfig { FileNameContains = t.FileNameContains ?? string.Empty },
            AutomationTriggerType.ComponentHealthChanged => new ComponentHealthChangedTriggerConfig { ComponentId = t.ComponentId ?? string.Empty },
            AutomationTriggerType.TaskCompleted => new TaskCompletedTriggerConfig { Role = t.Role ?? string.Empty, Status = t.Status ?? string.Empty },
            _ => new CustomConditionTriggerConfig
            {
                Signal = t.Signal ?? string.Empty, Operator = t.Operator ?? string.Empty, Value = t.Value ?? string.Empty,
                Role = t.Role ?? string.Empty, FileNameContains = t.FileNameContains ?? string.Empty, ComponentId = t.ComponentId ?? string.Empty
            }
        }, Json);

        string actionJson = JsonSerializer.Serialize(new CreateAgentTaskActionConfig
        {
            GoalTemplate = goal, Role = string.IsNullOrWhiteSpace(a.Role) ? null : a.Role.Trim(), Flow = true, Guards = conditions
        }, Json);

        var request = new CreateAutomationRequest
        {
            Name = name, TriggerType = t.Type!, TriggerConfigJson = triggerJson, ActionType = a.Type, ActionConfigJson = actionJson, Enabled = flow.Enabled
        };
        AutomationDefinitionValidator.Validate(request);
        return request;
    }

    /// <summary>Validates condition blocks; used by the shared automation validator so no path can store an unusable guard.</summary>
    public static void ValidateGuards(IReadOnlyList<AutomationGuard>? guards, Action<string?, string> validateRole)
    {
        if (guards == null || guards.Count == 0) return;
        if (guards.Count > MaximumConditions) throw new InvalidDataException("A flow can have at most " + MaximumConditions + " condition blocks.");
        foreach (AutomationGuard g in guards)
        {
            string signal = (g.Signal ?? string.Empty).Trim();
            string op = (g.Operator ?? string.Empty).Trim();
            string value = (g.Value ?? string.Empty).Trim();
            if (!AutomationGuardSignal.IsKnown(signal)) throw new InvalidDataException("Condition signal '" + signal + "' is not supported.");
            if (value.Length == 0 || value.Length > AutomationDefinitionValidator.MaximumConditionTextCharacters || value.Any(char.IsControl))
                throw new InvalidDataException("A condition value must be 1-" + AutomationDefinitionValidator.MaximumConditionTextCharacters + " printable characters.");
            if (signal == AutomationGuardSignal.TriggerFileName)
            {
                if (!AutomationGuardOperator.Text.Contains(op)) throw new InvalidDataException("The file-name condition supports eq, neq, contains and not_contains.");
                continue;
            }
            if (!AutomationConditionOperator.All.Contains(op)) throw new InvalidDataException("Condition operator '" + op + "' is not supported.");
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0)
                throw new InvalidDataException("A count condition needs a non-negative whole number.");
            validateRole(g.Role, "condition role filter");
            if ((g.FileNameContains ?? string.Empty).Length > AutomationDefinitionValidator.MaximumConditionTextCharacters)
                throw new InvalidDataException("A condition file-name filter is too long.");
        }
    }

    /// <summary>Reads a stored flow back into its blocks; null when the automation is not a flow.</summary>
    public static AgentFlowDto? Describe(AutomationDto automation)
    {
        try
        {
            CreateAgentTaskActionConfig? action = JsonSerializer.Deserialize<CreateAgentTaskActionConfig>(automation.ActionConfigJson, Json);
            if (action is not { Flow: true }) return null;
            return new AgentFlowDto { Automation = automation, Conditions = action.Guards ?? new List<AutomationGuard>(), Goal = action.GoalTemplate, Role = action.Role };
        }
        catch (JsonException) { return null; }
    }
}
