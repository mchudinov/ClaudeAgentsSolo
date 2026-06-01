using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace DeveloperAgent.Tests.DaprComponents;

/// <summary>
/// Well-formedness tests for the declarative Dapr state-store component shipped with
/// the DeveloperAgent (<c>src/DeveloperAgent/dapr-components/agent-state-store.yaml</c>).
/// These tests do not require a live Dapr runtime or Docker — they parse the YAML
/// offline and assert the schema keys and the metadata the component must declare.
///
/// The component reuses the already-running <c>dapr_redis</c> container from
/// <c>dapr init</c> (localhost:6379) instead of an Aspire-managed Redis resource.
///
/// Unit-shaped: deliberately named <c>*Tests</c> (not <c>*IntegrationTests</c>) and
/// placed in the <c>.DaprComponents</c> namespace (not <c>.Integration</c>) so the
/// <see cref="DeveloperAgent.Tests.Conventions.IntegrationTraitConventionTests"/>
/// convention does not require an Integration trait.
/// </summary>
public sealed class AgentStateStoreYamlTests
{
    private static YamlMappingNode LoadRoot()
    {
        var path = AgentStateStoreYamlPath.Resolve();
        File.Exists(path).Should().BeTrue(
            because: $"the Dapr state-store YAML must exist at {path}");

        using var reader = File.OpenText(path);
        var stream = new YamlStream();
        stream.Load(reader);

        stream.Documents.Should().NotBeEmpty();
        var root = stream.Documents[0].RootNode;
        root.Should().BeOfType<YamlMappingNode>();
        return (YamlMappingNode)root;
    }

    [Fact]
    public void Yaml_top_level_keys_match_Dapr_Component_CRD_schema()
    {
        var root = LoadRoot();

        root["apiVersion"].ToString().Should().Be("dapr.io/v1alpha1",
            because: "the declarative component targets the v1alpha1 Component CRD");
        root["kind"].ToString().Should().Be("Component");

        var metadata = (YamlMappingNode)root["metadata"];
        metadata["name"].ToString().Should().Be("agent-state-store",
            because: "the component name must match the *.StateStoreName constants, the " +
                     "actor/workflow runtime, and resiliency.yaml's targets.components");

        var spec = (YamlMappingNode)root["spec"];
        spec["type"].ToString().Should().Be("state.redis");
        spec["version"].ToString().Should().Be("v1");
    }

    [Fact]
    public void Spec_metadata_enables_the_actor_state_store()
    {
        var entries = SpecMetadataEntries();

        entries.Should().ContainSingle(e => e.Name == "actorStateStore")
            .Which.Value.Should().Be("true",
                because: "Dapr Workflow runs on the Dapr actor runtime, which requires " +
                         "the backing state store to set actorStateStore=true");
    }

    [Fact]
    public void Spec_metadata_targets_the_dapr_init_redis_on_localhost()
    {
        var entries = SpecMetadataEntries();

        entries.Should().ContainSingle(e => e.Name == "redisHost")
            .Which.Value.Should().Be("localhost:6379",
                because: "the component reuses the already-running dapr_redis container " +
                         "(dapr init) on localhost:6379, not an Aspire-managed Redis");
    }

    [Fact]
    public void Yaml_has_no_top_level_auth_secret_store_block()
    {
        var root = LoadRoot();

        root.Children.Keys.Select(k => k.ToString()).Should().NotContain("auth",
            because: "there is no local k8s secret store; an auth.secretStore block " +
                     "would make daprd fail to load the component in self-hosted mode");
    }

    private static IReadOnlyList<(string Name, string Value)> SpecMetadataEntries()
    {
        var spec = (YamlMappingNode)LoadRoot()["spec"];
        var metadata = (YamlSequenceNode)spec["metadata"];

        return metadata
            .Cast<YamlMappingNode>()
            .Select(entry => (
                Name: entry["name"].ToString(),
                Value: entry.Children.ContainsKey(new YamlScalarNode("value"))
                    ? entry["value"].ToString()
                    : string.Empty))
            .ToList();
    }

    /// <summary>
    /// Locates the state-store YAML on disk by walking up from the test assembly
    /// directory to the repo root, then descending into the source tree. Reads the
    /// source-tree copy so the test catches edits even without a consuming rebuild.
    /// </summary>
    private static class AgentStateStoreYamlPath
    {
        public static string Resolve()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "src", "DeveloperAgent", "dapr-components", "agent-state-store.yaml");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException(
                "Could not locate src/DeveloperAgent/dapr-components/agent-state-store.yaml by walking up from " +
                AppContext.BaseDirectory);
        }
    }
}
