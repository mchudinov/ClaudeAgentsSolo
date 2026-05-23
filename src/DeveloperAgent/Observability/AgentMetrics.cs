using System.Diagnostics.Metrics;

namespace DeveloperAgent.Observability;

/// <summary>
/// Agent-specific instruments emitted via <see cref="System.Diagnostics.Metrics.Meter"/>
/// per <c>docs/plans/07-phase-2-outline.md §P2-N</c>.
///
/// Instruments
/// <list type="bullet">
///   <item><c>agent.tasks.completed</c> — <see cref="Counter{Int64}"/> (unit <c>{task}</c>).
///   The rate-per-hour shape is derived downstream by the collector; this class
///   does not window itself.</item>
///   <item><c>agent.tool_calls.per_task</c> — <see cref="Histogram{Int64}"/> (unit <c>{call}</c>).</item>
///   <item><c>agent.model_tokens.per_task</c> — <see cref="Histogram{Int64}"/> (unit <c>{token}</c>).</item>
///   <item><c>agent.build_test.pass_rate</c> — <see cref="ObservableGauge{Double}"/> (unit <c>{ratio}</c>)
///   over a running success/total pair maintained by <see cref="RecordTaskOutcome"/>.</item>
///   <item><c>agent.time_to_pr</c> — <see cref="Histogram{Double}"/> (unit <c>s</c>).</item>
/// </list>
///
/// Registered as a singleton in <c>Program.cs</c>; the meter name is added to the OTel
/// metrics pipeline via <c>WithMetrics(m =&gt; m.AddMeter(AgentMetrics.MeterName))</c> so
/// instruments flow through whatever exporter <c>ServiceDefaults</c> wires up
/// (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is already honoured there).
/// </summary>
public sealed class AgentMetrics : IDisposable
{
    /// <summary>Meter name shared with the OTel pipeline registration.</summary>
    public const string MeterName = "ClaudeAgentsSolo.DeveloperAgent";

    private readonly Meter _meter;

    /// <summary>
    /// The underlying <see cref="Meter"/> instance.  Exposed for test isolation:
    /// because <see cref="Meter"/> names are process-global, tests must filter
    /// <see cref="MeterListener"/> callbacks on <em>this instance</em> rather than on
    /// the shared <see cref="MeterName"/> string to avoid cross-test pollution.
    /// </summary>
    internal Meter Meter => _meter;

    private readonly Counter<long> _tasksCompleted;
    private readonly Histogram<long> _toolCallsPerTask;
    private readonly Histogram<long> _modelTokensPerTask;
    private readonly Histogram<double> _timeToPr;

    // Running totals for the build/test pass-rate gauge.  Updated only through
    // RecordTaskOutcome so the gauge has exactly one source.
    private long _successCount;
    private long _totalCount;

    public AgentMetrics()
    {
        _meter = new Meter(MeterName);

        _tasksCompleted = _meter.CreateCounter<long>(
            name: "agent.tasks.completed",
            unit: "{task}",
            description: "Tasks the agent has driven through to Done.");

        _toolCallsPerTask = _meter.CreateHistogram<long>(
            name: "agent.tool_calls.per_task",
            unit: "{call}",
            description: "Total tool invocations recorded for a single task.");

        _modelTokensPerTask = _meter.CreateHistogram<long>(
            name: "agent.model_tokens.per_task",
            unit: "{token}",
            description: "Total model tokens (input + output) consumed by a single task.");

        _timeToPr = _meter.CreateHistogram<double>(
            name: "agent.time_to_pr",
            unit: "s",
            description: "Seconds from task acquisition to the agent opening a pull request.");

        _meter.CreateObservableGauge(
            name: "agent.build_test.pass_rate",
            observeValue: () =>
            {
                var total = Interlocked.Read(ref _totalCount);
                if (total == 0)
                {
                    return 0d;
                }
                var success = Interlocked.Read(ref _successCount);
                return (double)success / total;
            },
            unit: "{ratio}",
            description: "Fraction of completed tasks whose build+tests passed (success / total).");
    }

    /// <summary>Records one completed task on the <c>agent.tasks.completed</c> counter.</summary>
    public void RecordTaskCompleted() => _tasksCompleted.Add(1);

    /// <summary>Records the total tool-call count for a single task.</summary>
    public void RecordToolCallsPerTask(long toolCalls) => _toolCallsPerTask.Record(toolCalls);

    /// <summary>Records the total model token count for a single task.</summary>
    public void RecordModelTokensPerTask(long tokens) => _modelTokensPerTask.Record(tokens);

    /// <summary>Records the elapsed time from task acquisition to PR open, in seconds.</summary>
    public void RecordTimeToPullRequest(TimeSpan elapsed) => _timeToPr.Record(elapsed.TotalSeconds);

    /// <summary>
    /// Records a task outcome (success or failure) for the <c>agent.build_test.pass_rate</c>
    /// observable gauge.  Updates the running pair atomically; the gauge callback divides
    /// success by total on every collection.
    /// </summary>
    public void RecordTaskOutcome(bool success)
    {
        Interlocked.Increment(ref _totalCount);
        if (success)
        {
            Interlocked.Increment(ref _successCount);
        }
    }

    /// <summary>
    /// Single fan-out for terminal task transitions.  Called once per task from each
    /// terminal branch in <c>TaskExecutor</c>:
    /// <list type="bullet">
    ///   <item>Increments <c>agent.tasks.completed</c> when <paramref name="success"/> is true.</item>
    ///   <item>Records <paramref name="toolCalls"/> to <c>agent.tool_calls.per_task</c> always.</item>
    ///   <item>Updates the <c>agent.build_test.pass_rate</c> running pair via <see cref="RecordTaskOutcome"/>.</item>
    /// </list>
    /// Concentrating these recordings in one method keeps "one call site per instrument"
    /// while still letting <c>TaskExecutor</c> emit from multiple branches.
    /// </summary>
    public void RecordTaskTerminated(bool success, long toolCalls)
    {
        if (success)
        {
            _tasksCompleted.Add(1);
        }
        _toolCallsPerTask.Record(toolCalls);
        RecordTaskOutcome(success);
    }

    public void Dispose() => _meter.Dispose();
}
