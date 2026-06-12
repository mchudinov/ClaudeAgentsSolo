using System.Text.Json;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Resolution;

/// <summary>
/// <see cref="IResolutionChecker"/> backed by a single Anthropic chat completion over a deterministic
/// working-tree snapshot. Makes one lightweight, tool-free classification call (low reasoning effort)
/// that returns a strict JSON verdict.
/// </summary>
/// <remarks>
/// <para>
/// The prompt is deliberately <b>asymmetric and conservative</b>: the model is told to answer
/// <c>alreadyResolved=true</c> ONLY when it is highly confident the work is already present and
/// complete, and to answer <c>false</c> on any uncertainty or partial evidence. Combined with the
/// fail-open behaviour below, the gate only parks items it is confident about — important because a
/// false "already done" abandons real work to the write-only Backlog column and is unrecoverable,
/// whereas a false negative merely re-implements something that existed.
/// </para>
/// <para>
/// Every snapshot/transport/parse failure resolves to a <b>not-resolved</b> verdict (fail open →
/// proceed to implement), the opposite direction from relevance triage but the same safety principle:
/// a flaky call can never silently abandon legitimate work.
/// </para>
/// <para>
/// This is a swappable seam body. Its <em>judgment quality</em> depends on prompt tuning against live
/// runs and cannot be validated by unit tests (no model in the loop), so the gate ships
/// <see cref="ResolutionCheckOptions.Enabled"/> = <see langword="false"/> by default.
/// </para>
/// </remarks>
public sealed class AnthropicResolutionChecker : IResolutionChecker
{
    private const int MaxTokens = 2_048;

    private const string Instructions =
        "You are an 'already-implemented' classifier for an autonomous software-development agent. " +
        "Given a work item, its existing comments, and a listing of the current repository's files, " +
        "decide whether the work the item describes is ALREADY fully implemented in this repository. " +
        "Answer alreadyResolved=true ONLY when you are highly confident the work is already present and " +
        "complete (e.g. the described class/file/feature clearly already exists in the listing and the " +
        "comments do not indicate remaining work). When uncertain, when the evidence is only partial, " +
        "or when you cannot tell from the listing, answer alreadyResolved=false so the agent implements " +
        "it — a wrong 'already done' abandons real work and is unrecoverable. Reply with ONLY a JSON " +
        "object and nothing else, in exactly this shape: " +
        "{\"alreadyResolved\": true|false, \"reason\": \"<one concise sentence>\"}.";

    private readonly IAgentChatClientFactory _chatClientFactory;
    private readonly IWorkingTreeSnapshot _snapshot;
    private readonly AgentOptions _agent;
    private readonly ILogger<AnthropicResolutionChecker> _logger;

    public AnthropicResolutionChecker(
        IAgentChatClientFactory chatClientFactory,
        IWorkingTreeSnapshot snapshot,
        IOptions<AgentOptions> agentOptions,
        ILogger<AnthropicResolutionChecker> logger)
    {
        _chatClientFactory = chatClientFactory;
        _snapshot = snapshot;
        _agent = agentOptions.Value;
        _logger = logger;
    }

    public async Task<ResolutionVerdict> EvaluateAsync(
        string title, string bodyMarkdown, string comments, string workspacePath, CancellationToken ct)
    {
        string snapshot;
        try
        {
            snapshot = await _snapshot.CaptureAsync(workspacePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Working-tree snapshot failed; failing open (treating item as not resolved).");
            return new ResolutionVerdict(false, "Working-tree snapshot failed; proceeding (fail-open).");
        }

        var userMessage =
            $"Work item title: {title}\n\n" +
            $"Work item body:\n{bodyMarkdown}\n\n" +
            $"Existing comments on the item:\n{(string.IsNullOrWhiteSpace(comments) ? "(none)" : comments)}\n\n" +
            $"Repository file listing:\n{(string.IsNullOrWhiteSpace(snapshot) ? "(empty or unavailable)" : snapshot)}";

        var options = new ChatOptions
        {
            Instructions = Instructions,
            MaxOutputTokens = MaxTokens,
            // Cheap classification — run at low reasoning effort, like relevance triage.
            RawRepresentationFactory = AnthropicRequestOptions.EffortFactory("low", _agent.Model),
        };

        string? text;
        try
        {
            var client = _chatClientFactory.Create(_agent.Model);
            var response = await client
                .GetResponseAsync([new ChatMessage(ChatRole.User, userMessage)], options, ct)
                .ConfigureAwait(false);
            text = response.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resolution evaluation failed; failing open (treating item as not resolved).");
            return new ResolutionVerdict(false, "Resolution call failed; proceeding (fail-open).");
        }

        return Parse(text);
    }

    /// <summary>
    /// Parses the model's reply into a verdict. Lenient: tolerates prose or markdown fences by extracting
    /// the span from the first '{' to the last '}'. Any failure (empty, no JSON, malformed, missing
    /// field, unexpected shape) fails open to a <b>not-resolved</b> verdict (proceed to implement).
    /// </summary>
    internal ResolutionVerdict Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return FailOpen("Resolution returned an empty response");

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return FailOpen("Resolution response contained no JSON object");

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;

            if (!root.TryGetProperty("alreadyResolved", out var prop))
                return FailOpen("Resolution response was missing the 'alreadyResolved' field");

            var resolved = prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(prop.GetString(), out var b) && b,
                _ => false, // unexpected shape → fail open (NOT resolved → proceed)
            };

            var reason = root.TryGetProperty("reason", out var rp) && rp.ValueKind == JsonValueKind.String
                ? rp.GetString() ?? string.Empty
                : string.Empty;

            return new ResolutionVerdict(resolved, reason);
        }
        catch (JsonException)
        {
            return FailOpen("Resolution response was not valid JSON");
        }
    }

    private ResolutionVerdict FailOpen(string why)
    {
        _logger.LogWarning("{Why}; failing open (treating item as not resolved).", why);
        return new ResolutionVerdict(false, $"{why}; proceeding (fail-open).");
    }
}
