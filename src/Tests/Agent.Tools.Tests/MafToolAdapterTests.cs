using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Agent.Tools.Tests;

/// <summary>
/// Unit tests for <see cref="MafToolAdapter"/> — the ITool -> AIFunction bridge. These lock the
/// two seams Step-48 introduced: the <see cref="IToolCallBudget"/> gate/increment ordering and the
/// <c>onToolThrew</c> latch callback (which the host uses to surface a sandbox violation).
/// </summary>
public sealed class MafToolAdapterTests
{
    private static readonly IToolContext Ctx = new TestToolContext(Path.GetTempPath());

    private sealed class FakeTool : ITool
    {
        public string Name { get; init; } = "fake_tool";
        public string Description => "fake";
        public JsonNode InputSchema { get; } = JsonNode.Parse("{\"type\":\"object\"}")!;
        public Func<JsonNode, IToolContext, CancellationToken, Task<ToolResult>>? Handler { get; init; }
        public int Calls { get; private set; }

        public Task<ToolResult> InvokeAsync(JsonNode input, IToolContext context, CancellationToken ct)
        {
            Calls++;
            return Handler is not null
                ? Handler(input, context, ct)
                : Task.FromResult(new ToolResult(false, "ok"));
        }
    }

    [Fact]
    public async Task Successful_invocation_records_budget_and_returns_payload()
    {
        var tool = new FakeTool { Handler = (_, _, _) => Task.FromResult(new ToolResult(false, "ok")) };
        var budget = Substitute.For<IToolCallBudget>();

        var adapter = new MafToolAdapter(tool, Ctx, budget);
        var result = await adapter.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        tool.Calls.Should().Be(1);
        budget.Received(1).ThrowIfExhausted();
        budget.Received(1).Record();

        var payload = JsonSerializer.SerializeToElement(result);
        payload.GetProperty("is_error").GetBoolean().Should().BeFalse();
        payload.GetProperty("content").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Error_result_is_propagated_as_payload()
    {
        var tool = new FakeTool { Handler = (_, _, _) => Task.FromResult(new ToolResult(true, "bad input")) };
        var budget = Substitute.For<IToolCallBudget>();

        var adapter = new MafToolAdapter(tool, Ctx, budget);
        var result = await adapter.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        // A returned error result is a successful invocation — budget is still consumed.
        budget.Received(1).Record();
        var payload = JsonSerializer.SerializeToElement(result);
        payload.GetProperty("is_error").GetBoolean().Should().BeTrue();
        payload.GetProperty("content").GetString().Should().Be("bad input");
    }

    [Fact]
    public async Task Exhausted_budget_blocks_invocation_before_the_tool_runs()
    {
        var tool = new FakeTool();
        var budget = Substitute.For<IToolCallBudget>();
        budget.When(b => b.ThrowIfExhausted()).Do(_ => throw new InvalidOperationException("exhausted"));

        var adapter = new MafToolAdapter(tool, Ctx, budget);

        var act = async () => await adapter.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("exhausted");
        tool.Calls.Should().Be(0, "the gate must throw before the tool runs");
        budget.DidNotReceive().Record();
    }

    [Fact]
    public async Task Tool_exception_fires_onToolThrew_then_rethrows_without_recording()
    {
        var boom = new InvalidOperationException("boom");
        var tool = new FakeTool { Name = "shell_run", Handler = (_, _, _) => throw boom };
        var budget = Substitute.For<IToolCallBudget>();

        string? observedName = null;
        Exception? observedEx = null;
        void OnThrew(string name, Exception ex) { observedName = name; observedEx = ex; }

        var adapter = new MafToolAdapter(tool, Ctx, budget, OnThrew);

        var act = async () => await adapter.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        observedName.Should().Be("shell_run");
        observedEx.Should().BeSameAs(boom);
        budget.DidNotReceive().Record();
    }

    [Fact]
    public async Task Tool_exception_without_callback_still_rethrows()
    {
        var tool = new FakeTool { Handler = (_, _, _) => throw new InvalidOperationException("boom") };
        var budget = Substitute.For<IToolCallBudget>();

        var adapter = new MafToolAdapter(tool, Ctx, budget, onToolThrew: null);

        var act = async () => await adapter.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public void Name_description_and_schema_are_surfaced_from_the_tool()
    {
        var tool = new FakeTool { Name = "read_file" };
        var adapter = new MafToolAdapter(tool, Ctx, Substitute.For<IToolCallBudget>());

        adapter.Name.Should().Be("read_file");
        adapter.Description.Should().Be("fake");
        adapter.JsonSchema.GetProperty("type").GetString().Should().Be("object");
    }
}
