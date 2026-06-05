namespace Agent.Tools;

/// <summary>
/// The slice of per-run context the generic file tools need: the absolute path of the workspace
/// root they may read and write within.
/// </summary>
/// <remarks>
/// The host implements this on its own richer per-run context type (which also carries policy
/// fields the generic tools never touch — the issue being implemented, the run's counters, the
/// created pull request). Host-specific tools that need those fields downcast the
/// <see cref="IToolContext"/> they receive to that concrete type; the generic file tools depend
/// only on <see cref="WorkspaceRoot"/>.
/// </remarks>
public interface IToolContext
{
    /// <summary>Absolute path to the workspace root. All tool file paths resolve within this boundary.</summary>
    string WorkspaceRoot { get; }
}
