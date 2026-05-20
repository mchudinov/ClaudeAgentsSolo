# 01 — Configuration & Process Shape

**Status:** draft — 2026-05-20
**Depends on:** nothing. Unblocks all other phase-1 plans.

## Purpose

Replace the Azure OpenAI placeholder with the LLD's real configuration surface, and define the `DeveloperAgent` process: stays on `Microsoft.NET.Sdk.Web`, adds an `IHostedService` that runs the agent loop alongside Kestrel. The Razor/MudBlazor scaffold stays untouched but unused — it will host the phase-2 operator dashboard.

## Deliverables

| Path | Change |
| ---- | ------ |
| `src/DeveloperAgent/Settings.cs` | Rewrite. Replace `Web.Settings`/`Web.AzureOpenAI` with `DeveloperAgent.Configuration.*` records matching the LLD schema. |
| `src/DeveloperAgent/Program.cs` | Move from `namespace Web` to `namespace DeveloperAgent`. Drop Azure OpenAI client registration. Register the new options classes + the future agent services (placeholders here, real registration lands as plans 02–05 land). Keep Serilog, OTel, MudBlazor, the Razor mapping, and the `/livez` `/uptime` `/info` endpoints from `Library.Extensions`. |
| `src/DeveloperAgent/appsettings.json` | Rewrite to the schema below. No secrets — only references to secret names. |
| `src/DeveloperAgent/appsettings.Development.json` | New file (gitignored — `.gitignore` already excludes `appsettings.Development*.json`). Holds local-only overrides for dev. |
| `src/DeveloperAgent/DeveloperAgent.csproj` | Remove `Azure.AI.OpenAI` PackageReference. Add `Anthropic.SDK` (latest, see plan 04 for the version pin). Keep MudBlazor and the Razor framework references. |

## Public surface

Options classes are bound via `IOptions<T>` and registered with `builder.Services.Configure<T>(builder.Configuration.GetSection("..."))`. All records are `sealed` and use `init` setters so they can be deserialized from configuration but not mutated at runtime.

```csharp
namespace DeveloperAgent.Configuration;

public sealed record AgentOptions
{
    public string Name { get; init; } = "DeveloperAgent";
    public string Model { get; init; } = "claude-opus-4-7";
    public string Effort { get; init; } = "xhigh";              // xhigh | high | medium | low
    public string PersonaPath { get; init; } = "/personas/developer.md";
    public int PollIntervalSeconds { get; init; } = 60;
    public int ReviewPollIntervalSeconds { get; init; } = 60;
    public int MaxModelTurnsHardCap { get; init; } = 40;        // safety net only; full scope limits = phase 2
}

public sealed record AnthropicOptions
{
    public string ApiKeySecretName { get; init; } = "anthropic-api-key";   // dev: user-secrets key; prod: env var or secret store
}

public sealed record GitHubOptions
{
    public string Owner { get; init; } = "";
    public RepositoryOptions Repository { get; init; } = new();
    public ProjectOptions Project { get; init; } = new();
    public ProjectStateNames States { get; init; } = new();
    public string TokenSecretName { get; init; } = "github-token";
}

public sealed record RepositoryOptions
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string DefaultBranch { get; init; } = "main";
}

public sealed record ProjectOptions
{
    public string Name { get; init; } = "";
    public int Number { get; init; }
    public string OwnerType { get; init; } = "Organization";   // Organization | User
}

public sealed record ProjectStateNames
{
    public string Ready { get; init; } = "Ready";
    public string InProgress { get; init; } = "In Progress";
    public string InReview { get; init; } = "In Review";
    public string Done { get; init; } = "Done";
}

public sealed record WorkspaceOptions
{
    public string RootPath { get; init; } = "/workspace";
    public IReadOnlyList<string> AllowedCommands { get; init; } = new[]
    {
        "dotnet restore",
        "dotnet build",
        "dotnet test",
        "git clone",
        "git symbolic-ref",
        "git status",
        "git diff",
        "git checkout",
        "git add",
        "git commit",
        "git push"
    };
}
```

The `McpServers` and `Dapr` LLD sections are intentionally absent in phase 1. Adding them in phase 2 is additive — bind new options records, don't reshape existing ones.

**Deviation from LLD's literal `AllowedCommands` list:** the LLD example lists only the *agent-issued* operations (`dotnet *`, `git status/diff/checkout/add/commit/push`). The orchestrator needs `git clone` (to populate the workspace) and `git symbolic-ref` (to resolve the default branch and as a defence-in-depth check before push — see plan 03). Both are added to the phase-1 default. In phase 2 the policy engine will split orchestrator-trusted operations from agent-issued ones via a separate interface; until then the allowlist is shared, and these two entries are the cost.

## `appsettings.json` shape

```json
{
  "Agent": {
    "Name": "DeveloperAgent",
    "Model": "claude-opus-4-7",
    "Effort": "xhigh",
    "PersonaPath": "personas/developer.md",
    "PollIntervalSeconds": 60,
    "ReviewPollIntervalSeconds": 60,
    "MaxModelTurnsHardCap": 40
  },
  "Anthropic": {
    "ApiKeySecretName": "anthropic-api-key"
  },
  "GitHub": {
    "Owner": "",
    "Repository": { "Name": "", "Url": "", "DefaultBranch": "main" },
    "Project":    { "Name": "", "Number": 0, "OwnerType": "Organization" },
    "States":     { "Ready": "Ready", "InProgress": "In Progress", "InReview": "In Review", "Done": "Done" },
    "TokenSecretName": "github-token"
  },
  "Workspace": {
    "RootPath": "/workspace",
    "AllowedCommands": [
      "dotnet restore", "dotnet build", "dotnet test",
      "git clone", "git symbolic-ref",
      "git status", "git diff", "git checkout", "git add", "git commit", "git push"
    ]
  },
  "Serilog": { "...": "unchanged" },
  "Kestrel": { "...": "unchanged — keeps http://*:8089" }
}
```

`PersonaPath` becomes a path relative to `ContentRootPath` (the published app dir) rather than `/personas/developer.md`. The LLD example uses an absolute container path; in phase 1 the persona file is part of the `DeveloperAgent` output (see plan 04 §Persona loading) so a relative path is more portable across local-dev and container.

`Owner`, repository, and project values are left empty in the committed `appsettings.json`. Local-dev values go in `appsettings.Development.json` (gitignored) or env vars (`GitHub__Owner`, `GitHub__Repository__Name`, …).

## Secret handling

Two secrets, both **never** in `appsettings.json` or any tracked file:

- **`anthropic-api-key`** — Anthropic API key.
- **`github-token`** — GitHub PAT or fine-grained token with `repo` + `project` scopes.

Resolution order (most → least specific) is implemented by a single helper `ISecretResolver`:

1. **User Secrets** (`Microsoft.Extensions.Configuration.UserSecrets`) — keyed by the `*SecretName` value. Used in `Development` only.
2. **Environment variable** — `ANTHROPIC_API_KEY`, `GITHUB_TOKEN` (or the configurable secret name uppercased and `-` → `_`).
3. **Failure** — throw at startup with a clear "configure secret X via …" message. Do not fall back to silent zero.

Phase 2 will plug Dapr Secrets API behind the same `ISecretResolver` interface. Phase 1 picks env > user-secrets, never reads files outside the secret store, and never logs secret values.

```csharp
public interface ISecretResolver
{
    string Resolve(string secretName);   // throws if missing
}
```

## Process shape

`Program.cs` keeps the existing `WebApplication.CreateBuilder(args)` pattern. The differences from today:

1. `namespace Web` → `namespace DeveloperAgent`. Class becomes `DeveloperAgent.Program`. The Razor app type reference becomes `DeveloperAgent.Components.App` (already in the right namespace — see `Components/_Imports.razor`).
2. Drop the `Azure.AI.OpenAI.AzureOpenAIClient` singleton registration.
3. Drop the old `Web.Settings` registration. Register the new options classes:
   ```csharp
   builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
   builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
   builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection("GitHub"));
   builder.Services.Configure<WorkspaceOptions>(builder.Configuration.GetSection("Workspace"));
   builder.Services.AddSingleton<ISecretResolver, EnvAndUserSecretsResolver>();
   ```
4. Register the future services (plans 02–05) as singletons. Each plan's "Deliverables" lists the exact DI registration line.
5. Register the agent lifecycle as a hosted service:
   ```csharp
   builder.Services.AddHostedService<AgentLifecycleService>();   // defined in plan 05
   ```
6. Keep Kestrel binding (`http://*:8089`), Serilog, OTel, `app.MapDefaultEndpoints(applicationStartTime)`, `app.MapRazorComponents<App>()`, the `/info` endpoint. Add nothing else here — dashboard is phase 2.

The hosted service starts after `app.Run()` is invoked; it polls regardless of whether a browser ever hits the Blazor app. The Razor pages stay as the default template and serve as "the process is up" indicator until the dashboard lands.

## Behavior

- **Startup logging** — log the resolved configuration (with secret *names*, never values) via `Library.Extensions.AllConfigurationKeys`. The existing call in `Program.cs` is kept; it just runs against the new schema.
- **Startup validation** — bind all four options sections eagerly with `OptionsBuilder<T>().Validate(...).ValidateOnStart()` so a bad `appsettings.json` fails fast instead of at first use. Implementer either decorates required fields with DataAnnotations (`[Required]`, `[Range]`) and adds `ValidateDataAnnotations()`, or writes a one-line `Validate(...)` predicate per options class — pick one consistently. The minimum required asserts: `Agent.PollIntervalSeconds > 0`, `Agent.ReviewPollIntervalSeconds > 0`, `Agent.MaxModelTurnsHardCap > 0`, `GitHub.Owner` non-empty, `GitHub.Repository.Url` non-empty, `GitHub.Project.Number > 0`, `Workspace.RootPath` non-empty, `Workspace.AllowedCommands` non-empty.
- **`PersonaPath` resolution** — at startup, resolve `Agent.PersonaPath` relative to `ContentRootPath`. If the file is missing, fail fast (the agent cannot operate without a persona).
- **Secret resolution** — eager at startup. Resolve both secrets once into a `record SecretsBundle(string AnthropicApiKey, string GitHubToken)` registered as a singleton. Subsequent services consume the bundle, not the resolver.

## Out of scope (deferred to phase 2)

- `McpServers` config section + MCP client startup → plan in phase 2.
- `Dapr` config section + sidecar wiring → phase 2.
- `Workspace.MaxChangedFiles`, `MaxChangedLines`, denied-commands rules, secret-file blocklist → phase 2 (the policy engine).
- Operator dashboard pages → phase 2.
- `ISecretResolver` Dapr-Secrets-API implementation → phase 2 (the interface stays the same).

## Verification

- `dotnet build src/DeveloperAgent/DeveloperAgent.csproj` succeeds without `Azure.AI.OpenAI`.
- `dotnet run --project src/DeveloperAgent/DeveloperAgent.csproj` starts, logs the new configuration tree, fails fast with a clear message if `ANTHROPIC_API_KEY` or `GITHUB_TOKEN` is not set in env / user-secrets.
- `dotnet run --project src/AppHost/AppHost.csproj` shows `DeveloperAgent` healthy in the Aspire dashboard.
- `Settings`-binding unit tests in `tests/DeveloperAgent.Tests/Configuration/` (see plan 06) verify default values and that a malformed section throws at bind time.
