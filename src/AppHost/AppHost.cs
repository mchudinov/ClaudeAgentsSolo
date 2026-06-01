using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Dapr;

var builder = DistributedApplication.CreateBuilder(args);

// Redis resource that backs Dapr state for the DeveloperAgent.
// Step-7 (P2-A part 1/3) registers the resource and wires the 'web' project to
// reference it. Step-8 (P2-A part 2/3) layers a Dapr sidecar and a Dapr
// state-store component named "agent-state-store" on top of this Redis
// resource. The DeveloperAgent connection-string round-trip is finished in
// P2-A part 3/3.
var agentState = builder.AddRedis("agent-state");

// Dapr state-store component. The toolkit generates the redis state-store
// component dynamically; we inject the *Aspire-managed* 'agent-state' Redis
// endpoint (a dynamic host:port, NOT a hardcoded localhost:6379) via WithMetadata
// so the daprd sidecar talks to the same Redis the AppHost owns. We also set
// `actorStateStore: "true"`: Dapr Workflows run on the Dapr actor runtime, which
// requires the backing state store to enable it — otherwise workflow scheduling
// fails at runtime with "the state store is not configured to use the actor
// runtime". WaitFor links the sidecar startup to Redis health.
//
// redisPassword is intentionally omitted: Aspire's local `AddRedis` container
// runs without auth, matching the toolkit's redis state-store idiom.
var redisHost = agentState.Resource.PrimaryEndpoint.Property(EndpointProperty.Host);
var redisPort = agentState.Resource.PrimaryEndpoint.Property(EndpointProperty.Port);
var stateStore = builder
    .AddDaprStateStore("agent-state-store")
    .WithMetadata("redisHost", ReferenceExpression.Create($"{redisHost}:{redisPort}"))
    .WithMetadata("actorStateStore", "true")
    .WaitFor(agentState);

// Dapr Resiliency CRD (Step-26, P2-K). The YAML is published with the
// DeveloperAgent's content output; we hand a file path to the toolkit, which
// adds the containing folder to the sidecar's --resources-path so the daprd
// process loads it at startup alongside the state-store component.
var resiliencyYamlPath = ResolveDaprComponentPath("resiliency.yaml");
var resiliency = builder
    .AddDaprComponent(
        "resiliency-default",
        "resiliency",
        new DaprComponentOptions { LocalPath = resiliencyYamlPath });

builder.AddProject<Projects.DeveloperAgent>("DeveloperAgent")
    .WithReference(agentState)
    .WaitFor(agentState)
    .WithDaprSidecar(sidecar => sidecar
        .WithReference(stateStore)
        .WithReference(resiliency));

builder.Build().Run();

// Resolves the absolute path to a file under DeveloperAgent's dapr-components
// folder, regardless of whether the AppHost is launched from the repo root,
// the AppHost project folder, or via `dotnet run`. The folder is shipped to
// the DeveloperAgent output via its csproj content include; locally we point
// at the source-tree file so edits round-trip without rebuilding.
static string ResolveDaprComponentPath(string fileName)
{
    var sourcePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "DeveloperAgent", "dapr-components", fileName));
    return sourcePath;
}

// Expose the implicit top-level Program type so Aspire.Hosting.Testing can
// reach an accessible entry point from the DeveloperAgent.Tests assembly.
public partial class Program;
