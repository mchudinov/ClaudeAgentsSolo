using DeveloperAgent.Dashboard;

namespace DeveloperAgent.Tests.Dashboard;

/// <summary>Spy that records which operator commands the dashboard dispatched.</summary>
internal sealed class SpyOperatorCommandService : IOperatorCommandService
{
    private readonly string? _projectItemId;
    public List<OperatorCommand> Received { get; } = [];

    public SpyOperatorCommandService(string? projectItemId = "PVTI_1") => _projectItemId = projectItemId;

    public OperatorCommandResult? LastCommand { get; private set; }

    public Task<OperatorCommandResult> PauseAsync(CancellationToken ct) => Record(OperatorCommand.Pause);

    public Task<OperatorCommandResult> ResumeAsync(CancellationToken ct) => Record(OperatorCommand.Resume);

    public Task<OperatorCommandResult> CancelAsync(CancellationToken ct) => Record(OperatorCommand.Cancel);

    private Task<OperatorCommandResult> Record(OperatorCommand command)
    {
        Received.Add(command);
        LastCommand = new OperatorCommandResult(
            command,
            Accepted: _projectItemId is not null,
            _projectItemId,
            DateTimeOffset.UtcNow);
        return Task.FromResult(LastCommand);
    }
}
