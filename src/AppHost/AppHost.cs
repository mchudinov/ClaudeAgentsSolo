var builder = DistributedApplication.CreateBuilder(args);

// Redis resource that backs Dapr state for the DeveloperAgent.
// Step-7 (P2-A part 1/3) registers the resource and wires the 'web' project to
// reference it. Step-8 (P2-A part 2/3) layers a Dapr sidecar and a Dapr
// state-store component named "agent-state-store" on top of this Redis
// resource. The DeveloperAgent connection-string round-trip is finished in
// P2-A part 3/3.
var agentState = builder.AddRedis("agent-state");

// Dapr state-store component, programmatically registered via the
// CommunityToolkit hosting integration. The toolkit chooses the concrete
// component type at run-/deploy-time; we link it to the Redis resource via
// WaitFor so the sidecar only starts once Redis is healthy.
var stateStore = builder
    .AddDaprStateStore("agent-state-store")
    .WaitFor(agentState);

builder.AddProject<Projects.DeveloperAgent>("web")
    .WithReference(agentState)
    .WaitFor(agentState)
    .WithDaprSidecar(sidecar => sidecar.WithReference(stateStore));

builder.Build().Run();

// Expose the implicit top-level Program type so Aspire.Hosting.Testing can
// reach an accessible entry point from the DeveloperAgent.Tests assembly.
public partial class Program;
