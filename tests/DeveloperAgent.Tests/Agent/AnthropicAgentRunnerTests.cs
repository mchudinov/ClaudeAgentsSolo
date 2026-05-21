using System.Text.Json.Nodes;
using Anthropic.SDK.Messaging;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Unit tests for <see cref="AnthropicAgentRunner"/> using scripted fake clients.
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

    private static IOptions<AgentOptions> MakeOptions(int hardCap = 40) =>
        Options.Create(new AgentOptions { MaxModelTurnsHardCap = hardCap });

    private static PersonaLoader MakePersonaLoader()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "runner-tests-" + Guid.NewGuid().ToString("N"));
        var personasDir = Path.Combine(tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "developer.md"), "You are a developer.");
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        return new PersonaLoader(Options.Create(new AgentOptions { PersonaPath = "personas/developer.md" }), env);
    }

    private static AnthropicAgentRunner BuildRunner(
        IAnthropicClient client,
        IEnumerable<ITool>? tools = null,
        int hardCap = 40) =>
        new(
            client,
            MakePersonaLoader(),
            MakeOptions(hardCap),
            tools ?? [],
            NullLogger<AnthropicAgentRunner>.Instance);

    // ── Fake helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="AnthropicResponse"/> with a single tool_use block.
    /// </summary>
    private static AnthropicResponse ToolUseResponse(string toolName, string toolId = "tu-1", JsonNode? input = null)
    {
        var toolUse = new ToolUseContent
        {
            Id = toolId,
            Name = toolName,
            Input = input ?? JsonNode.Parse("{}")!,
        };
        var assistantMsg = new Message
        {
            Role = RoleType.Assistant,
            Content = [toolUse]
        };
        return new AnthropicResponse("tool_use", [toolUse], assistantMsg);
    }

    /// <summary>
    /// Builds an <see cref="AnthropicResponse"/> with a text-only assistant message.
    /// </summary>
    private static AnthropicResponse TextResponse(string text = "Task done.") =>
        new(
            StopReason: "end_turn",
            ContentBlocks: [new TextContent { Text = text }],
            AssistantMessage: new Message
            {
                Role = RoleType.Assistant,
                Content = [new TextContent { Text = text }]
            });

    // ── Test: loop completes with text-only ──────────────────────────────────

    [Fact]
    public async Task Loop_completes_when_assistant_returns_text_only()
    {
        // Arrange: client returns one tool_use, then a text-only response
        var client = Substitute.For<IAnthropicClient>();
        var fakeTool = Substitute.For<ITool>();
        fakeTool.Name.Returns("read_file");
        fakeTool.Description.Returns("Read a file");
        fakeTool.InputSchema.Returns(JsonNode.Parse("{\"type\":\"object\"}")!);
        fakeTool.InvokeAsync(Arg.Any<JsonNode>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult(false, "file contents"));

        client.SendAsync(Arg.Any<AnthropicRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                ToolUseResponse("read_file"),  // first call → tool_use
                TextResponse());               // second call → done

        var runner = BuildRunner(client, [fakeTool]);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.Completed);
        result.TurnsUsed.Should().Be(2);
        result.ToolCallsUsed.Should().Be(1);
        result.PullRequest.Should().BeNull();
    }

    // ── Test: hard cap ───────────────────────────────────────────────────────

    [Fact]
    public async Task HardCapReached_after_MaxModelTurnsHardCap_plus_one_turns()
    {
        // Arrange: client always returns tool_use; cap = 3 → after 4th turn we exceed cap
        var client = Substitute.For<IAnthropicClient>();
        var fakeTool = Substitute.For<ITool>();
        fakeTool.Name.Returns("read_file");
        fakeTool.Description.Returns("Read a file");
        fakeTool.InputSchema.Returns(JsonNode.Parse("{\"type\":\"object\"}")!);
        fakeTool.InvokeAsync(Arg.Any<JsonNode>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult(false, "ok"));

        // Always return tool_use
        client.SendAsync(Arg.Any<AnthropicRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToolUseResponse("read_file"));

        var runner = BuildRunner(client, [fakeTool], hardCap: 3);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.HardCapReached);
        result.TurnsUsed.Should().Be(4); // turns 1,2,3 pass; turn 4 triggers the cap
    }

    // ── Test: sandbox violation ──────────────────────────────────────────────

    [Fact]
    public async Task SandboxViolation_from_tool_ends_run()
    {
        var client = Substitute.For<IAnthropicClient>();
        var fakeTool = Substitute.For<ITool>();
        fakeTool.Name.Returns("shell_run");
        fakeTool.Description.Returns("Run shell");
        fakeTool.InputSchema.Returns(JsonNode.Parse("{\"type\":\"object\"}")!);
        fakeTool.InvokeAsync(Arg.Any<JsonNode>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns<ToolResult>(_ => throw new SandboxViolationException("rm -rf not allowed"));

        client.SendAsync(Arg.Any<AnthropicRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToolUseResponse("shell_run"));

        var runner = BuildRunner(client, [fakeTool]);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.SandboxViolation);
        result.TerminationReason.Should().Contain("shell_run");

        // No further client calls after violation
        await client.Received(1).SendAsync(Arg.Any<AnthropicRequest>(), Arg.Any<CancellationToken>());
    }

    // ── Test: cancellation ────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_returns_Cancelled_with_partial_history()
    {
        using var cts = new CancellationTokenSource();

        var client = Substitute.For<IAnthropicClient>();
        int callCount = 0;
        client.SendAsync(Arg.Any<AnthropicRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 2)
                    cts.Cancel(); // cancel during second iteration
                if (callCount >= 3)
                    callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return ToolUseResponse("read_file");
            });

        var fakeTool = Substitute.For<ITool>();
        fakeTool.Name.Returns("read_file");
        fakeTool.Description.Returns("Read");
        fakeTool.InputSchema.Returns(JsonNode.Parse("{\"type\":\"object\"}")!);
        fakeTool.InvokeAsync(Arg.Any<JsonNode>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult(false, "ok"));

        var runner = BuildRunner(client, [fakeTool]);

        // Act: run with a pre-cancel via the token checked at loop start
        // Use a separate cancel approach: cancel immediately before 3rd iteration
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

        var client = Substitute.For<IAnthropicClient>();
        client.SendAsync(Arg.Any<AnthropicRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                ToolUseResponse("create_pull_request"),
                TextResponse("PR is open!"));

        // Fake tool that sets session.CreatedPullRequest
        var prTool = Substitute.For<ITool>();
        prTool.Name.Returns("create_pull_request");
        prTool.Description.Returns("Create PR");
        prTool.InputSchema.Returns(JsonNode.Parse("{\"type\":\"object\"}")!);
        prTool.InvokeAsync(Arg.Any<JsonNode>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Simulate the real tool setting session.CreatedPullRequest
                var ctx = callInfo.Arg<ToolContext>();
                ctx.Session.CreatedPullRequest = expectedPr;
                return new ToolResult(false, "{\"number\":55}");
            });

        var runner = BuildRunner(client, [prTool]);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.Completed);
        result.PullRequest.Should().Be(expectedPr);
    }

    // ── Test: API error after retries ────────────────────────────────────────

    [Fact]
    public async Task ApiError_after_retries_exhausted()
    {
        var client = Substitute.For<IAnthropicClient>();
        client.SendAsync(Arg.Any<AnthropicRequest>(), Arg.Any<CancellationToken>())
            .Returns<AnthropicResponse>(_ => throw new AnthropicApiException("503 Service Unavailable"));

        var runner = BuildRunner(client);

        // Act
        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AgentRunOutcome.ApiError);
        result.TerminationReason.Should().Contain("503");
    }
}
