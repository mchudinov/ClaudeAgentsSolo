using System.Text;
using Agent.Workspace;
using FluentAssertions;

namespace Agent.Workspace.Tests;

/// <summary>
/// Guards the HTTP authentication scheme <see cref="GitClient"/> injects into git
/// clone/push command lines for HTTPS remotes.
/// </summary>
/// <remarks>
/// GitHub's git-over-HTTPS smart transport authenticates with HTTP <b>Basic</b> auth —
/// the token supplied as the password under the <c>x-access-token</c> username (the
/// scheme <c>actions/checkout</c> uses). It does <b>not</b> accept <c>Bearer</c>, which
/// is only valid for the REST / GraphQL API; a <c>Bearer</c> header makes GitHub answer
/// <c>HTTP 401 — remote: invalid credentials</c> and the clone fails with exit 128. These
/// tests pin the Basic scheme so the regression cannot silently return.
/// </remarks>
public sealed class GitClientAuthHeaderTests
{
    private static string ExpectedBasic(string token)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));

    [Fact]
    public void Https_url_with_token_uses_Basic_auth_with_x_access_token_username()
    {
        const string token = "ghp_exampletoken1234567890";

        var line = GitClient.BuildGitCommandLine(
            "https://github.com/mchudinov/TicTacToe2.git",
            "clone \"https://github.com/mchudinov/TicTacToe2.git\" repo",
            token);

        line.Should().Contain($"http.extraheader=\"Authorization: Basic {ExpectedBasic(token)}\"");
    }

    [Fact]
    public void Https_url_with_token_never_uses_Bearer()
    {
        const string token = "ghp_exampletoken1234567890";

        var line = GitClient.BuildGitCommandLine(
            "https://github.com/mchudinov/TicTacToe2.git",
            "clone \"https://github.com/mchudinov/TicTacToe2.git\" repo",
            token);

        // Regression guard: Bearer yields "invalid credentials" (HTTP 401) on git-over-HTTPS.
        line.Should().NotContain("Bearer",
            because: "GitHub git-over-HTTPS rejects Bearer tokens; only Basic auth is accepted");
    }

    [Fact]
    public void Http_url_with_token_also_uses_Basic_auth()
    {
        const string token = "ghp_exampletoken1234567890";

        var line = GitClient.BuildGitCommandLine(
            "http://example.com/repo.git",
            "clone \"http://example.com/repo.git\" repo",
            token);

        line.Should().Contain($"Authorization: Basic {ExpectedBasic(token)}");
    }

    [Fact]
    public void File_path_url_omits_the_auth_header()
    {
        // Local file-system remotes (used by the integration tests) need no credentials.
        var line = GitClient.BuildGitCommandLine(
            "/tmp/some/upstream.git",
            "clone \"/tmp/some/upstream.git\" repo",
            "ghp_exampletoken1234567890");

        line.Should().Be("git clone \"/tmp/some/upstream.git\" repo");
        line.Should().NotContain("http.extraheader");
    }

    [Fact]
    public void Https_url_without_token_omits_the_auth_header()
    {
        var lineNull = GitClient.BuildGitCommandLine(
            "https://github.com/mchudinov/TicTacToe2.git",
            "clone \"https://github.com/mchudinov/TicTacToe2.git\" repo",
            token: null);
        var lineEmpty = GitClient.BuildGitCommandLine(
            "https://github.com/mchudinov/TicTacToe2.git",
            "clone \"https://github.com/mchudinov/TicTacToe2.git\" repo",
            token: "");

        lineNull.Should().NotContain("http.extraheader");
        lineEmpty.Should().NotContain("http.extraheader");
    }
}
