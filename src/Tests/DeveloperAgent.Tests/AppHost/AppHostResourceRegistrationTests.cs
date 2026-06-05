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
    public async Task AppHost_registers_dapr_state_store_resource_named_agent_state_store()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = ResourcesSnapshot(model).SingleOrDefault(r => r.Name == "agent-state-store");

        resource.Should().NotBeNull(
            because: "the Dapr state-store component named 'agent-state-store' must be " +
                     "registered in the AppHost model via builder.AddDaprComponent(" +
                     "\"agent-state-store\", \"state\", ...) so the daprd sidecar loads " +
                     "the declarative agent-state-store.yaml at startup");
    }

    [Fact]
    public async Task DeveloperAgent_project_has_dapr_sidecar_annotation()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var DeveloperAgent = ResourcesSnapshot(model).SingleOrDefault(r => r.Name == "DeveloperAgent");
        DeveloperAgent.Should().NotBeNull(because: "the DeveloperAgent project must be registered as 'DeveloperAgent'");

        var annotations = DeveloperAgent!.Annotations.ToList();

        // .WithDaprSidecar(sidecar => sidecar.WithReference(stateStore)) wires a sidecar
        // onto 'DeveloperAgent' and associates the 'agent-state-store' Dapr component with that
        // sidecar. The DaprSidecarAnnotation on 'DeveloperAgent' references the IDaprSidecarResource,
        // which in turn carries a DaprComponentReferenceAnnotation pointing at the
        // state-store resource. The walker is given a generous depth so it can follow:
        //   DeveloperAgent -> DaprSidecarAnnotation -> SidecarResource -> Annotations collection
        //        -> DaprComponentReferenceAnnotation -> Component -> "agent-state-store"
        var referencesStateStore = annotations.Any(a =>
            AnnotationReferencesResource(a, "agent-state-store", maxDepth: 6));

        referencesStateStore.Should().BeTrue(
            because: "the 'DeveloperAgent' project must declare a Dapr sidecar that references the " +
                     "'agent-state-store' state-store component (via WithDaprSidecar + " +
                     "sidecar.WithReference(stateStore)). Annotation types present: {0}",
            string.Join(", ", annotations.Select(a => a.GetType().Name)));
    }

    [Fact]
    public async Task AppHost_registers_resiliency_component_named_resiliency_default()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = ResourcesSnapshot(model).SingleOrDefault(r => r.Name == "resiliency-default");

        resource.Should().NotBeNull(
            because: "Step-26 (P2-K) requires a Dapr Resiliency component named " +
                     "'resiliency-default' to be registered in the AppHost model so the " +
                     "Dapr sidecar loads the resiliency.yaml CRD at startup");
    }

    [Fact]
    public async Task DeveloperAgent_dapr_sidecar_references_resiliency_default_component()
    {
        await using var app = await BuildAppHostAsync();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var DeveloperAgent = ResourcesSnapshot(model).SingleOrDefault(r => r.Name == "DeveloperAgent");
        DeveloperAgent.Should().NotBeNull(because: "the DeveloperAgent project must be registered as 'DeveloperAgent'");

        var annotations = DeveloperAgent!.Annotations.ToList();

        // Same walker pattern as the state-store assertion above — the sidecar
        // chain is: DeveloperAgent -> DaprSidecarAnnotation -> SidecarResource -> Annotations
        // -> DaprComponentReferenceAnnotation -> Component(resiliency-default).
        var referencesResiliency = annotations.Any(a =>
            AnnotationReferencesResource(a, "resiliency-default", maxDepth: 6));

        referencesResiliency.Should().BeTrue(
            because: "the 'DeveloperAgent' project's Dapr sidecar must reference the " +
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
    /// Returns a stable snapshot of <c>model.Resources</c>. Aspire's build pipeline
    /// hands back the <see cref="DistributedApplicationModel"/> while lifecycle hooks
    /// may still be appending resources on a background continuation, so enumerating
    /// <c>model.Resources</c> directly can throw "Collection was modified" mid-LINQ.
    /// Copying under a short retry yields a consistent list to assert against without
    /// weakening any assertion (the wiring under test is unchanged — only the read is
    /// made race-safe).
    /// </summary>
    private static IReadOnlyList<IResource> ResourcesSnapshot(DistributedApplicationModel model)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return model.Resources.ToList();
            }
            catch (InvalidOperationException) when (attempt < 50)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Returns true when the given annotation transitively references a resource
    /// with the supplied name. Walks the annotation's public properties via
    /// reflection and looks for an <see cref="IResource"/> whose
    /// <see cref="IResource.Name"/> matches. The default depth (3) is enough for
    /// simple <c>WaitFor</c>/<c>WithReference</c> wiring; pass a larger
    /// <paramref name="maxDepth"/> for chained wiring like
    /// <c>WithDaprSidecar(sidecar =&gt; sidecar.WithReference(component))</c>,
    /// which puts the target resource two levels deeper (DeveloperAgent → sidecar →
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
