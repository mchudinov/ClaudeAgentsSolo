using System.Text.RegularExpressions;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace DeveloperAgent.Tests.Resilience;

/// <summary>
/// Well-formedness tests for the Dapr Resiliency CRD shipped with the AppHost
/// (<c>src/AppHost/dapr/components/resiliency.yaml</c>). These tests do not
/// require a live Dapr runtime — they parse the YAML offline and assert the schema
/// keys, the named policies, and the numeric ranges Step-26 (P2-K) committed to.
/// </summary>
public sealed class ResiliencyYamlTests
{
    private static YamlMappingNode LoadRoot()
    {
        var path = ResiliencyYamlPath.Resolve();
        File.Exists(path).Should().BeTrue(
            because: $"the Dapr Resiliency YAML must exist at {path}");

        using var reader = File.OpenText(path);
        var stream = new YamlStream();
        stream.Load(reader);

        stream.Documents.Should().NotBeEmpty();
        var root = stream.Documents[0].RootNode;
        root.Should().BeOfType<YamlMappingNode>();
        return (YamlMappingNode)root;
    }

    [Fact]
    public void Yaml_top_level_keys_match_Dapr_Resiliency_CRD_schema()
    {
        var root = LoadRoot();

        // apiVersion + kind + metadata.name + spec
        root["apiVersion"].ToString().Should().Be("dapr.io/v1alpha1",
            because: "Step-26 targets the v1alpha1 Resiliency CRD documented at " +
                     "https://docs.dapr.io/operations/resiliency/policies/");
        root["kind"].ToString().Should().Be("Resiliency");

        var metadata = (YamlMappingNode)root["metadata"];
        metadata["name"].ToString().Should().Be("resiliency-default");

        var spec = (YamlMappingNode)root["spec"];
        spec.Children.Keys.Select(k => k.ToString())
            .Should().Contain(new[] { "policies", "targets" });
    }

    [Fact]
    public void Spec_policies_contains_timeouts_retries_and_circuitBreakers()
    {
        var spec = (YamlMappingNode)LoadRoot()["spec"];
        var policies = (YamlMappingNode)spec["policies"];

        policies.Children.Keys.Select(k => k.ToString())
            .Should().BeEquivalentTo(
                new[] { "timeouts", "retries", "circuitBreakers" },
                "Dapr Resiliency v1alpha1 supports exactly these three " +
                "policy categories under spec.policies");
    }

    [Theory]
    [InlineData("anthropic-timeout", "2m")]
    [InlineData("github-timeout", "30s")]
    [InlineData("mcp-timeout", "30s")]
    [InlineData("state-timeout", "5s")]
    public void Each_named_timeout_policy_is_present_with_expected_duration(string name, string expected)
    {
        var timeouts = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)LoadRoot()["spec"])["policies"])["timeouts"];
        timeouts.Children.Keys.Select(k => k.ToString()).Should().Contain(name);
        timeouts[name].ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("anthropic-default")]
    [InlineData("github-default")]
    [InlineData("mcp-default")]
    [InlineData("state-default")]
    public void Each_named_retry_policy_is_present_with_exponential_policy_and_bounded_retries(string name)
    {
        var retries = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)LoadRoot()["spec"])["policies"])["retries"];

        retries.Children.Keys.Select(k => k.ToString()).Should().Contain(name);

        var policy = (YamlMappingNode)retries[name];
        policy["policy"].ToString().Should().Be("exponential",
            because: "all retry policies use exponential back-off to avoid the " +
                     "thundering-herd problem when an upstream recovers");

        var maxRetries = int.Parse(policy["maxRetries"].ToString());
        maxRetries.Should().BeInRange(1, 10,
            because: "maxRetries must be > 0 (retries are pointless otherwise) and " +
                     "<= 10 (anything larger risks compounding latency unacceptably)");
    }

    [Theory]
    [InlineData("anthropic-default")]
    [InlineData("github-default")]
    [InlineData("mcp-default")]
    [InlineData("state-default")]
    public void Each_named_circuit_breaker_policy_has_required_keys(string name)
    {
        var breakers = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)LoadRoot()["spec"])["policies"])["circuitBreakers"];

        breakers.Children.Keys.Select(k => k.ToString()).Should().Contain(name);

        var policy = (YamlMappingNode)breakers[name];
        var keys = policy.Children.Keys.Select(k => k.ToString()).ToList();
        keys.Should().Contain("maxRequests");
        keys.Should().Contain("interval");
        keys.Should().Contain("timeout");
        keys.Should().Contain("trip",
            because: "Dapr circuit breakers require a trip condition (CEL expression)");

        policy["trip"].ToString().Should().Match("consecutiveFailures *>=*",
            because: "all of the Step-26 circuit breakers trip on a consecutive-failure " +
                     "threshold; verify the expression shape is what Dapr expects");
    }

    [Fact]
    public void State_store_component_target_is_wired_to_state_default_policies()
    {
        var spec = (YamlMappingNode)LoadRoot()["spec"];
        var targets = (YamlMappingNode)spec["targets"];

        var components = (YamlMappingNode)targets["components"];
        components.Children.Keys.Select(k => k.ToString())
            .Should().Contain("agent-state-store",
                because: "the agent-state-store Dapr component (Step-8) is the one " +
                         "outbound-Dapr target this PR governs with the state-default " +
                         "policies");

        var stateStore = (YamlMappingNode)components["agent-state-store"];
        var outbound = (YamlMappingNode)stateStore["outbound"];
        outbound["timeout"].ToString().Should().Be("state-timeout");
        outbound["retry"].ToString().Should().Be("state-default");
        outbound["circuitBreaker"].ToString().Should().Be("state-default");
    }

    [Fact]
    public void Apps_target_block_is_present_even_when_empty()
    {
        // Future MCP-backed apps (Step-17) will opt into mcp-default by being
        // added under targets.apps. The block must exist so that addition is
        // a one-line edit instead of a schema change.
        var targets = (YamlMappingNode)((YamlMappingNode)LoadRoot()["spec"])["targets"];
        targets.Children.Keys.Select(k => k.ToString()).Should().Contain("apps");
    }

    [Fact]
    public void Apps_target_block_currently_carries_no_targets_so_Mcp_default_is_an_orphan_policy()
    {
        // Per the Step-26 scope the mcp-default policy is *defined* but not wired
        // to any concrete app. Step-17 (MCP wiring) is responsible for filling
        // the targets.apps block when it adds the GitHub MCP / Context7 MCP apps.
        var targets = (YamlMappingNode)((YamlMappingNode)LoadRoot()["spec"])["targets"];
        var apps = targets["apps"];

        if (apps is YamlMappingNode mapping)
        {
            mapping.Children.Should().BeEmpty(
                because: "apps targets are populated by Step-17, not by Step-26");
        }
        // Some YAML serializers parse `apps: {}` as a scalar node depending on
        // versions; either representation is acceptable as long as no children exist.
    }

    /// <summary>
    /// Locates the resiliency YAML on disk by walking up from the test assembly
    /// directory to the repo root, then descending into the source tree. The file
    /// is *also* copied to bin\Debug via the AppHost csproj content include, but we
    /// deliberately read the source-tree copy so the test catches edits made to
    /// the YAML even if the consuming project has not been rebuilt.
    /// </summary>
    private static class ResiliencyYamlPath
    {
        public static string Resolve()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "src", "AppHost", "dapr", "components", "resiliency.yaml");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException(
                "Could not locate src/AppHost/dapr/components/resiliency.yaml by walking up from " +
                AppContext.BaseDirectory);
        }
    }
}
