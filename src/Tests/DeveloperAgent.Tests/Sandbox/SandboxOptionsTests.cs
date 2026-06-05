namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Tests that the production <c>appsettings.json</c> (the single source after Step-41)
/// defines the expected sandbox rules and shape. These read the bound configuration via
/// <see cref="ProductionSandboxConfig"/> rather than <c>new SandboxOptions()</c>, whose
/// lists are now empty by design.
/// </summary>
public sealed class SandboxOptionsTests
{
    [Fact]
    public void Default_DenyPathPatterns_contains_ssh_glob()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DenyPathPatterns.Should().Contain("~/.ssh/**");
    }

    [Fact]
    public void Default_DenyPathPatterns_contains_dotenv_glob()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DenyPathPatterns.Should().Contain(".env*");
    }

    [Fact]
    public void Default_DenyPathPatterns_contains_git_config()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DenyPathPatterns.Should().Contain(".git/config");
    }

    [Fact]
    public void Default_SecretFileRegexes_is_empty()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.SecretFileRegexes.Should().BeEmpty();
    }

    // ── Command deny rules ───────────────────────────────────────────────────

    [Fact]
    public void Default_DeniedCommands_includes_curl()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r => r.Program == "curl");
    }

    [Fact]
    public void Default_DeniedCommands_includes_wget()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r => r.Program == "wget");
    }

    [Fact]
    public void Default_DeniedCommands_includes_chmod_plus_x()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "chmod" && r.ArgPatterns != null && r.ArgPatterns.Contains("+x"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_git_push_force()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "git" && r.ArgPatterns != null && r.ArgPatterns.Contains("--force"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_gh_secret_set()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "gh"
            && r.ArgPatterns != null
            && r.ArgPatterns.Contains("secret")
            && r.ArgPatterns.Contains("set"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_gh_repo_delete()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "gh"
            && r.ArgPatterns != null
            && r.ArgPatterns.Contains("repo")
            && r.ArgPatterns.Contains("delete"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_gh_auth()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "gh"
            && r.ArgPatterns != null
            && r.ArgPatterns.Contains("auth"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_gh_api_mutating_method()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "gh"
            && r.ArgPatterns != null
            && r.ArgPatterns.Contains("api")
            && r.ArgPatterns.Contains("DELETE"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_dotnet_tool_install()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "dotnet"
            && r.ArgPatterns != null
            && r.ArgPatterns.Contains("tool")
            && r.ArgPatterns.Contains("install"));
    }

    // ── Allowed hosts ────────────────────────────────────────────────────────

    [Fact]
    public void Default_AllowedHosts_includes_api_anthropic_com()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.AllowedHosts.Should().Contain("api.anthropic.com");
    }

    [Fact]
    public void Default_AllowedHosts_includes_api_github_com()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.AllowedHosts.Should().Contain("api.github.com");
    }

    [Fact]
    public void Default_AllowedHosts_includes_wildcard_githubusercontent()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.AllowedHosts.Should().Contain("*.githubusercontent.com");
    }

    [Fact]
    public void Default_AllowedHosts_includes_context7()
    {
        var opts = ProductionSandboxConfig.Sandbox;

        opts.AllowedHosts.Should().Contain("context7.com");
    }
}
