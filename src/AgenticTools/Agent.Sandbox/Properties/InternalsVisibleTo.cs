// CommandSandbox/DockerContainerRuntime have internal ctors (IProcessRunner is an internal
// abstraction) and HostAllowlistHandler exposes an internal explicit-list ctor for tests.
// Expose them to the library's own test project; DynamicProxyGenAssembly2 lets NSubstitute
// proxy the internal IProcessRunner seam.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agent.Sandbox.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2")]
