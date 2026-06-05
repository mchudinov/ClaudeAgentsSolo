using Agent.Runtime;

namespace Agent.Runtime.Tests;

public sealed class AnthropicHttpClientsTests
{
    [Fact]
    public void ChatClient_name_is_the_anthropic_contract_literal()
    {
        // This string is the key the host's egress allowlist (HostAllowlistHandler) and the Dapr
        // resiliency CRD match on. Renaming it would silently bypass egress filtering + resilience
        // tuning, so the literal is pinned here.
        AnthropicHttpClients.ChatClient.Should().Be("anthropic");
    }
}
