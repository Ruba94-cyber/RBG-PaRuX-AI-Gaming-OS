using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentOrchestratorDelegationSlotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-orchestrator-delegation-slot-" + Guid.NewGuid().ToString("N"));

    public AgentOrchestratorDelegationSlotTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Delegation_waiting_parent_releases_slot_for_live_child_without_resuming_parent()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Review implementation",
            Role = "review",
            GrantedCapabilities = new List<string> { "context.read", "repository.read", "agent.delegate" }
        });
        store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Reviewing implementation"
        });
        DelegateAgentTaskResponse delegated = store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Inspect tests",
            Role = "testing",
            GrantedCapabilities = new List<string> { "context.read", "repository.read" }
        });
        store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = "Waiting for delegated child " + delegated.ChildTaskId
        });

        AgentTaskDto unrelatedRunning = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Research independent evidence",
            Role = "research"
        });
        store.UpdateStatus(unrelatedRunning.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Researching"
        });

        ModelExecutionProfile profile = global::PaRuX.Server.ExecutionProfileStore.BuildDefault(
            "scheduler-test", ExecutionQualityProfile.Fast);
        Assert.Equal(2, profile.Budget.MaxParallelTasks);
        var orchestrator = new AgentOrchestrator(store, () => profile);

        List<string> dispatched = orchestrator.RunSchedulingPass();

        Assert.Contains(delegated.ChildTaskId, dispatched);
        Assert.Equal(AgentTaskStatus.Running, store.Get(delegated.ChildTaskId)!.Status);
        Assert.Equal(AgentTaskStatus.Waiting, store.Get(parent.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Running, store.Get(unrelatedRunning.Id)!.Status);
    }

    [Fact]
    public void A_task_paused_for_a_user_answer_does_not_hold_a_parallel_slot()
    {
        var store = new TaskStore(_root);
        var questions = new AgentUserQuestionService(store);
        var forgotten = new List<string>();
        ModelExecutionProfile profile = global::PaRuX.Server.ExecutionProfileStore.BuildDefault("scheduler-test", ExecutionQualityProfile.Fast);
        int slots = Math.Clamp(profile.Budget.MaxParallelTasks, 1, 8);
        for (int i = 0; i < slots; i++)
        {
            AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Edit file " + i, Role = "coding" });
            store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "Working" });
            questions.Ask(task.Id, new AskAgentUserQuestionRequest { Question = "Approve?", Options = new List<string> { "Yes", "No" }, AllowOther = false });
            forgotten.Add(task.Id);
        }
        AgentTaskDto fresh = store.Create(new CreateAgentTaskRequest { Goal = "Fix the discount bug", Role = "coding" });

        var orchestrator = new AgentOrchestrator(store, () => profile);
        List<string> dispatched = orchestrator.RunSchedulingPass();

        Assert.Contains(fresh.Id, dispatched);
        Assert.All(forgotten, id => Assert.Equal(AgentTaskStatus.Waiting, store.Get(id)!.Status));   // still waiting for their owner
    }

    [Fact]
    public void Ordinary_waiting_task_still_consumes_parallel_slot()
    {
        var store = new TaskStore(_root);
        AgentTaskDto waiting = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Wait for user approval",
            Role = "review"
        });
        store.UpdateStatus(waiting.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = "Waiting for user approval"
        });

        AgentTaskDto running = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Research evidence",
            Role = "research"
        });
        store.UpdateStatus(running.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Researching"
        });
        AgentTaskDto queued = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Inspect regression tests",
            Role = "testing"
        });

        ModelExecutionProfile profile = global::PaRuX.Server.ExecutionProfileStore.BuildDefault(
            "scheduler-test", ExecutionQualityProfile.Fast);
        var orchestrator = new AgentOrchestrator(store, () => profile);

        Assert.Empty(orchestrator.RunSchedulingPass());
        Assert.Equal(AgentTaskStatus.Queued, store.Get(queued.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Waiting, store.Get(waiting.Id)!.Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
