using System.Diagnostics;
using System.Text;

namespace DeveloperAgent.Workspace;

/// <summary>
/// Result returned by <see cref="IProcessRunner.RunAsync"/>.
/// </summary>
internal sealed record ProcessRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed,
    bool TimedOut);

/// <summary>
/// Minimal abstraction over <see cref="System.Diagnostics.Process"/> so that
/// <see cref="CommandSandbox"/> can be tested without spawning real child processes.
/// </summary>
internal interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Production implementation that shells out to a real child process via
/// <see cref="System.Diagnostics.Process.Start"/>.
/// stdout and stderr are captured in memory, bounded by <see cref="MaxCaptureBytes"/>.
/// </summary>
internal sealed class DefaultProcessRunner : IProcessRunner
{
    /// <summary>Maximum bytes captured from each output stream before truncation.</summary>
    private const int MaxCaptureBytes = 1 * 1024 * 1024; // 1 MiB

    private const string TruncationMarker = "\n[...output truncated...]\n";

    public async Task<ProcessRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        int stdoutBytes = 0;
        int stderrBytes = 0;

        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdoutTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutTcs.TrySetResult(true);
                return;
            }
            var line = e.Data + "\n";
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (stdoutBytes + lineBytes <= MaxCaptureBytes)
            {
                stdoutBuilder.Append(line);
                stdoutBytes += lineBytes;
            }
            else if (!stdoutBuilder.ToString().EndsWith(TruncationMarker, StringComparison.Ordinal))
            {
                stdoutBuilder.Append(TruncationMarker);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrTcs.TrySetResult(true);
                return;
            }
            var line = e.Data + "\n";
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (stderrBytes + lineBytes <= MaxCaptureBytes)
            {
                stderrBuilder.Append(line);
                stderrBytes += lineBytes;
            }
            else if (!stderrBuilder.ToString().EndsWith(TruncationMarker, StringComparison.Ordinal))
            {
                stderrBuilder.Append(TruncationMarker);
            }
        };

        process.Exited += (_, _) => exitTcs.TrySetResult(true);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        bool timedOut = false;
        try
        {
            await Task.WhenAll(stdoutTcs.Task, stderrTcs.Task, exitTcs.Task)
                      .WaitAsync(cts.Token)
                      .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not user cancellation)
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            // Drain remaining output
            await Task.WhenAll(stdoutTcs.Task, stderrTcs.Task).ConfigureAwait(false);
        }

        sw.Stop();

        return new ProcessRunResult(
            timedOut ? -1 : process.ExitCode,
            stdoutBuilder.ToString(),
            stderrBuilder.ToString(),
            sw.Elapsed,
            timedOut);
    }
}
