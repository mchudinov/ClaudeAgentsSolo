namespace DeveloperAgent.Agent;

/// <summary>
/// Thrown by <see cref="AnthropicSdkClient"/> when the Anthropic API returns an unrecoverable
/// error after all retry attempts are exhausted.
/// </summary>
public sealed class AnthropicApiException : Exception
{
    /// <summary>Initialises the exception with a message and optional inner exception.</summary>
    public AnthropicApiException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
