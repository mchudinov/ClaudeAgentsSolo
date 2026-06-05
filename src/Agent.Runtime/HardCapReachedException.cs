namespace Agent.Runtime;

/// <summary>
/// Thrown by <see cref="TurnCountingChatClient"/> when the model is asked to take more turns than
/// the configured hard cap. The host's run loop catches this (it may be wrapped in an
/// <see cref="AggregateException"/> by Microsoft Agent Framework) and ends the run.
/// </summary>
public sealed class HardCapReachedException : Exception
{
    public HardCapReachedException(int turns, int cap)
        : base($"Hard cap of {cap} model turns reached (turns used: {turns}).")
    {
        TurnsUsed = turns;
        Cap = cap;
    }

    /// <summary>The turn count that tripped the cap.</summary>
    public int TurnsUsed { get; }

    /// <summary>The configured maximum number of model turns.</summary>
    public int Cap { get; }
}
