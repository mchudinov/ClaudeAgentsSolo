// All of Agent.Runtime's types are public (the factory, the cap decorator and its exceptions,
// the persona loader, the counter DTO, the DI extension). The test project still declares
// visibility for parity with the other extracted libraries, and DynamicProxyGenAssembly2 lets
// a mocking framework proxy any (future) internal seam.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agent.Runtime.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2")]
