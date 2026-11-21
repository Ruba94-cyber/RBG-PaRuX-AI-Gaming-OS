namespace PaRuX.Contracts;

/// <summary>What a headless watcher does after one look at a coding or agent run.</summary>
public enum AgentRunWaitDecision
{
    KeepPolling,
    Finished,
    WaitingForUserAnswer,
    TimedOut
}

/// <summary>
/// The decision behind `parux agent run`. A run that pauses for a user answer is reported at once, with its
/// question id, and exits with a code of its own. Polling it until the deadline and failing as a timeout is
/// what made a headless question look like a 30-minute failure (CF-05).
/// </summary>
public static class AgentRunWaitPolicy
{
    /// <summary>The stage Core writes while a coding/agent task is paused for a durable user question.</summary>
    public const string UserInputStage = "Waiting for user input";

    /// <summary>
    /// True while the task is paused for a user answer. Core reports this as status "waiting" with <see cref="UserInputStage"/>;
    /// the desktop card used to look for a status ("awaiting-user-input") Core never writes, so the approval question was never shown.
    /// The legacy spelling is still accepted.
    /// </summary>
    public static bool IsPausedForUserAnswer(string? status, string? stage) =>
        string.Equals(status, "awaiting-user-input", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(status, AgentTaskStatus.Waiting, StringComparison.OrdinalIgnoreCase)
            && string.Equals(stage, UserInputStage, StringComparison.Ordinal));

    public static AgentRunWaitDecision Decide(string status, bool hasPendingUserQuestion, bool deadlinePassed)
    {
        if (AgentTaskStatus.Terminal.Contains(status)) return AgentRunWaitDecision.Finished;
        if (string.Equals(status, AgentTaskStatus.Waiting, StringComparison.Ordinal) && hasPendingUserQuestion)
            return AgentRunWaitDecision.WaitingForUserAnswer;
        return deadlinePassed ? AgentRunWaitDecision.TimedOut : AgentRunWaitDecision.KeepPolling;
    }
}
