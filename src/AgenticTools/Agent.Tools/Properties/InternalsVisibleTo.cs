// PathValidator and the tool types are public; the test project still declares visibility for
// parity with the other extracted libraries, and DynamicProxyGenAssembly2 lets a mocking framework
// proxy any (future) internal seam.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agent.Tools.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2")]
