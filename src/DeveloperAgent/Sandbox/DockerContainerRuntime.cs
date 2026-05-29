using DeveloperAgent.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Sandbox;

/// <summary>
/// Production <see cref="IContainerRuntime"/> that runs each command inside an
/// isolated child container by shelling out to the <c>docker</c>/<c>runc</c> CLI
/// via <see cref="IProcessRunner"/> — mirroring how <see cref="Workspace.GitClient"/>
/// and <see cref="Workspace.CommandSandbox"/> invoke external processes.
/// </summary>
/// <remarks>
/// The constructor is <c>internal</c> because <see cref="IProcessRunner"/> is an
/// internal abstraction — consumers resolve <see cref="IContainerRuntime"/> from DI.
/// </remarks>
public sealed class DockerContainerRuntime : IContainerRuntime
{
    private readonly IProcessRunner _runner;
    private readonly ContainerRuntimeOptions _options;
    private readonly ILogger<DockerContainerRuntime> _logger;

    internal DockerContainerRuntime(
        IProcessRunner runner,
        IOptions<ContainerRuntimeOptions> options,
        ILogger<DockerContainerRuntime> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ContainerRunResult> RunInContainerAsync(ContainerRunRequest request, CancellationToken ct)
    {
        var args = BuildDockerArguments(_options, request);

        _logger.LogInformation(
            "Container: running {Executable} in image {Image} (workspace {Workspace} → {Mount}, cpus={Cpus}, mem={Memory}, net={Network})",
            request.Executable, _options.Image, request.WorkspacePath, request.MountPath,
            _options.Cpus, _options.Memory, _options.NetworkMode);

        var result = await _runner.RunAsync(
            _options.RuntimeExecutable,
            args,
            request.WorkspacePath,
            request.Timeout,
            ct).ConfigureAwait(false);

        return new ContainerRunResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.Elapsed,
            result.TimedOut);
    }

    /// <summary>
    /// Builds the <c>docker run</c> argument list that isolates a single command:
    /// <c>--rm</c> (no leftover containers), <c>--read-only</c> (host root immutable),
    /// <c>-v {workspace}:{mount}:rw</c> (workspace bind-mounted read-write),
    /// <c>--cpus</c>/<c>--memory</c> resource caps, and <c>--network</c> per the egress
    /// policy. Empty cap values omit their flag. The command and its arguments are the
    /// final tokens, after the image, so docker treats them as the container entrypoint.
    /// </summary>
    internal static IReadOnlyList<string> BuildDockerArguments(
        ContainerRuntimeOptions options,
        ContainerRunRequest request)
    {
        var args = new List<string>
        {
            "run",
            "--rm",
            "--read-only",
            "-v", $"{request.WorkspacePath}:{request.MountPath}:rw",
            "-w", request.WorkingDirectoryInContainer,
        };

        if (!string.IsNullOrWhiteSpace(options.Cpus))
        {
            args.Add("--cpus");
            args.Add(options.Cpus);
        }
        if (!string.IsNullOrWhiteSpace(options.Memory))
        {
            args.Add("--memory");
            args.Add(options.Memory);
        }
        if (!string.IsNullOrWhiteSpace(options.NetworkMode))
        {
            args.Add("--network");
            args.Add(options.NetworkMode);
        }

        args.Add(options.Image);
        args.Add(request.Executable);
        args.AddRange(request.Arguments);

        return args;
    }
}
