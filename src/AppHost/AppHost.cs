using CommunityToolkit.Aspire.Hosting.Dapr;

var builder = DistributedApplication.CreateBuilder(args);

// Reuse the already-running dapr_redis (dapr init) on localhost:6379. The
// state-store component is declared in agent-state-store.yaml (actorStateStore=true,
// required because Dapr Workflow runs on the Dapr actor runtime), so Aspire does
// not manage a separate Redis resource.
var stateStoreYamlPath = ResolveDaprComponentPath("agent-state-store.yaml");
var stateStore = builder.AddDaprComponent(
    "agent-state-store", "state",
    new DaprComponentOptions { LocalPath = stateStoreYamlPath });

// Dapr Resiliency CRD (Step-26, P2-K). The YAML is published with the
// AppHost's content output; we hand a file path to the toolkit, which
// adds the containing folder to the sidecar's --resources-path so the daprd
// process loads it at startup alongside the state-store component.
var resiliencyYamlPath = ResolveDaprComponentPath("resiliency.yaml");
var resiliency = builder.AddDaprComponent(
    "resiliency-default", "resiliency",
    new DaprComponentOptions { LocalPath = resiliencyYamlPath });

builder.AddProject<Projects.DeveloperAgent>("DeveloperAgent")
    .WithDaprSidecar(sidecar => sidecar
        .WithReference(stateStore)
        .WithReference(resiliency));

builder.AddContainer("redisinsight", "redis/redisinsight")
    .WithImageTag("latest")
    .WithHttpEndpoint(port: 5540, targetPort: 5540, name: "http")
    .WithEnvironment("RI_REDIS_HOST", "host.docker.internal")
    .WithEnvironment("RI_REDIS_PORT", "6379")
    .WithEnvironment("RI_REDIS_USERNAME", "")
    .WithEnvironment("RI_REDIS_PASSWORD", "")
    .WithEnvironment("RI_REDIS_ALIAS", "Local Redis")
    .WithEnvironment("RI_ACCEPT_TERMS_AND_CONDITIONS", "true");        

builder.Build().Run();

// Resolves the absolute path to a file under AppHost's dapr/components
// folder, regardless of whether the AppHost is launched from the repo root,
// the AppHost project folder, or via `dotnet run`. The folder is shipped to
// the AppHost output via its csproj content include; locally we point at the
// source-tree file (three levels up from bin/<cfg>/<tfm> to the project root)
// so edits round-trip without rebuilding.
static string ResolveDaprComponentPath(string fileName)
{
    var sourcePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "dapr", "components", fileName));
    return sourcePath;
}

// Expose the implicit top-level Program type so Aspire.Hosting.Testing can
// reach an accessible entry point from the DeveloperAgent.Tests assembly.
public partial class Program;
