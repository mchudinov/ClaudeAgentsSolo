namespace DeveloperAgent.Lifecycle;

/// <summary>Phase of the per-item state machine.</summary>
public enum TaskPhase
{
    /// <summary>Item moved to InProgress; workspace not yet prepared.</summary>
    Acquired,

    /// <summary>Workspace prepared and branch checked out.</summary>
    WorkspaceReady,

    /// <summary>Agent loop is running.</summary>
    AgentRunning,

    /// <summary>Agent created the PR; item moved to InReview.</summary>
    PullRequestOpen,

    /// <summary>Polling PR review status.</summary>
    AwaitingReview,

    /// <summary>PR merged and approved; item moved to Done.</summary>
    Done,

    /// <summary>Terminal failure; item released back to Ready.</summary>
    Failed
}
