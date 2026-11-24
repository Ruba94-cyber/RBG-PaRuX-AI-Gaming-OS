using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Automation;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Agent Flows: validated blocks compiled onto the automation engine, condition blocks, run log, manual run and cancellable runs.</summary>
public sealed class AgentFlowTests : IDisposable
{
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), "parux-flow-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchDir = Path.Combine(Path.GetTempPath(), "parux-flow-watch-" + Guid.NewGuid().ToString("N"));

    public AgentFlowTests() => Directory.CreateDirectory(_watchDir);

    public void Dispose()
    {
        try { Directory.Delete(_dbDir, true); } catch { }
        try { Directory.Delete(_watchDir, true); } catch { }
    }

    private (AutomationStore Store, TaskStore Tasks, AutomationEngine Engine) Build()
    {
        var store = new AutomationStore(_dbDir);
        var tasks = new TaskStore(_dbDir);
        return (store, tasks, new AutomationEngine(store, tasks, new ModelDownloadService(),
            new ComponentInventoryService(new RuntimeManagerService(new LocalAiProcessManager()))));
    }

    private AgentFlowRequest FileFlow(string goal = "Summarise {fileName}", params AutomationGuard[] conditions) => new()
    {
        Name = "Summarise reports", Enabled = true,
        Trigger = new AgentFlowTriggerBlock { Type = AutomationTriggerType.FileCreated, FolderPath = _watchDir, Filter = "*" },
        Conditions = conditions.ToList(),
        Action = new AgentFlowActionBlock { Goal = goal }
    };

    [Fact]
    public void A_valid_flow_compiles_to_a_validated_automation_and_reads_back_as_blocks()
    {
        var (store, _, _) = Build();
        AutomationDto created = store.Create(AgentFlowCompiler.Compile(FileFlow(conditions: new AutomationGuard
        { Signal = AutomationGuardSignal.TriggerFileName, Operator = AutomationGuardOperator.Contains, Value = ".csv" })));
        AgentFlowDto? flow = AgentFlowCompiler.Describe(created);
        Assert.NotNull(flow);
        Assert.Equal("Summarise {fileName}", flow!.Goal);
        Assert.Single(flow.Conditions);
        Assert.Single(store.List().Select(AgentFlowCompiler.Describe).Where(f => f != null));
    }

    [Fact]
    public void A_flow_is_created_switched_off_unless_the_person_enables_it()
    {
        Assert.False(new AgentFlowRequest().Enabled);
        CreateAutomationRequest request = AgentFlowCompiler.Compile(new AgentFlowRequest
        {
            Name = "Hourly", Trigger = new AgentFlowTriggerBlock { Type = AutomationTriggerType.Schedule, IntervalSeconds = 600 },
            Action = new AgentFlowActionBlock { Goal = "Check the downloads folder" }
        });
        Assert.False(request.Enabled);
    }

    [Theory]
    [InlineData("", "ok goal")]                       // no name
    [InlineData("name", "")]                          // no goal
    [InlineData("name", "Open {nonsense}")]           // placeholder the trigger cannot fill
    [InlineData("name", "Use {commitSha}")]           // placeholder of another trigger
    public void Invalid_blocks_are_refused_with_a_reason(string name, string goal)
    {
        AgentFlowRequest flow = FileFlow(goal);
        flow.Name = name;
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(flow));
    }

    [Fact]
    public void Unknown_blocks_bad_conditions_and_unusable_trigger_settings_are_refused()
    {
        AgentFlowRequest badTrigger = FileFlow();
        badTrigger.Trigger.Type = "run_shell";
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(badTrigger));

        AgentFlowRequest badAction = FileFlow();
        badAction.Action.Type = "delete_files";
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(badAction));

        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(FileFlow(conditions: new AutomationGuard { Signal = "powershell", Operator = "eq", Value = "1" })));
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(FileFlow(conditions: new AutomationGuard
        { Signal = AutomationConditionSignal.FailedTaskCount, Operator = "matches_regex", Value = "1" })));
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(FileFlow(conditions: new AutomationGuard
        { Signal = AutomationConditionSignal.FailedTaskCount, Operator = "gte", Value = "-3" })));
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(FileFlow(conditions: Enumerable.Range(0, 6).Select(_ => new AutomationGuard()).ToArray())));

        AgentFlowRequest fileCondOnSchedule = new()
        {
            Name = "x", Trigger = new AgentFlowTriggerBlock { Type = AutomationTriggerType.Schedule },
            Conditions = { new AutomationGuard { Signal = AutomationGuardSignal.TriggerFileName, Operator = "contains", Value = "a" } },
            Action = new AgentFlowActionBlock { Goal = "g" }
        };
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(fileCondOnSchedule));

        AgentFlowRequest flagFolder = FileFlow();
        flagFolder.Trigger.FolderPath = "--exec";
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(flagFolder)); // the shared validator still applies
        AgentFlowRequest badRole = FileFlow();
        badRole.Action.Role = "not-a-role";
        Assert.Throws<InvalidDataException>(() => AgentFlowCompiler.Compile(badRole));
    }

    [Fact]
    public async Task A_met_condition_fires_the_flow_and_an_unmet_one_is_logged_as_skipped_without_a_task()
    {
        var (store, tasks, engine) = Build();
        AutomationDto flow = store.Create(AgentFlowCompiler.Compile(FileFlow(conditions: new AutomationGuard
        { Signal = AutomationGuardSignal.TriggerFileName, Operator = AutomationGuardOperator.Contains, Value = ".csv" })));
        await engine.RunTickAsync(CancellationToken.None); // seeds

        File.WriteAllText(Path.Combine(_watchDir, "notes.txt"), "x");
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Empty(tasks.List());
        AgentFlowRunDto skipped = store.ListRuns(flow.Id).Single();
        Assert.Equal("skipped", skipped.Outcome);
        Assert.Null(skipped.TaskId);
        Assert.Contains("notes.txt", skipped.Detail);

        File.WriteAllText(Path.Combine(_watchDir, "sales.csv"), "x");
        await engine.RunTickAsync(CancellationToken.None);
        AgentTaskDto task = Assert.Single(tasks.List());
        Assert.Equal("Summarise sales.csv", task.Goal);
        Assert.Equal(flow.Id, task.CreatedByAutomationId);
        AgentFlowRunDto fired = store.ListRuns(flow.Id).First();
        Assert.Equal("fired", fired.Outcome);
        Assert.Equal(task.Id, fired.TaskId);
    }

    [Fact]
    public async Task A_count_condition_reads_core_state()
    {
        var (store, tasks, engine) = Build();
        AutomationDto flow = store.Create(AgentFlowCompiler.Compile(new AgentFlowRequest
        {
            Name = "Retry on failures", Enabled = true,
            Trigger = new AgentFlowTriggerBlock { Type = AutomationTriggerType.Schedule, IntervalSeconds = 0 },
            Conditions = { new AutomationGuard { Signal = AutomationConditionSignal.FailedTaskCount, Operator = AutomationConditionOperator.GreaterThanOrEqual, Value = "1" } },
            Action = new AgentFlowActionBlock { Goal = "Look at the failed tasks" }
        }));

        Assert.Equal("skipped", engine.RunNow(flow.Id)!.Outcome);
        Assert.Empty(tasks.List());

        AgentTaskDto failing = tasks.Create(new CreateAgentTaskRequest { Goal = "something that failed" });
        tasks.UpdateStatus(failing.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed, Stage = "Failed" });
        AgentFlowRunDto run = engine.RunNow(flow.Id)!;
        Assert.Equal("fired", run.Outcome);
        Assert.Contains("manual run", run.Detail);
        Assert.Equal(2, tasks.List().Count);
    }

    [Fact]
    public void A_run_started_by_a_flow_can_be_cancelled_through_the_task_and_the_log_says_which_task()
    {
        var (store, tasks, engine) = Build();
        AutomationDto flow = store.Create(AgentFlowCompiler.Compile(new AgentFlowRequest
        {
            Name = "Manual only", Trigger = new AgentFlowTriggerBlock { Type = AutomationTriggerType.Schedule, IntervalSeconds = 86400 },
            Action = new AgentFlowActionBlock { Goal = "Do the thing" }
        }));
        AgentFlowRunDto run = engine.RunNow(flow.Id)!;
        Assert.Equal("fired", run.Outcome);
        Assert.True(tasks.UpdateStatus(run.TaskId!, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled, Stage = "Cancelled" }));
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(run.TaskId!)!.Status);
    }

    [Fact]
    public void While_automations_are_paused_a_manual_run_creates_nothing_and_says_so()
    {
        var (store, tasks, engine) = Build();
        AutomationDto flow = store.Create(AgentFlowCompiler.Compile(new AgentFlowRequest
        {
            Name = "Paused", Trigger = new AgentFlowTriggerBlock { Type = AutomationTriggerType.Schedule },
            Action = new AgentFlowActionBlock { Goal = "g" }
        }));
        engine.IsPaused = true;
        AgentFlowRunDto run = engine.RunNow(flow.Id)!;
        Assert.Equal("failed", run.Outcome);
        Assert.Contains("paused", run.Detail);
        Assert.Empty(tasks.List());
        Assert.Null(engine.RunNow("missing"));
    }

    [Fact]
    public void The_run_log_is_bounded_and_deleted_with_the_flow()
    {
        var (store, _, _) = Build();
        AutomationDto flow = store.Create(AgentFlowCompiler.Compile(new AgentFlowRequest
        {
            Name = "Log", Trigger = new AgentFlowTriggerBlock { Type = AutomationTriggerType.Schedule }, Action = new AgentFlowActionBlock { Goal = "g" }
        }));
        for (int i = 0; i < AutomationStore.MaximumRunsPerAutomation + 15; i++) store.RecordRun(flow.Id, "fired: created task t" + i, "t" + i);
        List<AgentFlowRunDto> runs = store.ListRuns(flow.Id, 1000);
        Assert.Equal(AutomationStore.MaximumRunsPerAutomation, runs.Count);
        Assert.Equal("t" + (AutomationStore.MaximumRunsPerAutomation + 14), runs[0].TaskId); // newest first
        Assert.True(store.Delete(flow.Id));
        Assert.Empty(store.ListRuns(flow.Id));
    }

    [Fact]
    public void A_plain_automation_is_not_listed_as_a_flow_and_guards_are_validated_for_every_creation_path()
    {
        var (store, _, _) = Build();
        AutomationDto plain = store.Create(new CreateAutomationRequest
        {
            Name = "plain", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{\"intervalSeconds\":60}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{\"goalTemplate\":\"g\"}"
        });
        Assert.Null(AgentFlowCompiler.Describe(plain));
        Assert.Throws<InvalidDataException>(() => store.Create(new CreateAutomationRequest
        {
            Name = "sneaky", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{\"intervalSeconds\":60}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"g\",\"guards\":[{\"signal\":\"powershell\",\"operator\":\"eq\",\"value\":\"1\"}]}"
        }));
    }
}
