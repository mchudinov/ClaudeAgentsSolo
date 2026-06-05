using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using Agent.Mcp;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Unit tests for <see cref="AnthropicAgentRunner"/> using a scripted fake <see cref="IChatClient"/>.
/// No real Anthropic API calls are made.
/// </summary>
public sealed class AnthropicAgentRunnerTests
{
    // ── Builder helpers ──────────────────────────────────────────────────────

    private static AgentRunRequest MakeRequest(string? feedback = null) =>
        new(
            Item: new ProjectItem("pid", "cid", 1, "Fix bug", "Fix the null ref", ProjectState.InProgress),
            Workspace: new TaskWorkspace("item-1", "agent/fix-bug", Path.GetTempPath(), "main"),
            PriorReviewFeedback: feedback);

    private static IOptions<AgentOptions> MakeOptions() =>
        Options.Create(new AgentOptions());

    private static IOptions<ScopeLimitOptions> MakeScopeLimits(
        int maxModelTurns = 40,
        int maxToolCalls = 200,
        int maxExecutionTimeSeconds = 1_800) =>
        Options.Create(new ScopeLimitOptions
        {
            MaxModelTurns = maxModelTurns,
            MaxToolCalls = maxToolCalls,
            MaxExecutionTimeSeconds = maxExecutionTimeSeconds,
        });

    private static PersonaLoader MakePersonaLoader()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "runner-tests-" + Guid.NewGuid().ToString("N"));
        var personasDir = Path.Combine(tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "developer.md"), "You are a developer.");
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        return new PersonaLoader("personas/developer.md", env);
    }

    private sealed class StubChatClientFactory : IAgentChatClientFactory
    {
        private readonly IChatClient _client;
        public StubChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string modelId) => _client;
    }

    private static AnthropicAgentRunner BuildRunner(
        IChatClient chatClient,
        IEnumerable<ITool>? tools = null,
        int hardCap = 40,
        int maxToolCalls = 200,
        int maxExecutionTimeSeconds = 1_800,
        IMcpToolSource? mcpToolSource = null) =>
        new(
            new StubChatClientFactory(chatClient),
            MakePersonaLoader(),
            MakeOptions(),
            MakeScopeLimits(hardCap, maxToolCalls, maxExecutionTimeSeconds),
            tools ?? [],
            NullLogger<AnthropicAgentRunner>.Instance,
            mcpToolSource);

    private sealed class StubMcpToolSource : IMcpToolSource
    {
        private readonly IReadOnlyList<AITool> _tools;
        public int Calls { get; private set; }
        public StubMcpToolSource(IReadOnlyList<AITool> tools) => _tools = tools;
        public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_tools);
        }
    }

    private sealed class FakeMcpAITool : AIFunction
    {
        private static readonly JsonElement EmptyObject = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public FakeMcpAITool(string name) { Name = name; }
        public override string Name { get; }
        public override string Description => "fake-mcp-tool";
        public override JsonElement JsonSchema => EmptyObject;
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>("ok");
    }

    // ── Chat response builders ───────────────────────────────────────────────

    /// <summary>Build a ChatResponse that contains one function-call to the named tool with the given JSON args.</summary>
    private static ChatResponse FunctionCallResponse(
        string toolName,
        string callId = "call-1",
        IDictionary<string, object?>? args = null)
    {
        var fcc = new FunctionCallContent(callId, toolName, args ?? new Dictionary<string, object?>());
        var msg = new ChatMessage(ChatRole.Assistant, [fcc]);
        return new ChatResponse(msg) { FinishReason = ChatFinishReason.ToolCalls };
    }

    /// <summary>Build a plain-text ChatResponse (no function calls) — the loop terminates here.</summary>
    private static ChatResponse TextResponse(string text = "Task done.")
    {
        var msg = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse(msg) { FinishReason = ChatFinishReason.Stop };
    }

    // ── Fake tool that records what the runner passed it ─────────────────────

    private sealed class FakeTool : ITool
    {
        public string Name { get; init; } = "read_file";
        public string Description { get; init; } = "Read";
        public JsonNode InputSchema { get; init; } = JsonNode.Parse("{\"type\":\"object\"}")!;
        public Func<JsonNode, IToolContext, CancellationToken, Task<ToolResult>>? Handler { get; init; }
        public int Calls { get; private set; }

        public async Task<ToolResult> InvokeAsync(JsonNode input, IToolContext context, CancellationToken ct)
        {
            Calls++;
            if (Handler is not null)
                return await Handler(input, context, ct);
            return new ToolResult(false, "ok");
        }
    }

    // ── Test: loop completes with text-only ──────────────────────────────────

    [Fact]
    public async Task Loop_completes_when_assistant_returns_text_only()
    {
        // Arrange: chat client returns one function-call (which the function-invoking middleware
        // resolves locally), then a text-only response.
        var tool = new FakeTool { Name = "read_file" };

        int call = 0;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                call++;
                return call == 1
                    ? Task.FromResult(FunctionCallResponse("read_file"))
                    : Task.FromResult(TextResponse());
            });

        var runner = BuildRunner(chatClient, [tool]);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.Completed);
        result.TurnsUsed.Should().Be(2);
        result.ToolCallsUsed.Should().Be(1);
        result.PullRequest.Should().BeNull();
        tool.Calls.Should().Be(1);
    }

    // ── Test: hard cap ───────────────────────────────────────────────────────

    [Fact]
    public async Task HardCapReached_after_MaxModelTurns_plus_one_turns()
    {
        // Arrange: client always returns tool_use; cap = 3 → after 4th request to the inner
        // client we exceed cap and the runner returns HardCapReached.
        var tool = new FakeTool { Name = "read_file" };

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(FunctionCallResponse("read_file")));

        var runner = BuildRunner(chatClient, [tool], hardCap: 3);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.HardCapReached);
        result.TurnsUsed.Should().Be(4); // turns 1,2,3 pass; turn 4 triggers the cap
        result.TerminationReason.Should().Contain("Hard cap of 3");
    }

    // ── Test: tool-call limit ─────────────────────────────────────────────────

    [Fact]
    public async Task ToolCallLimitReached_after_MaxToolCalls_tool_invocations()
    {
        // Arrange: client always returns a tool_use for a local tool; MaxToolCalls = 2.
        // The 3rd invocation is blocked by the adapter before the tool runs.
        var tool = new FakeTool { Name = "read_file" };

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(FunctionCallResponse("read_file")));

        // Large turn cap so the tool-call cap is the binding limit.
        var runner = BuildRunner(chatClient, [tool], hardCap: 100, maxToolCalls: 2);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.ToolCallLimitReached);
        result.ToolCallsUsed.Should().Be(2); // 2 calls succeed; the 3rd is blocked
        result.TerminationReason.Should().Contain("Tool-call limit of 2");
        tool.Calls.Should().Be(2);
    }

    // ── Test: execution-time limit ────────────────────────────────────────────

    [Fact]
    public async Task TimeLimitExceeded_when_run_exceeds_MaxExecutionTime()
    {
        // Arrange: the inner chat client blocks honouring its own cancellation token until
        // the run's timeout CTS fires. MaxExecutionTime = 0s → fires effectively immediately.
        var tool = new FakeTool { Name = "read_file" };

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var token = ci.Arg<CancellationToken>();
                // Wait until the run-scoped timeout cancels this token, then propagate OCE.
                await Task.Delay(Timeout.Infinite, token);
                return TextResponse();
            });

        // ct passed to RunAsync is NOT cancelled — only the internal timeout CTS fires.
        var runner = BuildRunner(chatClient, [tool], maxExecutionTimeSeconds: 0);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert: distinct outcome, NOT Cancelled and NOT ApiError.
        result.Outcome.Should().Be(AgentRunOutcome.TimeLimitExceeded);
        result.TerminationReason.Should().Contain("Execution time limit");
    }

    // ── Test: sandbox violation ──────────────────────────────────────────────

    [Fact]
    public async Task SandboxViolation_from_tool_ends_run()
    {
        var sandboxTool = new FakeTool
        {
            Name = "shell_run",
            Description = "Run shell",
            Handler = (_, _, _) => throw new SandboxViolationException("rm -rf not allowed"),
        };

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(FunctionCallResponse("shell_run")));

        var runner = BuildRunner(chatClient, [sandboxTool]);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.SandboxViolation);
        result.TerminationReason.Should().Contain("shell_run");

        // No further chat-client calls after violation (only the one tool-use request)
        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Test: non-sandbox tool exception is NOT latched as a sandbox violation ──

    [Fact]
    public async Task NonSandbox_tool_exception_surfaces_ApiError_not_SandboxViolation()
    {
        // Guards the Step-48 onToolThrew discrimination: the host callback latches ONLY
        // SandboxViolationException. A different tool exception must fall through to ApiError,
        // never be mislabeled SandboxViolation.
        var faultyTool = new FakeTool
        {
            Name = "read_file",
            Handler = (_, _, _) => throw new InvalidOperationException("tool blew up"),
        };

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(FunctionCallResponse("read_file")));

        var runner = BuildRunner(chatClient, [faultyTool]);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AgentRunOutcome.ApiError);
        result.Outcome.Should().NotBe(AgentRunOutcome.SandboxViolation);
    }

    // ── Test: cancellation ────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_returns_Cancelled_with_partial_history()
    {
        using var cts = new CancellationTokenSource();

        int callCount = 0;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount == 2)
                    cts.Cancel(); // cancel during second iteration
                if (callCount >= 3)
                    ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult(FunctionCallResponse("read_file"));
            });

        var tool = new FakeTool { Name = "read_file" };

        var runner = BuildRunner(chatClient, [tool]);

        // Act
        var result = await runner.RunAsync(MakeRequest(), cts.Token);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.Cancelled);
        result.TurnsUsed.Should().BeGreaterThan(0);
    }

    // ── Test: PR returned after create_pull_request ─────────────────────────

    [Fact]
    public async Task PullRequest_returned_after_create_pull_request_tool()
    {
        var expectedPr = new PullRequest(55, "sha-xyz", "https://github.com/foo/pull/55");

        var prTool = new FakeTool
        {
            Name = "create_pull_request",
            Description = "Create PR",
            // The tool side-effects the session in production; mimic that here. The runner always
            // supplies the concrete ToolContext, so downcast to reach the run session.
            Handler = (_, ctx, _) =>
            {
                ((ToolContext)ctx).Session.CreatedPullRequest = expectedPr;
                return Task.FromResult(new ToolResult(false, "{\"number\":55}"));
            },
        };

        int call = 0;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                call++;
                return call == 1
                    ? Task.FromResult(FunctionCallResponse("create_pull_request"))
                    : Task.FromResult(TextResponse("PR is open!"));
            });

        var runner = BuildRunner(chatClient, [prTool]);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.Completed);
        result.PullRequest.Should().Be(expectedPr);
        prTool.Calls.Should().Be(1);
    }

    // ── Test: MCP tools merged into the agent's tool list ───────────────────

    [Fact]
    public async Task RunAsync_appends_McpToolSource_tools_to_local_tools()
    {
        var localTool = new FakeTool { Name = "read_file" };
        var mcpTools = new List<AITool> { new FakeMcpAITool("gh_search"), new FakeMcpAITool("ctx_lookup") };
        var mcpSource = new StubMcpToolSource(mcpTools);

        ChatOptions? observedOptions = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observedOptions = ci.Arg<ChatOptions>();
                return Task.FromResult(TextResponse("done"));
            });

        var runner = BuildRunner(chatClient, [localTool], mcpToolSource: mcpSource);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AgentRunOutcome.Completed);
        mcpSource.Calls.Should().Be(1, "the runner must ask the MCP source for tools each run");
        observedOptions.Should().NotBeNull();
        var names = observedOptions!.Tools!
            .OfType<AIFunction>()
            .Select(t => t.Name)
            .ToList();
        names.Should().Contain("read_file");
        names.Should().Contain("gh_search");
        names.Should().Contain("ctx_lookup");
        names.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunAsync_works_when_McpToolSource_is_null()
    {
        var localTool = new FakeTool { Name = "read_file" };
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TextResponse("done")));

        var runner = BuildRunner(chatClient, [localTool], mcpToolSource: null);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AgentRunOutcome.Completed);
    }

    // ── Test: Temperature is not sent (deprecated for the model) ─────────────

    [Fact]
    public async Task RunAsync_does_not_set_Temperature_on_ChatOptions()
    {
        // Regression guard: newer Anthropic models reject the `temperature` request field
        // ("temperature is deprecated for this model" → AnthropicBadRequestException). The
        // runner must leave ChatOptions.Temperature unset (null) so the provider omits it.
        var localTool = new FakeTool { Name = "read_file" };

        ChatOptions? observedOptions = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observedOptions = ci.Arg<ChatOptions>();
                return Task.FromResult(TextResponse("done"));
            });

        var runner = BuildRunner(chatClient, [localTool]);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AgentRunOutcome.Completed);
        observedOptions.Should().NotBeNull();
        observedOptions!.Temperature.Should().BeNull(
            because: "the model rejects a `temperature` request field; it must not be sent");
    }

    // ── Test: API error from chat client ─────────────────────────────────────

    [Fact]
    public async Task ApiError_when_chat_client_throws()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("503 Service Unavailable"));

        var runner = BuildRunner(chatClient);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.ApiError);
        result.TerminationReason.Should().Contain("503");
    }
}
