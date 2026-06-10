using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Agent.Runtime;

/// <summary>
/// Agent-neutral helpers for setting Anthropic-specific request fields through the
/// <see cref="Microsoft.Extensions.AI"/> abstraction. Callers (the developer runner, the reviewer
/// engine) stay free of any direct Anthropic-package reference: they assign the returned delegate
/// to <see cref="ChatOptions.RawRepresentationFactory"/>, and the Anthropic <c>AsIChatClient</c>
/// adapter invokes it to seed the outgoing request before overlaying the standard options.
/// </summary>
public static class AnthropicRequestOptions
{
    /// <summary>
    /// Builds a <see cref="ChatOptions.RawRepresentationFactory"/> delegate that sets the model
    /// reasoning effort (<c>output_config.effort</c>) on the Anthropic request, or <see langword="null"/>
    /// when <paramref name="effort"/> is blank (leaving the provider default in place). The value is
    /// trimmed and lower-cased and passed through verbatim (<c>low | medium | high | xhigh | max</c>);
    /// <c>Effort</c> is an extensible API enum, so forward-compatible values are accepted as-is.
    /// </summary>
    public static Func<IChatClient, object?>? EffortFactory(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return null;

        var level = effort.Trim().ToLowerInvariant();
        return _ => new MessageCreateParams
        {
            // Model / Messages / MaxTokens are required members of MessageCreateParams, but the
            // Anthropic AsIChatClient adapter overwrites them from the ChatOptions (the model id it
            // was created with, the converted chat messages, and MaxOutputTokens) before sending.
            // They are inert placeholders here — only OutputConfig is carried through.
            Model = string.Empty,
            Messages = [],
            MaxTokens = 32_000,
            OutputConfig = new OutputConfig { Effort = level },
        };
    }
}
