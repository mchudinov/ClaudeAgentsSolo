using System.Text.Json;
using DeveloperAgent.Agent.Mcp;
using DeveloperAgent.Configuration;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent.Mcp;

/// <summary>
/// Unit tests for <see cref="McpToolSource"/> using a fake <see cref="IMcpClientConnector"/>.
/// No <c>npx</c> child processes are spawned.
/// </summary>
public sealed class McpToolSourceTests
{
    // ── A trivial fake AITool the connector can return ───────────────────────
    private sealed class FakeAITool : AIFunction
    {
        private static readonly JsonElement EmptyObject = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public FakeAITool(string name) { Name = name; }
        public override string Name { get; }
        public override string Description => "fake-mcp-tool";
        public override JsonElement JsonSchema => EmptyObject;
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>("ok");
    }

    private sealed class StubConnector : IMcpClientConnector
    {
        public List<(string Server, McpServerOptions Options)> Calls { get; } = new();
        public Dictionary<string, IReadOnlyList<AITool>> ByServer { get; } = new();
        public Dictionary<string, Exception> Throws { get; } = new();

        public Task<IReadOnlyList<AITool>> ConnectAndListToolsAsync(
            string serverName, McpServerOptions server, CancellationToken ct)
        {
            Calls.Add((serverName, server));
            if (Throws.TryGetValue(serverName, out var ex))
                throw ex;
            return Task.FromResult(ByServer.TryGetValue(serverName, out var t)
                ? t
                : (IReadOnlyList<AITool>)Array.Empty<AITool>());
        }
    }

    private static McpToolSource BuildSource(StubConnector connector, McpOptions opts)
        => new(connector, Options.Create(opts), NullLogger<McpToolSource>.Instance);

    [Fact]
    public async Task GetToolsAsync_returns_empty_when_all_servers_disabled()
    {
        var connector = new StubConnector();
        var source = BuildSource(connector, new McpOptions());

        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.Should().BeEmpty();
        connector.Calls.Should().BeEmpty("disabled servers must never be connected to");
    }

    [Fact]
    public async Task GetToolsAsync_collects_tools_from_only_enabled_servers()
    {
        var connector = new StubConnector();
        connector.ByServer["GitHub"] = new AITool[] { new FakeAITool("gh_search") };
        // Context7 will be disabled — its entry should never be queried.
        connector.ByServer["Context7"] = new AITool[] { new FakeAITool("ctx_lookup") };

        var opts = new McpOptions
        {
            GitHub = new McpServerOptions { Enabled = true, Command = "npx", Arguments = new[] { "-y", "gh" } },
            Context7 = new McpServerOptions { Enabled = false, Command = "npx" },
        };

        var source = BuildSource(connector, opts);
        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.Should().HaveCount(1);
        tools.Single().Should().BeOfType<FakeAITool>().Which.Name.Should().Be("gh_search");
        connector.Calls.Should().ContainSingle().Which.Server.Should().Be("GitHub");
    }

    [Fact]
    public async Task GetToolsAsync_concatenates_tools_from_multiple_enabled_servers()
    {
        var connector = new StubConnector();
        connector.ByServer["GitHub"] = new AITool[] { new FakeAITool("gh_a"), new FakeAITool("gh_b") };
        connector.ByServer["Context7"] = new AITool[] { new FakeAITool("ctx_c") };

        var opts = new McpOptions
        {
            GitHub = new McpServerOptions { Enabled = true, Command = "npx" },
            Context7 = new McpServerOptions { Enabled = true, Command = "npx" },
        };

        var source = BuildSource(connector, opts);
        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.Should().HaveCount(3);
        tools.Select(t => ((AIFunction)t).Name).Should().BeEquivalentTo("gh_a", "gh_b", "ctx_c");
    }

    [Fact]
    public async Task GetToolsAsync_continues_when_one_server_throws()
    {
        var connector = new StubConnector();
        connector.Throws["GitHub"] = new InvalidOperationException("npx not on PATH");
        connector.ByServer["Context7"] = new AITool[] { new FakeAITool("ctx_only") };

        var opts = new McpOptions
        {
            GitHub = new McpServerOptions { Enabled = true, Command = "npx" },
            Context7 = new McpServerOptions { Enabled = true, Command = "npx" },
        };

        var source = BuildSource(connector, opts);
        var tools = await source.GetToolsAsync(CancellationToken.None);

        // GitHub failed → 0 contributions; Context7 succeeded → 1 contribution.
        tools.Should().ContainSingle().Which.Should().BeOfType<FakeAITool>()
            .Which.Name.Should().Be("ctx_only");
    }

    [Fact]
    public async Task GetToolsAsync_caches_first_result_across_calls()
    {
        var connector = new StubConnector();
        connector.ByServer["GitHub"] = new AITool[] { new FakeAITool("gh_x") };

        var opts = new McpOptions
        {
            GitHub = new McpServerOptions { Enabled = true, Command = "npx" },
            Context7 = new McpServerOptions { Enabled = false, Command = "npx" },
        };
        var source = BuildSource(connector, opts);

        _ = await source.GetToolsAsync(CancellationToken.None);
        _ = await source.GetToolsAsync(CancellationToken.None);
        _ = await source.GetToolsAsync(CancellationToken.None);

        connector.Calls.Should().HaveCount(1, "the source must cache to avoid re-spawning npx");
    }

    [Fact]
    public async Task GetToolsAsync_propagates_cancellation()
    {
        var connector = new StubConnector();
        connector.Throws["GitHub"] = new OperationCanceledException();

        var opts = new McpOptions
        {
            GitHub = new McpServerOptions { Enabled = true, Command = "npx" },
            Context7 = new McpServerOptions { Enabled = false, Command = "npx" },
        };
        var source = BuildSource(connector, opts);

        Func<Task> act = () => source.GetToolsAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
