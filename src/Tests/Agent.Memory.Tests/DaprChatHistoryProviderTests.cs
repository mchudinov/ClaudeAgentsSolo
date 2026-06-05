using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent.Memory.Tests;

/// <summary>
/// Behavioural tests for <see cref="DaprChatHistoryProvider"/>: covers the three
/// acceptance cases from the Step-19 issue (below window, above window, repeatedly
/// above window) plus a couple of supporting invariants (StateKeys, empty load).
/// </summary>
/// <remarks>
/// <para>
/// The provider's <c>StoreChatHistoryAsync</c> and <c>ProvideChatHistoryAsync</c>
/// overrides are <c>protected</c> on the MAF base class, so these tests drive the
/// provider through its public entry points <see cref="ChatHistoryProvider.InvokedAsync"/>
/// and <see cref="ChatHistoryProvider.InvokingAsync"/>. Both apply the three filter
/// delegates passed to the base constructor — we use identity pass-through filters in
/// production so this is a faithful end-to-end exercise.
/// </para>
/// <para>
/// Tests use <see cref="InMemoryChatHistoryStore"/> (no Dapr) and a deterministic
/// <see cref="StubSummarizer"/> so the windowing behaviour is observable without LLM
/// traffic.
/// </para>
/// </remarks>
public sealed class DaprChatHistoryProviderTests
{
    private const string AgentId = "host-1";
    private const string ProjectItemId = "PVTI_step19";

    private sealed class StubSummarizer : ISummarizer
    {
        public List<IReadOnlyList<ChatMessage>> Invocations { get; } = new();

        public ValueTask<string> SummarizeAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            // Snapshot so later list mutations cannot retroactively change what the
            // test sees this call received.
            Invocations.Add(new List<ChatMessage>(messages));
            return ValueTask.FromResult($"SUMMARY[{messages.Count}]");
        }
    }

    private static ChatMessage User(string text) => new(ChatRole.User, text);
    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
    private static ChatMessage System(string text) => new(ChatRole.System, text);

    private static (DaprChatHistoryProvider provider, InMemoryChatHistoryStore store, StubSummarizer summarizer)
        BuildProvider(int maxRecentTurns)
    {
        var store = new InMemoryChatHistoryStore();
        var summarizer = new StubSummarizer();
        var provider = new DaprChatHistoryProvider(store, summarizer, AgentId, ProjectItemId, maxRecentTurns);
        return (provider, store, summarizer);
    }

    /// <summary>
    /// Drives the provider's protected <c>StoreChatHistoryAsync</c> via the public
    /// <see cref="ChatHistoryProvider.InvokedAsync"/> entry point.
    /// </summary>
    /// <remarks>
    /// The <c>InvokingContext</c>/<c>InvokedContext</c> constructors carry the
    /// <c>MAAI001</c> experimental diagnostic in MAF 1.6.2. Suppressed locally because
    /// MAF 1.6.2 is what the production package reference pins to and the
    /// <see cref="DaprChatHistoryProvider"/> production code already overrides the
    /// corresponding protected hook on this same surface.
    /// </remarks>
#pragma warning disable MAAI001
    private static async Task InvokeStoreAsync(
        DaprChatHistoryProvider provider,
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage> responseMessages)
    {
        var agent = Substitute.For<AIAgent>();
        var session = Substitute.For<Microsoft.Agents.AI.AgentSession>();
        var ctx = new ChatHistoryProvider.InvokedContext(
            agent, session, requestMessages, responseMessages);
        await provider.InvokedAsync(ctx, CancellationToken.None);
    }

    private static async Task<IEnumerable<ChatMessage>> InvokeProvideAsync(
        DaprChatHistoryProvider provider,
        IEnumerable<ChatMessage> requestMessages)
    {
        var agent = Substitute.For<AIAgent>();
        var session = Substitute.For<Microsoft.Agents.AI.AgentSession>();
        var ctx = new ChatHistoryProvider.InvokingContext(agent, session, requestMessages);
        return await provider.InvokingAsync(ctx, CancellationToken.None);
    }
#pragma warning restore MAAI001

    // ─── Acceptance case 1: below window — verbatim persistence ──────────────────

    [Fact]
    public async Task StoreChatHistoryAsync_below_window_persists_messages_unchanged()
    {
        var (provider, store, summarizer) = BuildProvider(maxRecentTurns: 10);

        var request = new[] { User("hello") };
        var response = new[] { Assistant("hi there") };

        await InvokeStoreAsync(provider, request, response);

        var persisted = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        persisted.Should().NotBeNull();
        persisted!.Should().HaveCount(2,
            because: "with 2 non-system messages and a 10-turn window, no compaction occurs");
        persisted.Select(m => m.Text).Should().Equal("hello", "hi there");
        persisted.Select(m => m.Role).Should().Equal(ChatRole.User, ChatRole.Assistant);

        summarizer.Invocations.Should().BeEmpty(
            because: "the summarizer must NOT be called when the persisted history is below the window");
    }

    // ─── Acceptance case 2: above window — older replaced by single summary ──────

    [Fact]
    public async Task StoreChatHistoryAsync_above_window_truncates_older_messages_with_summary()
    {
        const int Window = 4;
        var (provider, store, summarizer) = BuildProvider(maxRecentTurns: Window);

        // Six prior user/assistant turns already persisted, then add 2 new messages
        // (1 request, 1 response) -> 8 non-system messages total -> 4 overflow + 4 kept.
        var prior = new List<ChatMessage>
        {
            User("u1"), Assistant("a1"),
            User("u2"), Assistant("a2"),
            User("u3"), Assistant("a3"),
        };
        await store.SaveAsync(AgentId, ProjectItemId, prior, CancellationToken.None);

        var request = new[] { User("u4") };
        var response = new[] { Assistant("a4") };

        await InvokeStoreAsync(provider, request, response);

        var persisted = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        persisted.Should().NotBeNull();
        // Expected layout: 1 summary system message + 4 most-recent windowable turns = 5.
        persisted!.Should().HaveCount(1 + Window);
        persisted[0].Role.Should().Be(ChatRole.System);
        persisted[0].Text.Should().StartWith(DaprChatHistoryProvider.SummaryPrefix);
        persisted.Skip(1).Select(m => m.Text)
            .Should().Equal("u3", "a3", "u4", "a4");
        // No second summary message was appended — there is exactly one.
        persisted.Count(m => m.Role == ChatRole.System
                             && m.Text.StartsWith(DaprChatHistoryProvider.SummaryPrefix))
            .Should().Be(1, because: "summaries REPLACE prior overflow, they don't accumulate");

        summarizer.Invocations.Should().HaveCount(1,
            because: "exactly one summarizer call should happen on the single store call");
        summarizer.Invocations[0].Select(m => m.Text).ToList()
            .Should().Equal(new[] { "u1", "a1", "u2", "a2" });
    }

    // ─── Acceptance case 3: repeated overflow — REPLACE, not APPEND ──────────────

    [Fact]
    public async Task StoreChatHistoryAsync_repeated_overflow_updates_summary_without_appending()
    {
        const int Window = 4;
        var (provider, store, summarizer) = BuildProvider(maxRecentTurns: Window);

        // First overflow store: 6 prior + 2 new = 8 -> 4 overflow + 4 kept + 1 summary.
        var prior = new List<ChatMessage>
        {
            User("u1"), Assistant("a1"),
            User("u2"), Assistant("a2"),
            User("u3"), Assistant("a3"),
        };
        await store.SaveAsync(AgentId, ProjectItemId, prior, CancellationToken.None);
        await InvokeStoreAsync(provider, new[] { User("u4") }, new[] { Assistant("a4") });

        var afterFirst = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);
        afterFirst!.Should().HaveCount(1 + Window, because: "summary + window after first overflow");

        var firstInvocationCount = summarizer.Invocations.Count;
        firstInvocationCount.Should().Be(1);

        // Second store with 2 new messages -> appended to (loaded list minus prior summary)
        // = 4 retained + 2 new = 6 non-system -> 2 overflow + 4 kept -> 1 fresh summary.
        await InvokeStoreAsync(provider, new[] { User("u5") }, new[] { Assistant("a5") });

        var afterSecond = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        // 1 summary + window-of-4.
        afterSecond.Should().NotBeNull();
        afterSecond!.Should().HaveCount(1 + Window,
            because: "REPLACE semantics: a second overflow still yields exactly one summary + window");

        // Persisted count after second overflow MUST be ≤ persisted count after first
        // overflow + |new turns|, i.e. summaries never accumulate.
        afterSecond.Count.Should().BeLessThanOrEqualTo(afterFirst.Count + 2,
            because: "repeated overflow must not grow the persisted list beyond {first + |new turns|}");

        // The kept tail is the four most-recent turns submitted across both rounds.
        var tail = afterSecond.Skip(1).Select(m => m.Text).ToList();
        tail.Should().Equal(new[] { "u4", "a4", "u5", "a5" });
        tail.Should().NotContain(s => s.StartsWith(DaprChatHistoryProvider.SummaryPrefix),
            because: "the kept tail is raw turns only — no summary marker should appear there");

        // Exactly one summary message in the persisted list.
        afterSecond.Count(m => m.Role == ChatRole.System
                               && m.Text.StartsWith(DaprChatHistoryProvider.SummaryPrefix))
            .Should().Be(1);

        // Second summarizer call must have happened, and its input must NOT contain a
        // prior summary — the provider re-summarises raw turns, never summary-of-summary.
        summarizer.Invocations.Should().HaveCount(2);
        var secondInput = summarizer.Invocations[1];
        secondInput.Should().NotContain(
            m => m.Text != null && m.Text.StartsWith(DaprChatHistoryProvider.SummaryPrefix),
            because: "the summarizer's input on round 2 must be raw turns, not a summary-of-summary");
        secondInput.Select(m => m.Text).Should().Equal(new[] { "u3", "a3" },
            because: "round 2 overflows the two oldest raw turns remaining after stripping " +
                     "the prior summary and appending the new round's request+response");
    }

    // ─── Supporting invariants ────────────────────────────────────────────────────

    [Fact]
    public void StateKeys_is_empty_because_storage_lives_in_Dapr_not_in_the_session_state_bag()
    {
        var (provider, _, _) = BuildProvider(maxRecentTurns: 3);

        provider.StateKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_on_empty_store_returns_empty_enumeration()
    {
        var (provider, _, _) = BuildProvider(maxRecentTurns: 3);

        var messages = await InvokeProvideAsync(provider, Array.Empty<ChatMessage>());

        messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_returns_the_persisted_list_verbatim()
    {
        var (provider, store, _) = BuildProvider(maxRecentTurns: 10);
        var persisted = new List<ChatMessage> { User("a"), Assistant("b") };
        await store.SaveAsync(AgentId, ProjectItemId, persisted, CancellationToken.None);

        var loaded = await InvokeProvideAsync(provider, Array.Empty<ChatMessage>());

        loaded.Select(m => m.Text).Should().Equal("a", "b");
    }

    [Fact]
    public async Task StoreChatHistoryAsync_preserves_leading_system_messages_when_compacting()
    {
        const int Window = 2;
        var (provider, store, summarizer) = BuildProvider(maxRecentTurns: Window);

        // Two leading system messages (persona/instructions) + 3 prior non-system turns,
        // then one more request/response pair -> 5 non-system messages -> 3 overflow + 2 kept.
        var prior = new List<ChatMessage>
        {
            System("persona-line-1"),
            System("persona-line-2"),
            User("u1"), Assistant("a1"),
            User("u2"),
        };
        await store.SaveAsync(AgentId, ProjectItemId, prior, CancellationToken.None);

        await InvokeStoreAsync(provider, new[] { Assistant("a2") }, new[] { User("u3") });

        var persisted = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        persisted.Should().NotBeNull();
        // 2 leading system + 1 summary + window-of-2 = 5.
        persisted!.Should().HaveCount(2 + 1 + Window);
        persisted[0].Text.Should().Be("persona-line-1");
        persisted[1].Text.Should().Be("persona-line-2");
        persisted[2].Role.Should().Be(ChatRole.System);
        persisted[2].Text.Should().StartWith(DaprChatHistoryProvider.SummaryPrefix);
        persisted.Skip(3).Select(m => m.Text).Should().Equal("a2", "u3");

        summarizer.Invocations.Should().HaveCount(1);
        summarizer.Invocations[0].Should().NotContain(
            m => m.Role == ChatRole.System,
            because: "leading system messages stay at the head and must not be fed to the summarizer");
    }

    [Fact]
    public async Task Constructor_rejects_non_positive_maxRecentTurns()
    {
        var act = () => new DaprChatHistoryProvider(
            new InMemoryChatHistoryStore(), new StubSummarizer(), AgentId, ProjectItemId, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
