using DeveloperAgent.Configuration;
using FluentAssertions;

namespace DeveloperAgent.Tests.Configuration;

public sealed class SecretsBundleGitHubTokenProviderTests
{
    [Fact]
    public void GetToken_returns_the_bundles_GitHub_token()
    {
        var provider = new SecretsBundleGitHubTokenProvider(
            new SecretsBundle(AnthropicApiKey: "sk-anthropic", GitHubToken: "ghp_github"));

        provider.GetToken().Should().Be("ghp_github");
    }
}
