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
    public static Func<IChatClient, object?>? EffortFactory(string? effort, string model)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return null;

        var level = effort.Trim().ToLowerInvariant();
        return _ => new MessageCreateParams
        {
            // Model MUST be the real model id: when a RawRepresentationFactory supplies the request
            // seed, the Anthropic AsIChatClient adapter does NOT backfill Model from the chat client's
            // default model — an empty Model here reaches the API verbatim and is rejected with
            // "model: String should have at least 1 character", crashing the run.
            Model = model,
            // Messages / MaxTokens ARE overwritten by the adapter from the converted chat messages and
            // ChatOptions.MaxOutputTokens, so they are inert placeholders. Only Model and OutputConfig
            // are carried through to the wire.
            Messages = [],
            MaxTokens = 32_000,
            OutputConfig = new OutputConfig { Effort = level },
        };
    }
}
