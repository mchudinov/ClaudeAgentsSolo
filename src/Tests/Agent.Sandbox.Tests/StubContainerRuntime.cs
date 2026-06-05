namespace Agent.Sandbox.Tests;

/// <summary>
/// Lightweight in-memory <see cref="IContainerRuntime"/> for unit tests on hosts that
/// have no real container runtime (Windows dev boxes, CI without Docker). It records the
/// last request and returns a configurable canned result, so tests can simulate a
/// successful run, a timeout, and a resource-cap breach without starting a container.
/// </summary>
internal sealed class StubContainerRuntime : IContainerRuntime
{
    private readonly Func<ContainerRunRequest, ContainerRunResult> _responder;

    /// <summary>The most recent request passed to <see cref="RunInContainerAsync"/>.</summary>
    public ContainerRunRequest? LastRequest { get; private set; }

    /// <summary>Number of times <see cref="RunInContainerAsync"/> has been invoked.</summary>
    public int InvocationCount { get; private set; }

    private StubContainerRuntime(Func<ContainerRunRequest, ContainerRunResult> responder)
        => _responder = responder;

    /// <summary>A stub that returns a successful run (exit 0, given stdout).</summary>
    public static StubContainerRuntime Success(string stdout = "ok") =>
        new(req => new ContainerRunResult(
            ExitCode: 0,
            Stdout: stdout,
            Stderr: "",
            Elapsed: TimeSpan.FromMilliseconds(20),
            TimedOut: false));

    /// <summary>
    /// A stub that simulates a timeout: it honours the request timeout / cancellation
    /// token, then reports <c>TimedOut = true</c> with exit code -1 (mirroring how the
    /// real runtime maps a killed container).
    /// </summary>
    public static StubContainerRuntime Timeout() =>
        new(req => new ContainerRunResult(
            ExitCode: -1,
            Stdout: "",
            Stderr: "container killed: timeout exceeded",
            Elapsed: req.Timeout,
            TimedOut: true));

    /// <summary>
    /// A stub that simulates a resource-cap breach (e.g. OOM kill): a non-zero exit code
    /// with the runtime error captured on stderr.
    /// </summary>
    public static StubContainerRuntime ResourceCapBreach() =>
        new(req => new ContainerRunResult(
            ExitCode: 137,
            Stdout: "",
            Stderr: "container killed: memory limit exceeded (OOMKilled)",
            Elapsed: TimeSpan.FromMilliseconds(50),
            TimedOut: false));

    public Task<ContainerRunResult> RunInContainerAsync(ContainerRunRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastRequest = request;
        InvocationCount++;
        return Task.FromResult(_responder(request));
    }
}
