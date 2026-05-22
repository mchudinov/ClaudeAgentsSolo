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
    /// <see cref="IResource.Name"/> matches.
    /// </summary>
    private static bool AnnotationReferencesResource(IResourceAnnotation annotation, string resourceName)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return ReferencesResource(annotation, resourceName, seen, depth: 0);
    }

    private static bool ReferencesResource(object? value, string resourceName, HashSet<object> seen, int depth)
    {
        if (value is null || depth > 3)
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
                if (ReferencesResource(item, resourceName, seen, depth + 1))
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

            if (ReferencesResource(propertyValue, resourceName, seen, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
