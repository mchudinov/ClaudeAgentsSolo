using DeveloperAgent.Sandbox;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Tests for <see cref="CommandDenyPolicy"/>. The policy is consulted by the
/// <see cref="DeveloperAgent.Workspace.CommandSandbox"/> BEFORE the allowlist —
/// a deny hit short-circuits and overrides any matching allow entry.
/// </summary>
public sealed class CommandDenyPolicyTests
{
    private static ICommandDenyPolicy MakePolicy(IReadOnlyList<CommandDenyRule>? rules = null) =>
        new CommandDenyPolicy(Options.Create(new SandboxOptions
        {
            DeniedCommands = rules ?? new SandboxOptions().DeniedCommands,
        }));

    private static IReadOnlyList<string> Argv(params string[] tokens) => tokens;

    // ── Default rules: must be denied ────────────────────────────────────────

    [Theory]
    [InlineData("curl", "https://evil.com")]
    [InlineData("curl", "-X", "POST", "https://example.com/api")]
    [InlineData("wget", "https://example.com/file.sh")]
    [InlineData("chmod", "+x", "/tmp/script.sh")]
    [InlineData("git", "push", "--force")]
    [InlineData("git", "push", "--force", "origin", "main")]
    [InlineData("git", "push", "origin", "main", "--force")]
    [InlineData("git", "push", "-f")]
    [InlineData("git", "push", "-f", "origin", "main")]
    [InlineData("git", "push", "--force-with-lease")]
    [InlineData("gh", "secret", "set", "MY_TOKEN")]
    [InlineData("gh", "secret", "remove", "MY_TOKEN")]
    [InlineData("gh", "secret", "delete", "MY_TOKEN")]
    [InlineData("gh", "api", "-X", "PUT", "/repos/owner/repo/branches/main/protection")]
    [InlineData("gh", "api", "/repos/owner/repo/branches/main/protection")]
    public void Default_rules_deny_known_bypass_commands(params string[] argv)
    {
        var policy = MakePolicy();

        var result = policy.Check(argv);

        result.IsDenied.Should().BeTrue();
        result.RuleName.Should().NotBeNullOrWhiteSpace();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    // ── Default rules: must be allowed ───────────────────────────────────────

    [Theory]
    [InlineData("dotnet", "build")]
    [InlineData("dotnet", "test", "--no-build")]
    [InlineData("git", "status")]
    [InlineData("git", "push", "origin", "main")] // push without --force is allowed by deny policy
    [InlineData("git", "push", "--set-upstream", "origin", "my-branch")]
    [InlineData("git", "commit", "-m", "fix")]
    [InlineData("chmod", "644", "/tmp/data.txt")] // chmod without +x is fine
    [InlineData("gh", "pr", "create")]            // pr operations are not denied
    [InlineData("gh", "issue", "list")]
    [InlineData("gh", "api", "/repos/owner/repo")] // generic api call without branch-protection
    public void Default_rules_allow_non_matching_commands(params string[] argv)
    {
        var policy = MakePolicy();

        var result = policy.Check(argv);

        result.IsDenied.Should().BeFalse();
        result.RuleName.Should().BeNull();
        result.Reason.Should().BeNull();
    }

    // ── Empty / null inputs ──────────────────────────────────────────────────

    [Fact]
    public void Empty_argv_is_allowed()
    {
        var policy = MakePolicy();

        var result = policy.Check([]);

        result.IsDenied.Should().BeFalse();
    }

    [Fact]
    public void No_rules_means_everything_is_allowed()
    {
        var policy = MakePolicy(rules: []);

        var result = policy.Check(Argv("curl", "https://evil.com"));

        result.IsDenied.Should().BeFalse();
    }

    // ── Rule name surfaces in result ─────────────────────────────────────────

    [Fact]
    public void Denied_result_carries_rule_name()
    {
        var policy = MakePolicy();

        var result = policy.Check(Argv("wget", "https://example.com/x"));

        result.IsDenied.Should().BeTrue();
        result.RuleName.Should().Be("no-wget");
    }

    [Fact]
    public void Denied_reason_contains_program_name_and_rule_name()
    {
        var policy = MakePolicy();

        var result = policy.Check(Argv("curl", "https://example.com"));

        result.IsDenied.Should().BeTrue();
        result.Reason.Should().Contain("curl");
        result.Reason.Should().Contain("no-curl");
    }

    [Fact]
    public void Git_push_force_reason_contains_force_token()
    {
        var policy = MakePolicy();

        var result = policy.Check(Argv("git", "push", "--force"));

        result.IsDenied.Should().BeTrue();
        result.Reason.Should().Contain("--force");
    }

    // ── Custom rules ─────────────────────────────────────────────────────────

    [Fact]
    public void Custom_program_only_rule_denies_program()
    {
        var policy = MakePolicy(rules: [new CommandDenyRule("no-rm", "rm")]);

        var result = policy.Check(Argv("rm", "-rf", "/"));

        result.IsDenied.Should().BeTrue();
        result.RuleName.Should().Be("no-rm");
    }

    [Fact]
    public void Custom_program_only_rule_does_not_affect_other_programs()
    {
        var policy = MakePolicy(rules: [new CommandDenyRule("no-rm", "rm")]);

        var result = policy.Check(Argv("ls", "-la"));

        result.IsDenied.Should().BeFalse();
    }

    [Fact]
    public void Custom_rule_with_arg_substring_matches_anywhere_in_argument()
    {
        var policy = MakePolicy(rules:
        [
            new CommandDenyRule("no-mutating-api", "gh", ArgPatterns: ["api"], ArgContains: ["protection"]),
        ]);

        var result = policy.Check(Argv("gh", "api", "/repos/foo/bar/branches/main/protection"));

        result.IsDenied.Should().BeTrue();
        result.RuleName.Should().Be("no-mutating-api");
    }

    [Fact]
    public void Rule_arg_patterns_must_match_in_declared_order()
    {
        // Rule wants "push" then "--force" — passing them in reverse fails to match
        // (no "push" after the --force position).
        var policy = MakePolicy(rules:
        [
            new CommandDenyRule("ordered", "git", ArgPatterns: ["push", "--force"]),
        ]);

        // Reverse order — --force first then push: per the policy spec, --force is
        // matched at position 1, then "push" must come AFTER it. There is no "push"
        // after position 1, so the rule does NOT fire.
        var result = policy.Check(Argv("git", "--force", "push"));

        result.IsDenied.Should().BeFalse();
    }

    [Fact]
    public void Program_match_is_case_sensitive_ordinal()
    {
        var policy = MakePolicy(rules: [new CommandDenyRule("no-curl", "curl")]);

        // On Linux/macOS commands are case-sensitive; "CURL" must not match "curl".
        var result = policy.Check(Argv("CURL", "https://example.com"));

        result.IsDenied.Should().BeFalse();
    }

    // ── First-match wins ─────────────────────────────────────────────────────

    [Fact]
    public void First_matching_rule_wins_when_multiple_could_match()
    {
        var policy = MakePolicy(rules:
        [
            new CommandDenyRule("first",  "git", ArgPatterns: ["push"]),
            new CommandDenyRule("second", "git", ArgPatterns: ["push", "--force"]),
        ]);

        var result = policy.Check(Argv("git", "push", "--force"));

        result.IsDenied.Should().BeTrue();
        result.RuleName.Should().Be("first");
    }
}
