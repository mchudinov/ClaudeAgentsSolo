namespace DeveloperAgent.Sandbox;

/// <summary>
/// Request to execute a single command inside an isolated child container.
/// </summary>
/// <param name="Executable">The program token, e.g. <c>dotnet</c> (argv[0]).</param>
/// <param name="Arguments">Arguments passed to <paramref name="Executable"/> (argv[1..]).</param>
/// <param name="WorkspacePath">
/// Absolute host path bind-mounted read-write into the container at
/// <paramref name="MountPath"/>. The rest of the host filesystem is read-only
/// (the container itself runs with <c>--read-only</c>).
/// </param>
/// <param name="MountPath">Container-side mount target for <paramref name="WorkspacePath"/>.</param>
/// <param name="WorkingDirectoryInContainer">
/// Container-relative working directory the command runs in. Must be under
/// <paramref name="MountPath"/>.
/// </param>
/// <param name="Timeout">Maximum wall-clock time before the container is killed.</param>
public sealed record ContainerRunRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkspacePath,
    string MountPath,
    string WorkingDirectoryInContainer,
    TimeSpan Timeout);

/// <summary>
/// Result of a <see cref="IContainerRuntime.RunInContainerAsync"/> invocation.
/// Mirrors <see cref="Workspace.CommandResult"/> so the sandbox can map it 1:1.
/// </summary>
/// <param name="ExitCode">Process exit code; <c>-1</c> when the run timed out.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error (includes runtime errors such as OOM kills).</param>
/// <param name="Elapsed">Wall-clock duration of the run.</param>
/// <param name="TimedOut"><see langword="true"/> when the run exceeded <see cref="ContainerRunRequest.Timeout"/>.</param>
public sealed record ContainerRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed,
    bool TimedOut);

/// <summary>
/// Runs a single command inside an isolated child container so that <c>shell_run</c>
/// commands cannot read or mutate anything outside the bind-mounted workspace.
/// </summary>
/// <remarks>
/// The production implementation shells out to the <c>docker</c>/<c>runc</c> CLI via
/// <see cref="Workspace.IProcessRunner"/>; the host runtime must provide runc or
/// Firecracker isolation on Linux. A lightweight stub is used for unit tests and on
/// development hosts where no container runtime is available.
/// </remarks>
public interface IContainerRuntime
{
    /// <summary>Executes <paramref name="request"/> in an isolated container.</summary>
    Task<ContainerRunResult> RunInContainerAsync(ContainerRunRequest request, CancellationToken ct);
}
