using DeveloperAgent.Sandbox;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>Tests for <see cref="SandboxOptions"/> defaults and shape.</summary>
public sealed class SandboxOptionsTests
{
    [Fact]
    public void Default_DenyPathPatterns_contains_ssh_glob()
    {
        var opts = new SandboxOptions();

        opts.DenyPathPatterns.Should().Contain("~/.ssh/**");
    }

    [Fact]
    public void Default_DenyPathPatterns_contains_dotenv_glob()
    {
        var opts = new SandboxOptions();

        opts.DenyPathPatterns.Should().Contain(".env*");
    }

    [Fact]
    public void Default_DenyPathPatterns_contains_git_config()
    {
        var opts = new SandboxOptions();

        opts.DenyPathPatterns.Should().Contain(".git/config");
    }

    [Fact]
    public void Default_SecretFileRegexes_is_empty()
    {
        var opts = new SandboxOptions();

        opts.SecretFileRegexes.Should().BeEmpty();
    }

    // ── Command deny rules ───────────────────────────────────────────────────

    [Fact]
    public void Default_DeniedCommands_includes_curl()
    {
        var opts = new SandboxOptions();

        opts.DeniedCommands.Should().Contain(r => r.Program == "curl");
    }

    [Fact]
    public void Default_DeniedCommands_includes_wget()
    {
        var opts = new SandboxOptions();

        opts.DeniedCommands.Should().Contain(r => r.Program == "wget");
    }

    [Fact]
    public void Default_DeniedCommands_includes_chmod_plus_x()
    {
        var opts = new SandboxOptions();

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "chmod" && r.ArgPatterns != null && r.ArgPatterns.Contains("+x"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_git_push_force()
    {
        var opts = new SandboxOptions();

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "git" && r.ArgPatterns != null && r.ArgPatterns.Contains("--force"));
    }

    [Fact]
    public void Default_DeniedCommands_includes_gh_secret_set()
    {
        var opts = new SandboxOptions();

        opts.DeniedCommands.Should().Contain(r =>
            r.Program == "gh"
            && r.ArgPatterns != null
            && r.ArgPatterns.Contains("secret")
            && r.ArgPatterns.Contains("set"));
    }

    // ── Allowed hosts ────────────────────────────────────────────────────────

    [Fact]
    public void Default_AllowedHosts_includes_api_anthropic_com()
    {
        var opts = new SandboxOptions();

        opts.AllowedHosts.Should().Contain("api.anthropic.com");
    }

    [Fact]
    public void Default_AllowedHosts_includes_api_github_com()
    {
        var opts = new SandboxOptions();

        opts.AllowedHosts.Should().Contain("api.github.com");
    }

    [Fact]
    public void Default_AllowedHosts_includes_wildcard_githubusercontent()
    {
        var opts = new SandboxOptions();

        opts.AllowedHosts.Should().Contain("*.githubusercontent.com");
    }

    [Fact]
    public void Default_AllowedHosts_includes_context7()
    {
        var opts = new SandboxOptions();

        opts.AllowedHosts.Should().Contain("context7.com");
    }
}
