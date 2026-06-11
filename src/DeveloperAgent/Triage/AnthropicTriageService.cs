using System.Text.Json;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Triage;

/// <summary>
/// <see cref="ITriageService"/> backed by a single Anthropic chat completion. Makes one lightweight,
/// tool-free classification call (low reasoning effort) that returns a strict JSON verdict.
/// </summary>
/// <remarks>
/// <para>
/// The prompt is deliberately <b>asymmetric</b>: the model is told to reject ONLY when an item is
/// clearly out of scope or clearly outside the agent's skill, and to mark anything uncertain as
/// relevant. Combined with the fail-open behaviour below, this means the gate only parks items it is
/// confident about — important because the Backlog column is write-only (a parked item is never
/// re-picked), so a false rejection is an unrecoverable loss of work.
/// </para>
/// <para>
/// Any transport/parse failure resolves to a relevant verdict (fail open) rather than throwing, so a
/// flaky API call can never silently swallow a legitimate task.
/// </para>
/// </remarks>
public sealed class AnthropicTriageService : ITriageService
{
    private const int MaxTokens = 2_048;

    private const string Instructions =
        "You are a relevance-triage classifier for an autonomous software-development agent. " +
        "Given the project scope, the agent's skill, and a work item, decide whether the agent " +
        "should work the item. Reject ONLY when the item is CLEARLY outside the project's scope " +
        "or CLEARLY outside the agent's skill. When uncertain, or when the item is plausibly " +
        "in-scope and actionable, mark it relevant. Reply with ONLY a JSON object and nothing " +
        "else, in exactly this shape: {\"relevant\": true|false, \"reason\": \"<one concise sentence>\"}.";

    private readonly IAgentChatClientFactory _chatClientFactory;
    private readonly TriageOptions _triage;
    private readonly AgentOptions _agent;
    private readonly ILogger<AnthropicTriageService> _logger;

    public AnthropicTriageService(
        IAgentChatClientFactory chatClientFactory,
        IOptions<TriageOptions> triageOptions,
        IOptions<AgentOptions> agentOptions,
        ILogger<AnthropicTriageService> logger)
    {
        _chatClientFactory = chatClientFactory;
        _triage = triageOptions.Value;
        _agent = agentOptions.Value;
        _logger = logger;
    }

    public async Task<TriageVerdict> EvaluateAsync(string title, string bodyMarkdown, CancellationToken ct)
    {
        var userMessage =
            $"Project scope:\n{_triage.RepoScope}\n\n" +
            $"Agent skill:\n{_triage.AgentSkill}\n\n" +
            $"Work item title: {title}\n\n" +
            $"Work item body:\n{bodyMarkdown}";

        var options = new ChatOptions
        {
            Instructions = Instructions,
            MaxOutputTokens = MaxTokens,
            // Triage is a cheap classification — run it at low reasoning effort. The Anthropic
            // request seam lands this as output_config.effort (see AnthropicRequestOptions).
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
            _logger.LogWarning(ex, "Triage evaluation failed; failing open (treating item as relevant).");
            return new TriageVerdict(true, "Triage call failed; proceeding (fail-open).");
        }

        return Parse(text);
    }

    /// <summary>
    /// Parses the model's reply into a verdict. Lenient: tolerates prose or markdown code fences
    /// around the JSON object by extracting the span from the first '{' to the last '}'. Any failure
    /// (empty text, no JSON, malformed JSON, missing field) fails open to a relevant verdict.
    /// </summary>
    private TriageVerdict Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return FailOpen("Triage returned an empty response");

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return FailOpen("Triage response contained no JSON object");

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;

            if (!root.TryGetProperty("relevant", out var relevantProp))
                return FailOpen("Triage response was missing the 'relevant' field");

            var relevant = relevantProp.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(relevantProp.GetString(), out var b) && b,
                _ => true, // unexpected shape → fail open
            };

            var reason = root.TryGetProperty("reason", out var reasonProp)
                         && reasonProp.ValueKind == JsonValueKind.String
                ? reasonProp.GetString() ?? string.Empty
                : string.Empty;

            return new TriageVerdict(relevant, reason);
        }
        catch (JsonException)
        {
            return FailOpen("Triage response was not valid JSON");
        }
    }

    private TriageVerdict FailOpen(string why)
    {
        _logger.LogWarning("{Why}; failing open (treating item as relevant).", why);
        return new TriageVerdict(true, $"{why}; proceeding (fail-open).");
    }
}
