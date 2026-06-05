namespace Agent.Tools;

/// <summary>
/// A per-run budget for local tool invocations. <see cref="MafToolAdapter"/> consults it before
/// and after each call so the host can cap how many tools a single run may invoke.
/// </summary>
/// <remarks>
/// The two phases preserve the exact gate/increment ordering the run loop relies on: the adapter
/// calls <see cref="ThrowIfExhausted"/> <em>before</em> invoking a tool (so the call past the cap
/// is blocked before it runs) and <see cref="Record"/> only <em>after</em> the tool returns
/// successfully (a throwing tool does not consume budget). The host decides what "exhausted" means
/// and which exception <see cref="ThrowIfExhausted"/> throws.
/// </remarks>
public interface IToolCallBudget
{
    /// <summary>Throws if the budget is exhausted. Called before each tool invocation.</summary>
    void ThrowIfExhausted();

    /// <summary>Records one successful tool invocation against the budget.</summary>
    void Record();
}
