namespace Agent.Runtime;

/// <summary>
/// Supplies the Anthropic API key used to authenticate the chat-client transport, decoupling the
/// runtime layer from how the host resolves secrets. The host registers an implementation that
/// reads the key from its own secret store; the runtime layer never sees the host's secret bundle.
/// Mirrors <c>Agent.GitHub.IGitHubTokenProvider</c>.
/// </summary>
public interface IAnthropicApiKeyProvider
{
    /// <summary>Returns the Anthropic API key for request authentication.</summary>
    string GetApiKey();
}
