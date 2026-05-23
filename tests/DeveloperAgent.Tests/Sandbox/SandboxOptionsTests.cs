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
}
