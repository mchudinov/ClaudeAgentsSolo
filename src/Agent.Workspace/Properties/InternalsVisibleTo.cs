// GitClient exposes internal static helpers (BuildGitCommandLine — the git-over-HTTPS Basic-auth
// header builder; ParseNumstat — the diff --numstat parser) that the library's own test project
// pins directly. Expose them to Agent.Workspace.Tests, mirroring the other extracted libraries.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agent.Workspace.Tests")]
