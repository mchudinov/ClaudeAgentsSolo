using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DeveloperAgent.Tests.AppHost;

/// <summary>
/// Model-inspection tests for the dynamically generated <c>agent-state-store</c>
/// Dapr state-store component. The component is no longer a static YAML file — the
/// AppHost generates it via <c>AddDaprStateStore("agent-state-store")</c> and injects
/// the Aspire-managed Redis endpoint plus the actor flag with <c>WithMetadata</c>.
///
/// These tests build the distributed-application model without starting any
/// container resources (no Docker required) — they only inspect the resource graph.
/// Unit-shaped: deliberately named <c>*Tests</c> (not <c>*IntegrationTests</c>) and
/// placed in the <c>.AppHost</c> namespace (not <c>.Integration</c>) so the
/// <see cref="DeveloperAgent.Tests.Conventions.IntegrationTraitConventionTests"/>
/// convention does not require an Integration trait.
///
/// Assertion seam: the toolkit stores <c>WithMetadata(name, value)</c> calls as
/// internal <c>DaprComponentConfigurationAnnotation</c>s whose <c>Configure</c>
/// delegate mutates an internal <c>DaprComponentSchema</c> at publish time — the
/// name/value pair is captured in a closure and is not exposed as annotation
/// properties, and the schema type itself is <c>internal</c> to the toolkit. To
/// read the value back we construct the schema and replay those delegates against
/// it via reflection (exactly what the toolkit does when emitting the component
/// YAML) and then assert against the materialized <c>Spec.Metadata</c> collection.
/// This reads the real configured value rather than merely checking for key
/// presence. The reflection is confined to this helper so the assertions stay
/// readable.
/// </summary>
public sealed class AgentStateStoreMetadataTests
{
    [Fact]
    public async Task State_store_component_enables_the_actor_state_store()
    {
        var metadata = await MaterializeStateStoreMetadataAsync();

        var actorEntry = metadata.SingleOrDefault(m => m.Name == "actorStateStore");

        actorEntry.Should().NotBeNull(
            because: "Dapr Workflows run on the actor runtime, which requires the " +
                     "backing state store to set actorStateStore=true; the AppHost " +
                     "injects it via WithMetadata(\"actorStateStore\", \"true\")");

        actorEntry!.Value.Should().Be("true",
            because: "the value must be the string \"true\" — Dapr fails workflow " +
                     "scheduling otherwise");
    }

    [Fact]
    public async Task State_store_component_binds_to_the_aspire_redis_endpoint()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var stateStore = model.Resources.Single(r => r.Name == "agent-state-store");

        // The redisHost is injected via WithMetadata(name, IValueProvider) so the
        // Aspire-managed Redis endpoint (a dynamic host:port) flows in at runtime.
        // The toolkit records this as a public DaprComponentValueProviderAnnotation
        // whose MetadataName is the metadata key — assert the component binds its
        // redisHost to a value provider rather than a hardcoded literal.
        var redisHostBinding = stateStore.Annotations
            .Where(a => a.GetType().Name == "DaprComponentValueProviderAnnotation")
            .Select(a => a.GetType().GetProperty("MetadataName")?.GetValue(a) as string)
            .Where(name => name is not null)
            .ToList();

        redisHostBinding.Should().Contain("redisHost",
            because: "the state store must bind redisHost to the Aspire-managed " +
                     "'agent-state' Redis endpoint (via WithMetadata(redisHost, " +
                     "ReferenceExpression)), not to a hardcoded localhost:6379");
    }

    /// <summary>
    /// Builds the AppHost model, locates the <c>agent-state-store</c> resource, and
    /// replays its <c>DaprComponentConfigurationAnnotation.Configure</c> delegates
    /// against a freshly constructed toolkit <c>DaprComponentSchema</c> (built by
    /// reflection because the type is internal to the toolkit) to materialize the
    /// metadata list the toolkit would emit into the component YAML.
    /// </summary>
    private static async Task<IReadOnlyList<(string Name, string? Value)>> MaterializeStateStoreMetadataAsync()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var stateStore = model.Resources.Single(r => r.Name == "agent-state-store");

        var configurationAnnotations = stateStore.Annotations
            .Where(a => a.GetType().Name == "DaprComponentConfigurationAnnotation")
            .ToList();

        configurationAnnotations.Should().NotBeEmpty(
            because: "WithMetadata(name, value) is recorded as a " +
                     "DaprComponentConfigurationAnnotation on the component resource");

        // The Configure delegates and the schema type both live in the toolkit
        // assembly; resolve the (internal) schema type from an already-loaded
        // toolkit annotation instance so this assertion needs no compile-time
        // reference to the internal type.
        var toolkitAssembly = configurationAnnotations[0].GetType().Assembly;
        var schemaType = toolkitAssembly.GetType("CommunityToolkit.Aspire.Hosting.Dapr.DaprComponentSchema")
            ?? throw new InvalidOperationException("Could not resolve internal DaprComponentSchema type.");
        var schema = Activator.CreateInstance(schemaType, "agent-state-store", "state.redis")!;

        foreach (var annotation in configurationAnnotations)
        {
            var configure = annotation.GetType().GetProperty("Configure")!.GetValue(annotation)!;
            var task = (Task)configure.GetType()
                .GetMethod("Invoke")!
                .Invoke(configure, new object?[] { schema, CancellationToken.None })!;
            await task;
        }

        var spec = schema.GetType().GetProperty("Spec")!.GetValue(schema)!;
        var metadataObj = (System.Collections.IEnumerable)spec.GetType()
            .GetProperty("Metadata")!.GetValue(spec)!;

        var result = new List<(string Name, string? Value)>();
        foreach (var entry in metadataObj)
        {
            var name = (string)entry.GetType().GetProperty("Name")!.GetValue(entry)!;
            var value = entry.GetType().GetProperty("Value")?.GetValue(entry) as string;
            result.Add((name, value));
        }

        return result;
    }

    private static async Task<DistributedApplication> BuildAppHostAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AppHost>();

        // BuildAsync (NOT StartAsync) — we only inspect the model; do not require Docker.
        return await builder.BuildAsync();
    }
}
