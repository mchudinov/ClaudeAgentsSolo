using Microsoft.Extensions.AI;

namespace Agent.Runtime;

/// <summary>
/// <see cref="IChatClient"/> decorator that:
/// <list type="bullet">
///   <item>increments <see cref="RunCounters.TurnsUsed"/> on every call to the inner chat client;</item>
///   <item>throws <see cref="HardCapReachedException"/> when the turn count exceeds the configured hard cap.</item>
/// </list>
/// <para>
/// Sits between Microsoft Agent Framework's <c>FunctionInvokingChatClient</c> and the actual
/// model client — each "tool-use roundtrip" the framework performs is exactly one call to this
/// decorator, so the count maps cleanly onto a runner's notion of "model turn". The counter is
/// passed in (rather than owned) so the runner reads the final count back after the decorator
/// is discarded.
/// </para>
/// </summary>
public sealed class TurnCountingChatClient : DelegatingChatClient
{
    private readonly RunCounters _counters;
    private readonly int _hardCap;

    public TurnCountingChatClient(IChatClient inner, RunCounters counters, int hardCap)
        : base(inner)
    {
        _counters = counters;
        _hardCap = hardCap;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _counters.TurnsUsed++;
        if (_counters.TurnsUsed > _hardCap)
        {
            throw new HardCapReachedException(_counters.TurnsUsed, _hardCap);
        }
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _counters.TurnsUsed++;
        if (_counters.TurnsUsed > _hardCap)
        {
            throw new HardCapReachedException(_counters.TurnsUsed, _hardCap);
        }
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
