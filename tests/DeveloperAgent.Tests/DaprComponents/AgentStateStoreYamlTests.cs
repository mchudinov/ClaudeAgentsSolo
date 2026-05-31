using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace DeveloperAgent.Tests.DaprComponents;

/// <summary>
/// Well-formedness tests for the Dapr state-store component shipped with the
/// DeveloperAgent (<c>src/DeveloperAgent/dapr-components/agent-state-store.yaml</c>).
/// These tests do not require a live Dapr runtime — they parse the YAML offline
/// and assert the schema keys plus the <c>actorStateStore: "true"</c> metadata
/// entry that Dapr Workflows (which run on the actor runtime) require. Without
/// that entry, scheduling a workflow fails at runtime with "the state store is
/// not configured to use the actor runtime".
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

        root["apiVersion"].ToString().Should().Be("dapr.io/v1alpha1");
        root["kind"].ToString().Should().Be("Component");

        var metadata = (YamlMappingNode)root["metadata"];
        metadata["name"].ToString().Should().Be("agent-state-store",
            because: "resiliency.yaml and AppHostResourceRegistrationTests both " +
                     "target the component name 'agent-state-store'");
    }

    [Fact]
    public void Spec_declares_a_v1_redis_state_store()
    {
        var spec = (YamlMappingNode)LoadRoot()["spec"];
        spec["type"].ToString().Should().Be("state.redis");
        spec["version"].ToString().Should().Be("v1");
    }

    [Fact]
    public void Metadata_enables_the_actor_state_store()
    {
        var spec = (YamlMappingNode)LoadRoot()["spec"];
        var metadata = (YamlSequenceNode)spec["metadata"];

        var actorEntry = metadata
            .Children
            .OfType<YamlMappingNode>()
            .SingleOrDefault(entry =>
                entry.Children.TryGetValue(new YamlScalarNode("name"), out var name) &&
                name.ToString() == "actorStateStore");

        actorEntry.Should().NotBeNull(
            because: "Dapr Workflows run on the actor runtime, which requires the " +
                     "backing state store to set actorStateStore=true");

        actorEntry!["value"].ToString().Should().Be("true",
            because: "the value must be the string \"true\" — Dapr fails workflow " +
                     "scheduling otherwise");
    }

    [Fact]
    public void Metadata_declares_a_redis_host()
    {
        var spec = (YamlMappingNode)LoadRoot()["spec"];
        var metadata = (YamlSequenceNode)spec["metadata"];

        var names = metadata
            .Children
            .OfType<YamlMappingNode>()
            .Where(entry => entry.Children.ContainsKey(new YamlScalarNode("name")))
            .Select(entry => entry["name"].ToString())
            .ToList();

        names.Should().Contain("redisHost",
            because: "the Redis state store needs a host endpoint to connect to");
    }

    /// <summary>
    /// Locates the state-store YAML on disk by walking up from the test assembly
    /// directory to the repo root, then descending into the source tree. Mirrors
    /// the resolution strategy in ResiliencyYamlTests so the test reads the
    /// source-tree copy and catches edits even without a rebuild.
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
