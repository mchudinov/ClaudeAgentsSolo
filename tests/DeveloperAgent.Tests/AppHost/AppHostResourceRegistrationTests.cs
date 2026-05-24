using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DeveloperAgent.Tests.AppHost;

/// <summary>
/// Model-inspection tests for the Aspire AppHost resource graph. These tests
/// build the distributed-application model without starting any container
/// resources (no Docker required) — they only assert that the AppHost wires
/// the expected resources and references.
///
/// Unit-shaped: do not add <c>[Trait("Category","Integration")]</c>. The
/// namespace deliberately ends in <c>.AppHost</c> (not <c>.Integration</c>)
/// and the class name ends in <c>Tests</c> (not <c>IntegrationTests</c>) so
/// the <see cref="DeveloperAgent.Tests.Conventions.IntegrationTraitConventionTests"/>
/// convention does not require an Integration trait.
/// </summary>
public sealed class AppHostResourceRegistrationTests
{
    [Fact]
    public async Task AppHost_registers_redis_resource_named_agent_state()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = model.Resources.SingleOrDefault(r => r.Name == "agent-state");

        resource.Should().NotBeNull(
            because: "Step-7 (P2-A part 1/3) requires a Redis resource named 'agent-state' " +
                     "to be registered in the AppHost model");

        resource.Should().BeAssignableTo<IResourceWithConnectionString>(
            because: "the Redis resource exposes a connection string that downstream " +
                     "projects will consume via WithReference");

        resource!.GetType().Name.Should().Be(
            "RedisResource",
            because: "the resource must be created via AddRedis so that subsequent steps " +
                     "(P2-A parts 2 and 3) can layer Dapr and connection-string consumption " +
                     "on a real Redis resource type from Aspire.Hosting.Redis");
    }

    [Fact]
    public async Task Web_project_references_agent_state_resource()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var web = model.Resources.SingleOrDefault(r => r.Name == "web");
        web.Should().NotBeNull(because: "the DeveloperAgent project must be registered as 'web'");

        var annotations = web!.Annotations.ToList();

        // The .WithReference(agentState) call adds an environment-callback annotation
        // whose state references the 'agent-state' resource by name or instance, and
        // the .WaitFor(agentState) call adds an annotation that holds a reference to
        // the awaited resource. We assert with a reflective scan so the test is robust
        // across Aspire 13.x annotation-type renames: at least one annotation on 'web'
        // must transitively reference the 'agent-state' resource.
        var referencesAgentState = annotations.Any(a => AnnotationReferencesResource(a, "agent-state"));

        referencesAgentState.Should().BeTrue(
            because: "the 'web' project must declare a reference to the 'agent-state' " +
                     "Redis resource (via WithReference and/or WaitFor) so that the Aspire " +
                     "orchestrator injects the connection string and orders startup. " +
                     "Annotation types present: {0}",
            string.Join(", ", annotations.Select(a => a.GetType().Name)));
    }

    [Fact]
    public async Task AppHost_registers_dapr_state_store_resource_named_agent_state_store()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = model.Resources.SingleOrDefault(r => r.Name == "agent-state-store");

        resource.Should().NotBeNull(
            because: "Step-8 (P2-A part 2/3) requires a Dapr state-store component named " +
                     "'agent-state-store' to be registered in the AppHost model via " +
                     "builder.AddDaprStateStore(\"agent-state-store\")");
    }

    [Fact]
    public async Task Dapr_state_store_resource_references_agent_state_redis()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var stateStore = model.Resources.SingleOrDefault(r => r.Name == "agent-state-store");
        stateStore.Should().NotBeNull(
            because: "the 'agent-state-store' Dapr state-store resource must be registered");

        var annotations = stateStore!.Annotations.ToList();

        // The state-store is wired to the Redis resource via .WaitFor(agentState),
        // which attaches a WaitAnnotation whose .Resource property is the agent-state
        // Redis resource. The reflective walker discovers it via that property.
        var referencesAgentState = annotations.Any(a => AnnotationReferencesResource(a, "agent-state"));

        referencesAgentState.Should().BeTrue(
            because: "the 'agent-state-store' Dapr component must declare a dependency on " +
                     "the 'agent-state' Redis resource (via WaitFor) so the sidecar starts " +
                     "only after Redis is healthy. Annotation types present: {0}",
            string.Join(", ", annotations.Select(a => a.GetType().Name)));
    }

    [Fact]
    public async Task Web_project_has_dapr_sidecar_annotation()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var web = model.Resources.SingleOrDefault(r => r.Name == "web");
        web.Should().NotBeNull(because: "the DeveloperAgent project must be registered as 'web'");

        var annotations = web!.Annotations.ToList();

        // .WithDaprSidecar(sidecar => sidecar.WithReference(stateStore)) wires a sidecar
        // onto 'web' and associates the 'agent-state-store' Dapr component with that
        // sidecar. The DaprSidecarAnnotation on 'web' references the IDaprSidecarResource,
        // which in turn carries a DaprComponentReferenceAnnotation pointing at the
        // state-store resource. The walker is given a generous depth so it can follow:
        //   web -> DaprSidecarAnnotation -> SidecarResource -> Annotations collection
        //        -> DaprComponentReferenceAnnotation -> Component -> "agent-state-store"
        var referencesStateStore = annotations.Any(a =>
            AnnotationReferencesResource(a, "agent-state-store", maxDepth: 6));

        referencesStateStore.Should().BeTrue(
            because: "the 'web' project must declare a Dapr sidecar that references the " +
                     "'agent-state-store' state-store component (via WithDaprSidecar + " +
                     "sidecar.WithReference(stateStore)). Annotation types present: {0}",
            string.Join(", ", annotations.Select(a => a.GetType().Name)));
    }

    [Fact]
    public async Task AppHost_registers_resiliency_component_named_resiliency_default()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = model.Resources.SingleOrDefault(r => r.Name == "resiliency-default");

        resource.Should().NotBeNull(
            because: "Step-26 (P2-K) requires a Dapr Resiliency component named " +
                     "'resiliency-default' to be registered in the AppHost model so the " +
                     "Dapr sidecar loads the resiliency.yaml CRD at startup");
    }

    [Fact]
    public async Task Web_dapr_sidecar_references_resiliency_default_component()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var web = model.Resources.SingleOrDefault(r => r.Name == "web");
        web.Should().NotBeNull(because: "the DeveloperAgent project must be registered as 'web'");

        var annotations = web!.Annotations.ToList();

        // Same walker pattern as the state-store assertion above — the sidecar
        // chain is: web -> DaprSidecarAnnotation -> SidecarResource -> Annotations
        // -> DaprComponentReferenceAnnotation -> Component(resiliency-default).
        var referencesResiliency = annotations.Any(a =>
            AnnotationReferencesResource(a, "resiliency-default", maxDepth: 6));

        referencesResiliency.Should().BeTrue(
            because: "the 'web' project's Dapr sidecar must reference the " +
                     "'resiliency-default' component (via WithDaprSidecar + " +
                     "sidecar.WithReference(resiliency)) so daprd loads the Resiliency " +
                     "CRD at startup. Annotation types present: {0}",
            string.Join(", ", annotations.Select(a => a.GetType().Name)));
    }

    private static async Task<DistributedApplication> BuildAppHostAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AppHost>();

        // BuildAsync (NOT StartAsync) — we only inspect the model; do not require Docker.
        return await builder.BuildAsync();
    }

    /// <summary>
    /// Returns true when the given annotation transitively references a resource
    /// with the supplied name. Walks the annotation's public properties via
    /// reflection and looks for an <see cref="IResource"/> whose
    /// <see cref="IResource.Name"/> matches. The default depth (3) is enough for
    /// simple <c>WaitFor</c>/<c>WithReference</c> wiring; pass a larger
    /// <paramref name="maxDepth"/> for chained wiring like
    /// <c>WithDaprSidecar(sidecar =&gt; sidecar.WithReference(component))</c>,
    /// which puts the target resource two levels deeper (web → sidecar →
    /// component annotation → component).
    /// </summary>
    private static bool AnnotationReferencesResource(
        IResourceAnnotation annotation,
        string resourceName,
        int maxDepth = 3)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return ReferencesResource(annotation, resourceName, seen, depth: 0, maxDepth);
    }

    private static bool ReferencesResource(object? value, string resourceName, HashSet<object> seen, int depth, int maxDepth)
    {
        if (value is null || depth > maxDepth)
        {
            return false;
        }

        if (!value.GetType().IsValueType && !seen.Add(value))
        {
            return false;
        }

        if (value is IResource resource && resource.Name == resourceName)
        {
            return true;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                if (ReferencesResource(item, resourceName, seen, depth + 1, maxDepth))
                {
                    return true;
                }
            }
        }

        // Walk public instance properties on annotation/state objects.
        foreach (var property in value.GetType().GetProperties(
                     System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (ReferencesResource(propertyValue, resourceName, seen, depth + 1, maxDepth))
            {
                return true;
            }
        }

        return false;
    }
}
