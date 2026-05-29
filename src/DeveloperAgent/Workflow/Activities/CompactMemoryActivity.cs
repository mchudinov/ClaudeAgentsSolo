using System.Globalization;
using System.Text;
using Dapr.Workflow;
using DeveloperAgent.AgentMemory;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Input for <see cref="CompactMemoryActivity"/>. Carries only plain, deterministic data the
/// workflow body already holds at the Done transition — the activity itself stamps the
/// completion timestamp so the workflow stays replay-safe (no <c>DateTimeOffset.UtcNow</c> in
/// the workflow body).
/// </summary>
/// <param name="ProjectItemId">GitHub Project item id; also the <c>task-memory:{projectItemId}</c> key.</param>
/// <param name="Title">Item title — the completed task.</param>
/// <param name="ContentNumber">GitHub issue/PR number of the item, rendered as <c>#{n}</c>.</param>
/// <param name="BranchName">Branch the work landed on.</param>
/// <param name="PullRequestNumber">Merged PR number, or <c>null</c> when none was opened.</param>
/// <param name="ToolCallsUsed">Total agent tool calls consumed across the run.</param>
/// <param name="Decisions">Free-text notable decisions, or <c>null</c>/empty when none captured.</param>
/// <param name="ChangedFiles">Files changed during the run; may be empty.</param>
/// <param name="TestResults">Test-run outcome text, or <c>null</c>/empty when none captured.</param>
/// <param name="UnresolvedRisks">Open risks/follow-ups, or <c>null</c>/empty when none captured.</param>
public sealed record CompactMemoryActivityInput(
    string ProjectItemId,
    string Title,
    int ContentNumber,
    string BranchName,
    int? PullRequestNumber,
    long ToolCallsUsed,
    string? Decisions,
    IReadOnlyList<string> ChangedFiles,
    string? TestResults,
    string? UnresolvedRisks);

/// <summary>
/// Compacts a completed task into a single durable task-memory summary and persists it under the
/// <c>task-memory:{projectItemId}</c> namespace (P2-J). The summary covers the five required
/// sections — completed task, changed files, decisions, test results, unresolved risks — so the
/// Step-20 <see cref="DaprAgentMemoryContextProvider"/> can inject it into a future run on the
/// same item.
/// </summary>
/// <remarks>
/// Runs after <see cref="DoneActivity"/> on the success path. All non-deterministic work (the
/// completion timestamp and the state-store write) happens here, not in the deterministic
/// workflow body — mirrors <see cref="SaveAgentSessionActivity"/>, which also stamps time from an
/// injected <see cref="TimeProvider"/>.
///
/// Idempotency: the summary is written with overwrite semantics
/// (<see cref="IAgentMemoryStore.SaveTaskMemoriesAsync"/> replaces the prior list), so a workflow
/// retry/replay that re-invokes the activity with the same inputs and the same clock produces a
/// byte-identical single entry rather than appending duplicates.
/// </remarks>
public sealed class CompactMemoryActivity : WorkflowActivity<CompactMemoryActivityInput, object?>
{
    /// <summary>First line of every compacted summary; lets the reader recognise the entry.</summary>
    public const string SummaryHeader = "Task-completion summary";

    private const string NonePlaceholder = "(none recorded)";

    private readonly ILogger<CompactMemoryActivity> _logger;
    private readonly IAgentMemoryStore _store;
    private readonly TimeProvider _clock;

    public CompactMemoryActivity(
        ILogger<CompactMemoryActivity> logger,
        IAgentMemoryStore store,
        TimeProvider clock)
    {
        _logger = logger;
        _store = store;
        _clock = clock;
    }

    public override async Task<object?> RunAsync(
        WorkflowActivityContext context, CompactMemoryActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var summary = Compose(input, _clock.GetUtcNow());

        // Overwrite semantics → idempotent across retries/replays for the same inputs+clock.
        await _store.SaveTaskMemoriesAsync(
            input.ProjectItemId, new[] { summary }, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "[{Activity}] Persisted task-completion summary. item={ItemId} pr={Pr}",
            nameof(CompactMemoryActivity), input.ProjectItemId, input.PullRequestNumber);

        return null;
    }

    /// <summary>
    /// Builds the structured, deterministic summary string. Pure (timestamp is passed in) so it is
    /// trivially testable and produces identical output for identical inputs.
    /// </summary>
    internal static string Compose(CompactMemoryActivityInput input, DateTimeOffset completedAtUtc)
    {
        var sb = new StringBuilder();

        var completedAt = completedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        sb.Append(SummaryHeader).Append(" (completed ").Append(completedAt).Append(')');

        var pr = input.PullRequestNumber is { } prNumber
            ? $", PR #{prNumber}"
            : ", no PR";
        sb.Append('\n').Append("Completed task: #").Append(input.ContentNumber)
            .Append(' ').Append(input.Title)
            .Append(" (branch ").Append(input.BranchName).Append(pr)
            .Append(", ").Append(input.ToolCallsUsed).Append(" tool calls).");

        AppendList(sb, "Changed files", input.ChangedFiles);
        AppendText(sb, "Decisions", input.Decisions);
        AppendText(sb, "Test results", input.TestResults);
        AppendText(sb, "Unresolved risks", input.UnresolvedRisks);

        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string>? items)
    {
        sb.Append('\n').Append(label).Append(": ");
        var nonEmpty = items?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (nonEmpty is null || nonEmpty.Count == 0)
        {
            sb.Append(NonePlaceholder).Append('.');
            return;
        }

        sb.Append(string.Join(", ", nonEmpty)).Append('.');
    }

    private static void AppendText(StringBuilder sb, string label, string? value)
    {
        sb.Append('\n').Append(label).Append(": ");
        sb.Append(string.IsNullOrWhiteSpace(value) ? NonePlaceholder + "." : value!.Trim());
    }
}
