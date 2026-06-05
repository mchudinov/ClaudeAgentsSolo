using Agent.Workspace;
using Dapr.Workflow;
using DeveloperAgent.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Creates a Git branch and prepares the per-task workspace.
/// Updates state to <see cref="TaskPhase.WorkspaceReady"/>.
/// Returns <see cref="CreateBranchResult"/> containing the workspace repo path and default branch.
/// </summary>
public sealed class CreateBranchActivity : WorkflowActivity<CreateBranchActivityInput, CreateBranchResult>
{
    private readonly ILogger<CreateBranchActivity> _logger;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly IGitClient _gitClient;
    private readonly ITaskStateStore _taskStateStore;
    private readonly IOptions<GitHubOptions> _githubOptions;

    public CreateBranchActivity(
        ILogger<CreateBranchActivity> logger,
        IWorkspaceManager workspaceManager,
        IGitClient gitClient,
        ITaskStateStore taskStateStore,
        IOptions<GitHubOptions> githubOptions)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _gitClient = gitClient;
        _taskStateStore = taskStateStore;
        _githubOptions = githubOptions;
    }

    public override async Task<CreateBranchResult> RunAsync(
        WorkflowActivityContext context, CreateBranchActivityInput input)
    {
        var ct = CancellationToken.None;

        var ws = await _workspaceManager.PrepareAsync(
            input.ProjectItemId, input.BranchName, _githubOptions.Value.Repository.Url, ct);
        await _gitClient.CheckoutNewBranchAsync(ws, ct);

        var current = _taskStateStore.Current;
        if (current is not null)
        {
            _taskStateStore.Set(current with
            {
                Phase = TaskPhase.WorkspaceReady,
                BranchName = input.BranchName,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        _logger.LogInformation(
            "[{Activity}] item={ItemId} branch={Branch} repoRoot={RepoRoot} defaultBranch={DefaultBranch}",
            nameof(CreateBranchActivity), input.ProjectItemId, input.BranchName, ws.RepoRoot, ws.DefaultBranch);

        return new CreateBranchResult(ws.RepoRoot, ws.DefaultBranch);
    }
}
