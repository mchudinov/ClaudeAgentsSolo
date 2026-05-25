using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent;

/// <summary>
/// Thrown by <see cref="TurnCountingChatClient"/> when the model is asked to take
/// more turns than <see cref="Configuration.AgentOptions.MaxModelTurnsHardCap"/>.
/// The runner catches this and returns <see cref="AgentRunOutcome.HardCapReached"/>.
/// </summary>
internal sealed class HardCapReachedException : Exception
{
    public HardCapReachedException(int turns, int cap)
        : base($"Hard cap of {cap} model turns reached (turns used: {turns}).")
    {
        TurnsUsed = turns;
        Cap = cap;
    }

    public int TurnsUsed { get; }
    public int Cap { get; }
}

/// <summary>
/// <see cref="IChatClient"/> decorator that:
/// <list type="bullet">
///   <item>increments <see cref="AgentSession.TurnsUsed"/> on every call to the inner chat client;</item>
///   <item>throws <see cref="HardCapReachedException"/> when the turn count exceeds the configured hard cap.</item>
/// </list>
/// <para>
/// Sits between Microsoft Agent Framework's <c>FunctionInvokingChatClient</c> and the actual
/// model client — each "tool-use roundtrip" the framework performs is exactly one call to this
/// decorator, so the count maps cleanly onto the runner's notion of "model turn".
/// </para>
/// </summary>
internal sealed class TurnCountingChatClient : DelegatingChatClient
{
    private readonly AgentSession _session;
    private readonly int _hardCap;

    public TurnCountingChatClient(IChatClient inner, AgentSession session, int hardCap)
        : base(inner)
    {
        _session = session;
        _hardCap = hardCap;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _session.TurnsUsed++;
        if (_session.TurnsUsed > _hardCap)
        {
            throw new HardCapReachedException(_session.TurnsUsed, _hardCap);
        }
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _session.TurnsUsed++;
        if (_session.TurnsUsed > _hardCap)
        {
            throw new HardCapReachedException(_session.TurnsUsed, _hardCap);
        }
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
