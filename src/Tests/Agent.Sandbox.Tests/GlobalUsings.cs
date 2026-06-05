global using Xunit;
global using FluentAssertions;
global using NSubstitute;
// The types under test live in Agent.Sandbox; the path-deny seam + ITool/IToolContext/PathValidator/
// ToolResult it builds on live in Agent.Tools. Surfacing both unqualified keeps the moved tests close
// to their original form.
global using Agent.Tools;
global using Agent.Sandbox;
