using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentRunWaitPolicyPauseTests
{
    [Theory]
    [InlineData("waiting", "Waiting for user input", true)]      // what Core really writes while a question is pending
    [InlineData("awaiting-user-input", null, true)]               // legacy spelling still honoured
    [InlineData("waiting", "Waiting for delegated child abc", false)]
    [InlineData("running", "Waiting for user input", false)]
    [InlineData("completed", "Waiting for user input", false)]
    [InlineData(null, null, false)]
    public void The_card_recognises_a_task_paused_for_a_user_answer(string? status, string? stage, bool expected) =>
        Assert.Equal(expected, AgentRunWaitPolicy.IsPausedForUserAnswer(status, stage));

    [Fact]
    public void Core_and_the_card_agree_on_the_waiting_stage_text() =>
        Assert.Equal(AgentRunWaitPolicy.UserInputStage, PaRuX.Server.Orchestration.AgentUserQuestionService.WaitingStage);
}
