using DeveloperAgent.Agent.Tools;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Tests for the <c>Denied</c> error shape on <see cref="ToolResult"/>.
/// Construction with <c>ToolErrorKind.Denied</c> sets <c>IsError = true</c>
/// and surfaces the kind to the caller.
/// </summary>
public sealed class ToolResultDeniedTests
{
    [Fact]
    public void Denied_constructor_sets_IsError_true()
    {
        var result = ToolResult.Denied("blocked by deny rule");

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void Denied_constructor_sets_ErrorKind_Denied()
    {
        var result = ToolResult.Denied("blocked by deny rule");

        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    [Fact]
    public void Denied_constructor_propagates_message_into_Content()
    {
        var result = ToolResult.Denied("blocked: ~/.ssh/**");

        result.Content.Should().Contain("blocked");
        result.Content.Should().Contain("~/.ssh/**");
    }

    [Fact]
    public void Existing_two_arg_constructor_leaves_ErrorKind_null()
    {
        var result = new ToolResult(true, "generic error");

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().BeNull();
    }
}
