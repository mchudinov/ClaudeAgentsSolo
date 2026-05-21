namespace DeveloperAgent.Agent;

/// <summary>
/// Internal seam between the agent runner and the Anthropic SDK.
/// Unit tests substitute this interface via NSubstitute; production uses <see cref="AnthropicSdkClient"/>.
/// </summary>
internal interface IAnthropicClient
{
    /// <summary>
    /// Sends a single (non-streaming) request to the Anthropic Messages API and returns the response.
    /// Implements 3-try exponential back-off (1 s / 4 s / 16 s) on transient errors (429, 5xx).
    /// On unrecoverable failure after all retries, throws <see cref="AnthropicApiException"/>.
    /// </summary>
    Task<AnthropicResponse> SendAsync(AnthropicRequest request, CancellationToken ct);
}
