using Agent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Agent.Workspace.Tests;

public sealed class DiffScopeLimitOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new DiffScopeLimitOptions();
        options.MaxChangedFiles.Should().Be(50);
        options.MaxChangedLines.Should().Be(2_000);
    }

    [Fact]
    public void Binding_from_the_ScopeLimits_section_overrides_defaults()
    {
        // The diff-scope pair binds from the host's ScopeLimits section (the host binds both this
        // record and the residual ScopeLimitOptions from one section — these are scalars, so no
        // double-append). The record now lives in Agent.Workspace but the section name is unchanged.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ScopeLimits:MaxChangedFiles"] = "10",
                ["ScopeLimits:MaxChangedLines"] = "300",
            })
            .Build();

        var options = new DiffScopeLimitOptions();
        config.GetSection("ScopeLimits").Bind(options);

        options.MaxChangedFiles.Should().Be(10);
        options.MaxChangedLines.Should().Be(300);
    }
}

public sealed class WorkspaceRootOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        // The workspace-manager root keeps the same default as the sandbox's WorkspaceOptions.RootPath —
        // both bind from the one Workspace section and resolve to the same value.
        var options = new WorkspaceRootOptions();
        options.RootPath.Should().Be(Path.Combine(Path.GetTempPath(), "developer-agent", "workspace"));
    }

    [Fact]
    public void RootPath_default_lives_under_the_OS_temp_directory()
    {
        // Regression guard: a filesystem-root default like "/workspace" is not creatable by a
        // non-root process.
        var options = new WorkspaceRootOptions();

        options.RootPath.Should().StartWith(Path.GetTempPath());
        options.RootPath.Should().NotBe("/workspace");
    }

    [Fact]
    public void Binding_from_the_Workspace_section_overrides_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workspace:RootPath"] = "/custom/workspace/root",
            })
            .Build();

        var options = new WorkspaceRootOptions();
        config.GetSection("Workspace").Bind(options);

        options.RootPath.Should().Be("/custom/workspace/root");
    }
}
