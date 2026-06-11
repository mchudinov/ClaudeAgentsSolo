using DeveloperAgent.Configuration;
using DeveloperAgent.Tests.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Configuration;

public sealed class AgentOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new AgentOptions();
        options.Name.Should().Be("DeveloperAgent");
        options.Model.Should().Be("claude-opus-4-7");
        options.Effort.Should().Be("xhigh");
        options.PollIntervalSeconds.Should().Be(60);
        options.ReviewPollIntervalSeconds.Should().Be(60);
    }

    [Fact]
    public void PersonaPath_default_is_relative()
    {
        var options = new AgentOptions();
        options.PersonaPath.Should().Be("personas/developer.md");
    }

    [Fact]
    public void Binding_from_configuration_overrides_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Name"] = "TestAgent",
                ["Agent:Model"] = "claude-3-5-sonnet",
                ["Agent:Effort"] = "high",
                ["Agent:PollIntervalSeconds"] = "30",
                ["Agent:ReviewPollIntervalSeconds"] = "45",
            })
            .Build();

        var options = new AgentOptions();
        config.GetSection("Agent").Bind(options);

        options.Name.Should().Be("TestAgent");
        options.Model.Should().Be("claude-3-5-sonnet");
        options.Effort.Should().Be("high");
        options.PollIntervalSeconds.Should().Be(30);
        options.ReviewPollIntervalSeconds.Should().Be(45);
    }
}

public sealed class ScopeLimitOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new ScopeLimitOptions();
        options.MaxExecutionTimeSeconds.Should().Be(1_800);
        options.MaxModelTurns.Should().Be(40);
        options.MaxToolCalls.Should().Be(200);
        options.MaxRetryCount.Should().Be(3);
        options.MaxPRChangedFiles.Should().Be(50);
        options.MaxPRChangedLines.Should().Be(2_000);
    }

    [Fact]
    public void Binding_from_configuration_overrides_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ScopeLimits:MaxExecutionTimeSeconds"] = "600",
                ["ScopeLimits:MaxModelTurns"] = "25",
                ["ScopeLimits:MaxToolCalls"] = "75",
                ["ScopeLimits:MaxRetryCount"] = "5",
                ["ScopeLimits:MaxPRChangedFiles"] = "12",
                ["ScopeLimits:MaxPRChangedLines"] = "400",
            })
            .Build();

        var options = new ScopeLimitOptions();
        config.GetSection("ScopeLimits").Bind(options);

        options.MaxExecutionTimeSeconds.Should().Be(600);
        options.MaxModelTurns.Should().Be(25);
        options.MaxToolCalls.Should().Be(75);
        options.MaxRetryCount.Should().Be(5);
        options.MaxPRChangedFiles.Should().Be(12);
        options.MaxPRChangedLines.Should().Be(400);
    }
}

// Note: DiffScopeLimitOptions and WorkspaceRootOptions moved to the Agent.Workspace library in
// Step-51; their option tests now live in Agent.Workspace.Tests (WorkspaceConfigOptionsTests).

public sealed class AnthropicOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new AnthropicOptions();
        options.ApiKeySecretName.Should().Be("anthropic-api-key");
    }
}

public sealed class GitHubOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new GitHubOptions();
        options.Owner.Should().Be("");
        options.Repository.Should().NotBeNull();
        options.Project.Should().NotBeNull();
        options.TokenSecretName.Should().Be("github-token");
    }
}

public sealed class RepositoryOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new RepositoryOptions();
        options.Name.Should().Be("");
        options.Url.Should().Be("");
        options.DefaultBranch.Should().Be("main");
    }
}

public sealed class ProjectOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new ProjectOptions();
        options.Name.Should().Be("");
        options.Number.Should().Be(0);
        options.OwnerType.Should().Be("Organization");
    }
}

public sealed class ProjectStateNamesTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new ProjectStateNames();
        options.Backlog.Should().Be("Backlog");
        options.Ready.Should().Be("Ready");
        options.InProgress.Should().Be("In Progress");
        options.InReview.Should().Be("In Review");
        options.Done.Should().Be("Done");
    }

    [Fact]
    public void Binding_from_GitHub_States_section_overrides_all_five()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:States:Backlog"] = "Icebox",
                ["GitHub:States:Ready"] = "Todo",
                ["GitHub:States:InProgress"] = "Doing",
                ["GitHub:States:InReview"] = "Reviewing",
                ["GitHub:States:Done"] = "Shipped",
            })
            .Build();

        var options = new ProjectStateNames();
        config.GetSection("GitHub:States").Bind(options);

        options.Backlog.Should().Be("Icebox");
        options.Ready.Should().Be("Todo");
        options.InProgress.Should().Be("Doing");
        options.InReview.Should().Be("Reviewing");
        options.Done.Should().Be("Shipped");
    }
}

public sealed class TriageOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new TriageOptions();
        options.Enabled.Should().BeFalse(
            because: "a host without a Triage section must boot with the gate off — no surprise LLM calls");
        options.RepoScope.Should().Be("");
        options.AgentSkill.Should().NotBeNullOrWhiteSpace(
            because: "the agent-skill description has a sensible built-in default");
    }

    [Fact]
    public void Binding_from_Triage_section_overrides_all_members()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Triage:Enabled"] = "true",
                ["Triage:RepoScope"] = "A widget service.",
                ["Triage:AgentSkill"] = "Go microservices.",
            })
            .Build();

        var options = new TriageOptions();
        config.GetSection("Triage").Bind(options);

        options.Enabled.Should().BeTrue();
        options.RepoScope.Should().Be("A widget service.");
        options.AgentSkill.Should().Be("Go microservices.");
    }
}

public sealed class WorkspaceOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new WorkspaceOptions();
        options.RootPath.Should().Be(Path.Combine(Path.GetTempPath(), "developer-agent", "workspace"));
        // Step-41: the allow-list now lives solely in appsettings.json, so the record's
        // default is empty (the binder would otherwise append config onto it and duplicate).
        options.AllowedCommands.Should().BeEmpty();
    }

    [Fact]
    public void RootPath_default_lives_under_the_OS_temp_directory()
    {
        // Regression guard: the previous default ("/workspace") is a filesystem-root
        // path that a non-root process cannot create, so WorkspaceManager.PrepareAsync
        // failed with UnauthorizedAccessException on a normal host. The default must
        // resolve to a location any process can create without elevated permissions.
        var options = new WorkspaceOptions();

        options.RootPath.Should().StartWith(Path.GetTempPath(),
            because: "the default workspace root must be creatable by a non-root process; " +
                     "a filesystem-root path like '/workspace' requires elevated permissions");
        options.RootPath.Should().NotBe("/workspace",
            because: "'/workspace' is an unwritable host default reserved for the in-container mount path");
    }

    [Fact]
    public void AllowedCommands_defaults_are_broad_command_families()
    {
        // The allowlist is a prefix allowlist (CommandSandbox.FindAllowlistEntry):
        // a bare entry like "dotnet" allows every "dotnet …" invocation. We allow the
        // whole dotnet / git / gh families plus the read-only file commands ls / dir
        // and pwd. Dangerous verbs inside those families (git push --force, gh repo
        // delete, gh auth, gh api -X DELETE/PUT/POST, dotnet tool install) are blocked
        // by the command deny policy, which runs BEFORE the allowlist (deny wins).
        // "cd" is intentionally absent: there is no real shell, so a "cd" segment would
        // run as a direct exec and cannot change the agent's working directory.
        // The read-only inspection commands cat / echo / grep / head / tail / wc are also
        // allowed so the agent can run common diagnostics; a benign un-listed command is a
        // recoverable miss (returned to the model), not a fatal run-ender. "find" is
        // deliberately EXCLUDED — bare find permits -exec/-delete, which would run arbitrary
        // subcommands outside the sandbox's per-command validation.
        // Step-41: sourced from the bound appsettings.json (the single source) — the record
        // default is empty.
        ProductionSandboxConfig.AllowedCommands.Should().BeEquivalentTo(new[]
        {
            "dotnet",
            "git",
            "gh",
            "ls",
            "dir",
            "pwd",
            "cat",
            "echo",
            "grep",
            "head",
            "tail",
            "wc",
        }, o => o.WithStrictOrdering());
    }
}
