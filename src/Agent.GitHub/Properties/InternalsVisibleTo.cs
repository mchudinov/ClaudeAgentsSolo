// The transports (IGraphQLTransport/IRestTransport + Octokit wrappers) and GitHubProjectsClient
// are internal implementation details. Tests construct/mock them directly:
//  - Agent.GitHub.Tests — the generic-mechanics tests for this library.
//  - DeveloperAgent.Tests — host-side tests that exercise the transports through the real
//    egress/resilience wiring (OctokitEgressWiringTests) and the end-to-end integration tests.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agent.GitHub.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DeveloperAgent.Tests")]
// Required by NSubstitute (Castle.DynamicProxy) to mock the internal transport interfaces.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2")]
