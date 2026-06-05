using System.Diagnostics.Metrics;
using DeveloperAgent.Observability;

namespace DeveloperAgent.Tests.Observability;

/// <summary>
/// Unit tests for <see cref="AgentMetrics"/>.  Use <see cref="MeterListener"/> to capture
/// emissions from the <c>ClaudeAgentsSolo.DeveloperAgent</c> meter and assert each
/// instrument is named, unitted, and recorded as the LLD §P2-N requires.
/// </summary>
public sealed class AgentMetricsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribe a listener to a single named instrument on the supplied AgentMetrics meter,
    /// invoke <paramref name="act"/>, and return every measurement recorded for that
    /// instrument together with its instrument unit.
    ///
    /// Filters on the <see cref="Meter"/> <em>instance</em> (not its name) because Meter
    /// names are process-global; xUnit runs other tests in parallel that create their own
    /// <see cref="AgentMetrics"/> (same meter name), so listening by name leaks foreign
    /// emissions into our callbacks.
    /// </summary>
    private static (List<double> values, string? unit) Capture(
        AgentMetrics metrics,
        string instrumentName,
        Action act)
    {
        var values = new List<double>();
        string? unit = null;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter) &&
                    instrument.Name == instrumentName)
                {
                    unit = instrument.Unit;
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Add(value));
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => values.Add(value));
        listener.Start();

        act();

        // ObservableGauge needs an explicit pull
        listener.RecordObservableInstruments();

        return (values, unit);
    }

    // ── tasks.completed ───────────────────────────────────────────────────────

    [Fact]
    public void RecordTaskCompleted_emits_one_to_agent_tasks_completed()
    {
        using var metrics = new AgentMetrics();

        var (values, unit) = Capture(metrics, "agent.tasks.completed",
            () => metrics.RecordTaskCompleted());

        values.Should().ContainSingle().Which.Should().Be(1);
        unit.Should().Be("{task}");
    }

    // ── tool_calls.per_task ──────────────────────────────────────────────────

    [Fact]
    public void RecordToolCallsPerTask_emits_the_value_to_agent_tool_calls_per_task()
    {
        using var metrics = new AgentMetrics();

        var (values, unit) = Capture(metrics, "agent.tool_calls.per_task",
            () => metrics.RecordToolCallsPerTask(17));

        values.Should().ContainSingle().Which.Should().Be(17);
        unit.Should().Be("{call}");
    }

    // ── model_tokens.per_task ────────────────────────────────────────────────

    [Fact]
    public void RecordModelTokensPerTask_emits_the_value_to_agent_model_tokens_per_task()
    {
        using var metrics = new AgentMetrics();

        var (values, unit) = Capture(metrics, "agent.model_tokens.per_task",
            () => metrics.RecordModelTokensPerTask(12_345));

        values.Should().ContainSingle().Which.Should().Be(12_345);
        unit.Should().Be("{token}");
    }

    // ── build_test.pass_rate ─────────────────────────────────────────────────

    [Fact]
    public void RecordTaskOutcome_observable_gauge_reflects_running_success_total()
    {
        using var metrics = new AgentMetrics();

        // Three successes, one failure ⇒ 0.75
        metrics.RecordTaskOutcome(success: true);
        metrics.RecordTaskOutcome(success: true);
        metrics.RecordTaskOutcome(success: true);
        metrics.RecordTaskOutcome(success: false);

        var (values, unit) = Capture(metrics, "agent.build_test.pass_rate",
            act: () => { /* observable: read via RecordObservableInstruments */ });

        values.Should().ContainSingle().Which.Should().Be(0.75);
        unit.Should().Be("{ratio}");
    }

    [Fact]
    public void Pass_rate_gauge_with_zero_samples_reports_zero()
    {
        using var metrics = new AgentMetrics();

        var (values, _) = Capture(metrics, "agent.build_test.pass_rate",
            act: () => { });

        values.Should().ContainSingle().Which.Should().Be(0);
    }

    // ── time_to_pr ───────────────────────────────────────────────────────────

    [Fact]
    public void RecordTimeToPullRequest_emits_seconds_to_agent_time_to_pr()
    {
        using var metrics = new AgentMetrics();

        var elapsed = TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(7);

        var (values, unit) = Capture(metrics, "agent.time_to_pr",
            () => metrics.RecordTimeToPullRequest(elapsed));

        values.Should().ContainSingle().Which.Should().Be(elapsed.TotalSeconds);
        unit.Should().Be("s");
    }

    // ── RecordTaskTerminated dispatch ────────────────────────────────────────
    //
    // RecordTaskTerminated is the single call-site funnel used by TaskExecutor for
    // every terminal branch (Done = success, Failed = !success).  It must:
    //   - Add 1 to agent.tasks.completed only when success is true
    //   - Record toolCalls to agent.tool_calls.per_task always
    //   - Update the pass-rate gauge's running success/total pair

    [Fact]
    public void RecordTaskTerminated_success_increments_tasks_completed_and_records_tool_calls()
    {
        using var metrics = new AgentMetrics();

        var tasksCompleted = new List<double>();
        var toolCalls = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (!ReferenceEquals(instrument.Meter, metrics.Meter)) return;
                if (instrument.Name is "agent.tasks.completed" or "agent.tool_calls.per_task")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            (inst.Name == "agent.tasks.completed" ? tasksCompleted : toolCalls).Add(value);
        });
        listener.Start();

        metrics.RecordTaskTerminated(success: true, toolCalls: 9);

        tasksCompleted.Should().ContainSingle().Which.Should().Be(1);
        toolCalls.Should().ContainSingle().Which.Should().Be(9);
    }

    [Fact]
    public void RecordTaskTerminated_failure_does_not_touch_tasks_completed_but_still_records_tool_calls()
    {
        using var metrics = new AgentMetrics();

        var tasksCompleted = new List<long>();
        var toolCalls = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (!ReferenceEquals(instrument.Meter, metrics.Meter)) return;
                if (instrument.Name is "agent.tasks.completed" or "agent.tool_calls.per_task")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            (inst.Name == "agent.tasks.completed" ? tasksCompleted : toolCalls).Add(value);
        });
        listener.Start();

        metrics.RecordTaskTerminated(success: false, toolCalls: 4);

        tasksCompleted.Should().BeEmpty();
        toolCalls.Should().ContainSingle().Which.Should().Be(4);
    }

    [Fact]
    public void RecordTaskTerminated_updates_pass_rate_gauge()
    {
        using var metrics = new AgentMetrics();

        metrics.RecordTaskTerminated(success: true,  toolCalls: 1);
        metrics.RecordTaskTerminated(success: true,  toolCalls: 1);
        metrics.RecordTaskTerminated(success: false, toolCalls: 1);

        var (values, _) = Capture(metrics, "agent.build_test.pass_rate", () => { });

        // 2/3 ≈ 0.6666…
        values.Should().ContainSingle().Which.Should().BeApproximately(2d / 3d, 1e-9);
    }

    // ── meter name & lifetime ────────────────────────────────────────────────

    [Fact]
    public void Meter_name_matches_LLD_namespace()
    {
        AgentMetrics.MeterName.Should().Be("ClaudeAgentsSolo.DeveloperAgent");
    }
}
