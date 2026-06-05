namespace Agent.Runtime;

/// <summary>
/// A tiny mutable counter shared between an agent run and the <see cref="TurnCountingChatClient"/>
/// decorator wrapping that run's chat client. Deliberately a <em>class</em> (reference type) so the
/// decorator and the run observe the same instance — a value type would be copied and the run would
/// always read zero turns.
/// <para>
/// Agent-neutral by design: it carries only the model-turn count the cap decorator needs, not any
/// host policy (created PR, sandbox violations, tool-call accounting). A host that tracks richer
/// per-run state keeps that in its own type and passes a <see cref="RunCounters"/> to the decorator.
/// </para>
/// </summary>
public sealed class RunCounters
{
    /// <summary>Number of model turns (request/response cycles) consumed so far.</summary>
    public int TurnsUsed { get; set; }
}
