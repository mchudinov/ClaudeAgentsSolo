using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Tests for <see cref="PathDenyPolicy"/>: matches a normalised absolute path against
/// the deny patterns (and optional secret-file regexes) from <see cref="SandboxOptions"/>.
/// </summary>
public sealed class PathDenyPolicyTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "deny-policy-tests", "repo");
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IPathDenyPolicy MakePolicy(
        IReadOnlyList<string>? denyPatterns = null,
        IReadOnlyList<string>? secretRegexes = null) =>
        new PathDenyPolicy(Options.Create(new SandboxOptions
        {
            DenyPathPatterns = denyPatterns ?? ProductionSandboxConfig.DenyPathPatterns,
            SecretFileRegexes = secretRegexes ?? [],
        }));

    private static string InsideRoot(params string[] segments)
    {
        var parts = new List<string> { Root };
        parts.AddRange(segments);
        return Path.GetFullPath(Path.Combine([.. parts]));
    }

    // ── Default deny rule: ~/.ssh/** ──────────────────────────────────────────

    [Fact]
    public void Path_under_user_ssh_dir_is_denied()
    {
        var policy = MakePolicy();
        var target = Path.Combine(Home, ".ssh", "id_rsa");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeTrue();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Path_under_nested_user_ssh_dir_is_denied()
    {
        var policy = MakePolicy();
        var target = Path.Combine(Home, ".ssh", "keys", "deploy_key");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeTrue();
    }

    // ── Default deny rule: .env* (basename glob) ──────────────────────────────

    [Fact]
    public void File_named_dotenv_in_workspace_is_denied()
    {
        var policy = MakePolicy();
        var target = InsideRoot(".env");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeTrue();
    }

    [Fact]
    public void File_named_dotenv_local_is_denied()
    {
        var policy = MakePolicy();
        var target = InsideRoot(".env.local");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeTrue();
    }

    [Fact]
    public void File_with_env_in_name_but_not_starting_with_dotenv_is_allowed()
    {
        var policy = MakePolicy();
        // appsettings.env.json should NOT match `.env*` — `.env*` is a basename glob
        // that matches files whose basename starts with `.env`.
        var target = InsideRoot("src", "appsettings.env.json");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeFalse();
    }

    // ── Default deny rule: .git/config ────────────────────────────────────────

    [Fact]
    public void Path_to_git_config_in_workspace_is_denied()
    {
        var policy = MakePolicy();
        var target = InsideRoot(".git", "config");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeTrue();
    }

    // ── Workspace-escape: computed, not pattern-listed ────────────────────────

    [Fact]
    public void Absolute_path_outside_workspace_root_is_denied()
    {
        var policy = MakePolicy(denyPatterns: []); // even with NO patterns, escape is denied
        var outside = Path.GetTempPath(); // entirely different tree

        var result = policy.Check(outside, Root);

        result.IsDenied.Should().BeTrue();
        result.Reason.Should().Contain("workspace");
    }

    [Fact]
    public void Sibling_directory_sharing_prefix_is_denied()
    {
        var policy = MakePolicy(denyPatterns: []);
        var sibling = Root + "-evil" + Path.DirectorySeparatorChar + "secret";

        var result = policy.Check(sibling, Root);

        result.IsDenied.Should().BeTrue();
    }

    [Fact]
    public void Path_inside_workspace_root_with_no_pattern_match_is_allowed()
    {
        var policy = MakePolicy(denyPatterns: []);
        var target = InsideRoot("src", "Foo.cs");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeFalse();
    }

    // ── Custom secret-file regexes ────────────────────────────────────────────

    [Fact]
    public void Custom_secret_regex_blocks_matching_path()
    {
        var policy = MakePolicy(
            denyPatterns: [],
            secretRegexes: [@"secret\.txt$"]);
        var target = InsideRoot("config", "secret.txt");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeTrue();
    }

    [Fact]
    public void Custom_secret_regex_does_not_block_non_matching_path()
    {
        var policy = MakePolicy(
            denyPatterns: [],
            secretRegexes: [@"secret\.txt$"]);
        var target = InsideRoot("config", "public.txt");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeFalse();
    }

    // ── Allowed path ──────────────────────────────────────────────────────────

    [Fact]
    public void Regular_workspace_file_under_defaults_is_allowed()
    {
        var policy = MakePolicy(); // default deny patterns
        var target = InsideRoot("src", "Program.cs");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeFalse();
        result.Reason.Should().BeNull();
    }

    // ── Reason content (helps the model self-correct) ─────────────────────────

    [Fact]
    public void Denied_result_reason_describes_the_violation()
    {
        var policy = MakePolicy();
        var target = Path.Combine(Home, ".ssh", "id_rsa");

        var result = policy.Check(target, Root);

        result.IsDenied.Should().BeTrue();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }
}
