using DeveloperAgent.Configuration;
using DeveloperAgent.Triage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Triage;

/// <summary>
/// Unit tests for <see cref="AnthropicTriageService"/> using a scripted fake <see cref="IChatClient"/>.
/// No real Anthropic API calls are made.
/// </summary>
public sealed class AnthropicTriageServiceTests
{
    private sealed class StubChatClientFactory : IAgentChatClientFactory
    {
        private readonly IChatClient _client;
        public StubChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string modelId) => _client;
    }

    private static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

    private static IChatClient ClientReturning(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Text(text)));
        return client;
    }

    private static AnthropicTriageService Build(
        IChatClient client,
        string repoScope = "A C#/.NET app.",
        string agentSkill = "C# developer.") =>
        new(
            new StubChatClientFactory(client),
            Options.Create(new TriageOptions { Enabled = true, RepoScope = repoScope, AgentSkill = agentSkill }),
            Options.Create(new AgentOptions()),
            NullLogger<AnthropicTriageService>.Instance);

    // ── Verdict parsing ──────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_relevant_when_model_says_relevant()
    {
        var service = Build(ClientReturning("{\"relevant\": true, \"reason\": \"In scope.\"}"));

        var verdict = await service.EvaluateAsync("Add feature", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeTrue();
        verdict.Reason.Should().Be("In scope.");
    }

    [Fact]
    public async Task Returns_not_relevant_when_model_says_not_relevant()
    {
        var service = Build(ClientReturning("{\"relevant\": false, \"reason\": \"Different repo.\"}"));

        var verdict = await service.EvaluateAsync("Translate the website", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeFalse();
        verdict.Reason.Should().Be("Different repo.");
    }

    [Fact]
    public async Task Parses_JSON_wrapped_in_markdown_code_fences()
    {
        // The most common real-world deviation: the model fences its JSON.
        var fenced = "```json\n{\"relevant\": false, \"reason\": \"Out of scope.\"}\n```";
        var service = Build(ClientReturning(fenced));

        var verdict = await service.EvaluateAsync("Some item", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeFalse();
        verdict.Reason.Should().Be("Out of scope.");
    }

    [Fact]
    public async Task Parses_JSON_surrounded_by_prose()
    {
        var noisy = "Sure! Here is my verdict:\n{\"relevant\": true, \"reason\": \"Looks fine.\"}\nHope that helps.";
        var service = Build(ClientReturning(noisy));

        var verdict = await service.EvaluateAsync("Some item", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeTrue();
        verdict.Reason.Should().Be("Looks fine.");
    }

    // ── Fail-open behaviour (Backlog is write-only → never block on uncertainty) ──

    [Fact]
    public async Task Fails_open_when_response_is_not_parseable_JSON()
    {
        var service = Build(ClientReturning("I think this is probably fine, yes."));

        var verdict = await service.EvaluateAsync("Some item", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeTrue(
            because: "an unparseable triage response must never silently park a legitimate item");
    }

    [Fact]
    public async Task Fails_open_when_response_is_empty()
    {
        var service = Build(ClientReturning(""));

        var verdict = await service.EvaluateAsync("Some item", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_open_when_chat_client_throws()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("503"));
        var service = Build(client);

        var verdict = await service.EvaluateAsync("Some item", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeTrue(
            because: "a transport failure must fail open, not throw or reject");
    }

    [Fact]
    public async Task Fails_open_when_relevant_field_is_missing()
    {
        var service = Build(ClientReturning("{\"reason\": \"no verdict field\"}"));

        var verdict = await service.EvaluateAsync("Some item", "Body", CancellationToken.None);

        verdict.IsRelevant.Should().BeTrue();
    }

    // ── Prompt content ───────────────────────────────────────────────────────

    [Fact]
    public async Task Prompt_includes_repo_scope_agent_skill_and_item_text()
    {
        IEnumerable<ChatMessage>? observed = null;
        ChatOptions? observedOptions = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observed = ci.Arg<IEnumerable<ChatMessage>>();
                observedOptions = ci.Arg<ChatOptions>();
                return Task.FromResult(Text("{\"relevant\": true, \"reason\": \"ok\"}"));
            });

        var service = Build(client, repoScope: "WIDGET-SCOPE-MARKER", agentSkill: "SKILL-MARKER");

        await service.EvaluateAsync("TITLE-MARKER", "BODY-MARKER", CancellationToken.None);

        observed.Should().NotBeNull();
        var prompt = string.Join("\n", observed!.Select(m => m.Text));
        prompt.Should().Contain("WIDGET-SCOPE-MARKER");
        prompt.Should().Contain("SKILL-MARKER");
        prompt.Should().Contain("TITLE-MARKER");
        prompt.Should().Contain("BODY-MARKER");

        observedOptions.Should().NotBeNull();
        observedOptions!.RawRepresentationFactory.Should().NotBeNull(
            because: "triage runs at low reasoning effort via output_config.effort");
    }

    [Fact]
    public async Task Uses_the_configured_agent_model()
    {
        string? modelSeen = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Text("{\"relevant\": true, \"reason\": \"ok\"}")));

        var factory = Substitute.For<IAgentChatClientFactory>();
        factory.Create(Arg.Do<string>(m => modelSeen = m)).Returns(client);

        var service = new AnthropicTriageService(
            factory,
            Options.Create(new TriageOptions { Enabled = true, RepoScope = "scope" }),
            Options.Create(new AgentOptions { Model = "claude-opus-4-8" }),
            NullLogger<AnthropicTriageService>.Instance);

        await service.EvaluateAsync("t", "b", CancellationToken.None);

        modelSeen.Should().Be("claude-opus-4-8");
    }
}
