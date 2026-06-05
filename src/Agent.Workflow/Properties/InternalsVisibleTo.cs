// DaprWorkflowInstanceInspector is internal — its public seam is IWorkflowInstanceInspector.
// The library's own test project exercises the internal concrete; DynamicProxyGenAssembly2
// lets a mocking framework proxy the Dapr client interface it depends on.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agent.Workflow.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2")]
