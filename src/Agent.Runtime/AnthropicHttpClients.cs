namespace Agent.Runtime;

/// <summary>
/// Name of the <see cref="System.Net.Http.IHttpClientFactory"/>-managed client the Anthropic
/// chat-client transport uses. <see cref="AgentRuntimeServiceCollectionExtensions.AddAgentRuntimeServices"/>
/// registers it; the host composes its egress filter and resilience pipeline onto it via the
/// <c>configureHttpClient</c> callback.
/// </summary>
/// <remarks>
/// The string value <c>"anthropic"</c> is a contract shared with the host's egress allowlist and
/// any Dapr resiliency policy keyed by client name (e.g. <c>dapr-components/resiliency.yaml</c>).
/// Changing it would silently bypass the host's <c>HostAllowlistHandler</c> and resilience tuning,
/// so the value is pinned by a test.
/// </remarks>
public static class AnthropicHttpClients
{
    /// <summary>HttpClient used by the Anthropic .NET SDK.</summary>
    public const string ChatClient = "anthropic";
}
