using Agent.Runtime;
using Microsoft.Extensions.AI;

namespace Agent.Runtime.Tests;

public sealed class TurnCountingChatClientTests
{
    private static IChatClient InnerReturning(ChatResponse response)
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
             .Returns(_ => Task.FromResult(response));
        inner.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
             .Returns(_ => EmptyStream());
        return inner;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ChatResponse SomeResponse() =>
        new(new ChatMessage(ChatRole.Assistant, "ok"));

    [Fact]
    public async Task GetResponseAsync_increments_the_shared_counter()
    {
        // RunCounters is a reference type: the decorator and the caller observe the same instance.
        var counters = new RunCounters();
        var sut = new TurnCountingChatClient(InnerReturning(SomeResponse()), counters, hardCap: 10);

        await sut.GetResponseAsync([]);
        await sut.GetResponseAsync([]);

        counters.TurnsUsed.Should().Be(2);
    }

    [Fact]
    public async Task GetResponseAsync_throws_HardCap_on_the_call_that_exceeds_the_cap()
    {
        var counters = new RunCounters();
        var sut = new TurnCountingChatClient(InnerReturning(SomeResponse()), counters, hardCap: 2);

        // Calls 1 and 2 pass (TurnsUsed 1, 2 — neither > 2).
        await sut.GetResponseAsync([]);
        await sut.GetResponseAsync([]);

        // Call 3 takes TurnsUsed to 3 (> 2) and throws.
        var act = async () => await sut.GetResponseAsync([]);

        (await act.Should().ThrowAsync<HardCapReachedException>())
            .Which.Should().Match<HardCapReachedException>(e => e.TurnsUsed == 3 && e.Cap == 2);
        counters.TurnsUsed.Should().Be(3);
    }

    [Fact]
    public void GetStreamingResponseAsync_increments_the_shared_counter()
    {
        var counters = new RunCounters();
        var sut = new TurnCountingChatClient(InnerReturning(SomeResponse()), counters, hardCap: 10);

        _ = sut.GetStreamingResponseAsync([]);

        counters.TurnsUsed.Should().Be(1);
    }

    [Fact]
    public void GetStreamingResponseAsync_throws_HardCap_when_over_cap()
    {
        var counters = new RunCounters { TurnsUsed = 2 };
        var sut = new TurnCountingChatClient(InnerReturning(SomeResponse()), counters, hardCap: 2);

        // The override increments to 3 (> 2) and throws synchronously, before returning the stream.
        var act = () => _ = sut.GetStreamingResponseAsync([]);

        act.Should().Throw<HardCapReachedException>()
            .Which.Cap.Should().Be(2);
    }
}
