using System.Text.Json;
using Agent.GitHub;
using Microsoft.Extensions.AI;

namespace Agent.Review;

/// <summary>
/// The single tool exposed to the reviewer model during the persona-violation scan. The model
/// calls it exactly once to record its verdict + summary; <see cref="ReviewerAgent"/> reads the
/// recorded value back and owns the actual GitHub posting.
/// </summary>
internal sealed class SubmitReviewTool : AIFunction
{
    // JSON schema: { verdict: "approve" | "request_changes", summary: string }.
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "verdict": {
              "type": "string",
              "enum": ["approve", "request_changes"],
              "description": "approve when the PR is correct, tested, and consistent; request_changes otherwise."
            },
            "summary": {
              "type": "string",
              "description": "Markdown review body. For request_changes, itemize the issues found."
            }
          },
          "required": ["verdict", "summary"]
        }
        """).RootElement;

    public ReviewVerdict? RecordedVerdict { get; private set; }
    public string? RecordedSummary { get; private set; }

    public override string Name => "submit_review";

    public override string Description =>
        "Submit your review verdict for this pull request. Call this exactly once when you have " +
        "finished reviewing. Choose 'approve' or 'request_changes' and provide a markdown summary.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var verdictRaw = GetString(arguments, "verdict");
        var summary = GetString(arguments, "summary") ?? string.Empty;

        RecordedVerdict = string.Equals(verdictRaw, "approve", StringComparison.OrdinalIgnoreCase)
            ? ReviewVerdict.Approve
            : ReviewVerdict.RequestChanges;
        RecordedSummary = summary;

        return ValueTask.FromResult<object?>(new { recorded = true });
    }

    private static string? GetString(AIFunctionArguments arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => value.ToString(),
        };
    }
}
