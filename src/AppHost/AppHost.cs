var builder = DistributedApplication.CreateBuilder(args);

// Redis resource that backs Dapr state for the DeveloperAgent.
// Step-7 (P2-A part 1/3) only registers the resource and wires the 'web'
// project to reference it. The Dapr sidecar and the connection-string
// round-trip integration are added in subsequent steps (P2-A parts 2 and 3).
var agentState = builder.AddRedis("agent-state");

builder.AddProject<Projects.DeveloperAgent>("web")
    .WithReference(agentState)
    .WaitFor(agentState);

builder.Build().Run();

// Expose the implicit top-level Program type so Aspire.Hosting.Testing can
// reach an accessible entry point from the DeveloperAgent.Tests assembly.
public partial class Program;
