namespace Agent.Sandbox;

/// <summary>
/// Configuration for per-<c>shell_run</c> container isolation (LLD §P2-I part 3/3).
/// Bound from the <c>ContainerRuntime</c> section of <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is <see langword="false"/> (the default) <c>shell_run</c>
/// executes directly via the host process runner — the same behaviour as before this
/// feature. Container isolation is opt-in so the agent boots and runs on Windows dev
/// boxes and CI agents that have no container runtime installed.
/// </remarks>
public sealed record ContainerRuntimeOptions
{
    /// <summary>
    /// Master switch. When <see langword="false"/>, <c>shell_run</c> commands run
    /// directly on the host (via <see cref="IProcessRunner"/>) and no
    /// container is started. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Container image used to run each command, e.g.
    /// <c>mcr.microsoft.com/dotnet/sdk:10.0</c>.
    /// </summary>
    public string Image { get; init; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>
    /// Path inside the container where the workspace is bind-mounted read-write.
    /// The working directory passed to <c>shell_run</c> is rewritten relative to
    /// this mount. Defaults to <c>/workspace</c>.
    /// </summary>
    public string MountPath { get; init; } = "/workspace";

    /// <summary>
    /// CPU cap passed to <c>docker run --cpus</c> (number of CPUs, may be fractional,
    /// e.g. <c>"2"</c> or <c>"0.5"</c>). Empty string omits the flag.
    /// </summary>
    public string Cpus { get; init; } = "2";

    /// <summary>
    /// Memory cap passed to <c>docker run --memory</c> (e.g. <c>"2g"</c>, <c>"512m"</c>).
    /// Empty string omits the flag.
    /// </summary>
    public string Memory { get; init; } = "2g";

    /// <summary>
    /// Network mode passed to <c>docker run --network</c>. Defaults to <c>none</c>:
    /// shell commands get no network egress — outbound HTTP for the agent itself goes
    /// through the host-level <see cref="HostAllowlistHandler"/>, not through containers.
    /// </summary>
    public string NetworkMode { get; init; } = "none";

    /// <summary>
    /// Path to the container runtime CLI binary. Defaults to <c>docker</c>; can be set
    /// to a full path or an alternative front-end on the host.
    /// </summary>
    public string RuntimeExecutable { get; init; } = "docker";
}
