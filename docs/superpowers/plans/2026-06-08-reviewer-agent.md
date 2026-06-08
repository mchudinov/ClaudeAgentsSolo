# ReviewerAgent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up an independent, standalone `ReviewerAgent` service that polls a configured GitHub repo for open PRs, reviews each with the (extracted) reviewer engine, and posts Approve / RequestChanges — all configured via `appsettings.json`, with no Dapr state.

**Architecture:** Extract the existing in-`DeveloperAgent` reviewer mechanics into a new agent-neutral `src/AgenticTools/Agent.Review/` library (depends only on `Agent.GitHub` + `Agent.Runtime`). Add two repo-centric methods to `Agent.GitHub` (list open PRs; reviewed-head-SHAs). Build a thin `src/ReviewerAgent/` host (Sdk.Web, no Dapr/actors/workflow/sandbox-commands/dashboard) whose `ReviewLifecycleService` polls and is idempotent via GitHub (skip PRs already reviewed at their current head SHA). `DeveloperAgent` stops registering its (currently dormant) reviewer.

**Tech Stack:** .NET 10, ASP.NET Core (`Microsoft.NET.Sdk.Web`), Microsoft Agent Framework (`Microsoft.Agents.AI` 1.8.0) + Anthropic provider via `Agent.Runtime`, `Agent.GitHub` (Octokit + Octokit.GraphQL), xUnit + NSubstitute + FluentAssertions, Serilog, Aspire AppHost.

**Key facts established during research (do not re-derive):**
- `DeveloperAgent` registers `IReviewerAgent` (Program.cs 277–283) and binds `ReviewerOptions` (124–129) but **never invokes `ReviewAsync` in production** — the only callers are tests. So "take over" = delete dead wiring + move code; no workflow change. `WaitForReviewActivity` already polls for an *external* verdict and stays untouched.
- The reviewer engine couples to exactly two `DeveloperAgent`-only things, both removed during extraction: `PullRequestBodyBuilder.RequiredSectionHeaders` (→ `ReviewerOptions.RequiredPrBodySections`) and `AgentOptions.Model` (→ `ReviewerOptions.Model`). It also uses `IGitHubProjectService` (→ the agent-neutral `IGitHubProjectsClient`, which carries the same PR-review methods).
- `MarkdownSectionBuilder.FindMissingSections(body, headers)` lives in `Agent.GitHub` (already reachable from `Agent.Review`).
- `HostAllowlistHandler` (Agent.Sandbox) depends only on `IOptions<SandboxOptions>.AllowedHosts`; reuse it without `AddSandboxServices`.
- Secrets: `ISecretResolver`/`EnvAndUserSecretsResolver` live in `Library/Secrets/` (shared). `SecretsBundle` + `SecretsBundleAnthropicApiKeyProvider` + `SecretsBundleGitHubTokenProvider` live in `DeveloperAgent/Configuration/` — the new host gets its own small copies.

**Engine never merges. The reviewer host has no merge path.**

Build command (run from repo root): `dotnet build src/ClaudeAgentsSolo.slnx`
Fast test command: `dotnet test src/ClaudeAgentsSolo.slnx --filter "Category!=Integration"`

---

## Phase A — Extract `Agent.Review` library

### Task 1: Scaffold the `Agent.Review` library project

**Files:**
- Create: `src/AgenticTools/Agent.Review/Agent.Review.csproj`
- Create: `src/AgenticTools/Agent.Review/Properties/InternalsVisibleTo.cs`
- Modify: `src/ClaudeAgentsSolo.slnx`

- [ ] **Step 1: Create the csproj**

`src/AgenticTools/Agent.Review/Agent.Review.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Agents.AI" Version="1.8.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Agent.GitHub\Agent.GitHub.csproj" />
    <ProjectReference Include="..\Agent.Runtime\Agent.Runtime.csproj" />
  </ItemGroup>

</Project>
```

> If a referenced package version above fails to restore, match the version already used by `Agent.Runtime.csproj`/`Agent.GitHub.csproj` for that package (open them and copy the exact `Version=`). `Microsoft.Extensions.AI` (for `AIFunction`) flows transitively from `Microsoft.Agents.AI`.

- [ ] **Step 2: Add InternalsVisibleTo for the test project**

`src/AgenticTools/Agent.Review/Properties/InternalsVisibleTo.cs`:
```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agent.Review.Tests")]
```

- [ ] **Step 3: Register the project in the solution**

In `src/ClaudeAgentsSolo.slnx`, inside `<Folder Name="/AgenticTools/">`, add (keep alphabetical):
```xml
    <Project Path="AgenticTools/Agent.Review/Agent.Review.csproj" />
```

- [ ] **Step 4: Build**

Run: `dotnet build src/ClaudeAgentsSolo.slnx`
Expected: PASS (empty library compiles; no other project references it yet).

- [ ] **Step 5: Commit**

```bash
git add src/AgenticTools/Agent.Review src/ClaudeAgentsSolo.slnx
git commit -m "feat(agent-review): scaffold Agent.Review library"
```

---

### Task 2: Add `ReviewerOptions` to `Agent.Review` (with `Model` + `RequiredPrBodySections`)

**Files:**
- Create: `src/AgenticTools/Agent.Review/ReviewerOptions.cs`

- [ ] **Step 1: Write the options record**

`src/AgenticTools/Agent.Review/ReviewerOptions.cs`:
```csharp
namespace Agent.Review;

/// <summary>
/// Reviewer-engine options — bound by the host from its <c>Reviewer</c> configuration section.
/// Agent-neutral: the engine reads the model id, persona path, the deterministic oversized-diff
/// thresholds, and the set of PR-body section headers it requires.
/// </summary>
public sealed record ReviewerOptions
{
    /// <summary>Anthropic model id the persona scan runs on (e.g. "claude-opus-4-7").</summary>
    public string Model { get; init; } = "claude-opus-4-7";

    /// <summary>Path to the reviewer persona markdown file, relative to <c>ContentRootPath</c>.</summary>
    public string PersonaPath { get; init; } = "personas/reviewer.md";

    /// <summary>Max changed files before the reviewer returns RequestChanges on size alone (no model call).</summary>
    public int MaxDiffFiles { get; init; } = 50;

    /// <summary>Max changed lines (additions + deletions) before RequestChanges on size alone.</summary>
    public int MaxDiffLines { get; init; } = 2_000;

    /// <summary>
    /// PR-body section headers the reviewer requires (each must be present with non-empty content);
    /// a body missing any of them is RequestChanges without a model call. Empty list = skip the
    /// section check entirely. Defaults to [] so the config-binder append-on-default gotcha (Step-41)
    /// cannot double-load it — the canonical list lives solely in the host's appsettings.json.
    /// </summary>
    public IReadOnlyList<string> RequiredPrBodySections { get; init; } = [];
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/AgenticTools/Agent.Review/Agent.Review.csproj`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/AgenticTools/Agent.Review/ReviewerOptions.cs
git commit -m "feat(agent-review): add agent-neutral ReviewerOptions"
```

---

### Task 3: Move `ReviewerPersonaLoader` into `Agent.Review`

**Files:**
- Create: `src/AgenticTools/Agent.Review/ReviewerPersonaLoader.cs`

- [ ] **Step 1: Write the persona loader (uses Agent.Runtime's `PersonaLoader`)**

`src/AgenticTools/Agent.Review/ReviewerPersonaLoader.cs`:
```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Review;

/// <summary>
/// Loads and caches the reviewer persona from <see cref="ReviewerOptions.PersonaPath"/> at
/// construction. Delegates file resolution to the Agent.Runtime <see cref="PersonaLoader"/> so
/// the path-resolution logic lives in one place. A distinct DI type so it can be a singleton.
/// </summary>
public sealed class ReviewerPersonaLoader
{
    /// <summary>The cached reviewer persona text.</summary>
    public string Persona { get; }

    public ReviewerPersonaLoader(IOptions<ReviewerOptions> options, IHostEnvironment env)
    {
        Persona = new PersonaLoader(options.Value.PersonaPath, env).Persona;
    }
}
```

> `PersonaLoader` is the public Agent.Runtime type with a `(string personaPath, IHostEnvironment env)` ctor and a `Persona` property (same one the host's `ReviewerPersonaLoader` used). If its namespace is not surfaced by `ImplicitUsings`, add `using Agent.Runtime;` (confirm the namespace by opening `src/AgenticTools/Agent.Runtime/PersonaLoader.cs`).

- [ ] **Step 2: Build**

Run: `dotnet build src/AgenticTools/Agent.Review/Agent.Review.csproj`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/AgenticTools/Agent.Review/ReviewerPersonaLoader.cs
git commit -m "feat(agent-review): move ReviewerPersonaLoader into library"
```

---

### Task 4: Move `IReviewerAgent`/`ReviewResult` and `SubmitReviewTool` into `Agent.Review`

**Files:**
- Create: `src/AgenticTools/Agent.Review/IReviewerAgent.cs`
- Create: `src/AgenticTools/Agent.Review/SubmitReviewTool.cs`

- [ ] **Step 1: Write the interface + result (now over `Agent.GitHub` types)**

`src/AgenticTools/Agent.Review/IReviewerAgent.cs`:
```csharp
using Agent.GitHub;

namespace Agent.Review;

/// <summary>
/// Reviews an open pull request and posts a single GitHub review (Approve or RequestChanges).
/// </summary>
/// <remarks>
/// The verdict combines deterministic checks (plain C#, no model call) with an LLM-backed
/// persona-violation scan. Two deterministic checks short-circuit before the model is invoked:
/// (1) the PR body is missing a required section → RequestChanges; (2) the diff is oversized
/// → RequestChanges. Only a PR that passes both reaches the model-backed scan. Never merges.
/// </remarks>
public interface IReviewerAgent
{
    /// <summary>Reviews PR <paramref name="pullRequestNumber"/> and submits the verdict to GitHub.</summary>
    Task<ReviewResult> ReviewAsync(int pullRequestNumber, CancellationToken ct);
}

/// <summary>The outcome of a review: the verdict posted and the summary that accompanied it.</summary>
/// <param name="Verdict">Approve or RequestChanges.</param>
/// <param name="Summary">The markdown body posted with the review.</param>
/// <param name="UsedModel">True when the model-backed scan set the verdict; false when a deterministic check short-circuited.</param>
public sealed record ReviewResult(ReviewVerdict Verdict, string Summary, bool UsedModel);
```

- [ ] **Step 2: Write `SubmitReviewTool` (only the namespace + using change vs. the host copy)**

`src/AgenticTools/Agent.Review/SubmitReviewTool.cs`:
```csharp
using System.Text.Json;
using Agent.GitHub;
using Microsoft.Extensions.AI;

namespace Agent.Review;

/// <summary>
/// The single tool exposed to the reviewer model during the persona-violation scan. The model
/// calls it exactly once to record its verdict + summary; <see cref="ReviewerAgent"/> reads the
/// recorded value back and owns the actual GitHub posting.
/// </summary>
internal sealed class SubmitReviewTool : AIFunction
{
    // JSON schema: { verdict: "approve" | "request_changes", summary: string }.
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "verdict": {
              "type": "string",
              "enum": ["approve", "request_changes"],
              "description": "approve when the PR is correct, tested, and consistent; request_changes otherwise."
            },
            "summary": {
              "type": "string",
              "description": "Markdown review body. For request_changes, itemize the issues found."
            }
          },
          "required": ["verdict", "summary"]
        }
        """).RootElement;

    public ReviewVerdict? RecordedVerdict { get; private set; }
    public string? RecordedSummary { get; private set; }

    public override string Name => "submit_review";

    public override string Description =>
        "Submit your review verdict for this pull request. Call this exactly once when you have " +
        "finished reviewing. Choose 'approve' or 'request_changes' and provide a markdown summary.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var verdictRaw = GetString(arguments, "verdict");
        var summary = GetString(arguments, "summary") ?? string.Empty;

        RecordedVerdict = string.Equals(verdictRaw, "approve", StringComparison.OrdinalIgnoreCase)
            ? ReviewVerdict.Approve
            : ReviewVerdict.RequestChanges;
        RecordedSummary = summary;

        return ValueTask.FromResult<object?>(new { recorded = true });
    }

    private static string? GetString(AIFunctionArguments arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => value.ToString(),
        };
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/AgenticTools/Agent.Review/Agent.Review.csproj`
Expected: PASS (`ReviewerAgent` referenced in the xmldoc/`<see>` does not yet exist as a symbol — xmldoc cref to a missing type only warns; it compiles. It is created in Task 5.)

- [ ] **Step 4: Commit**

```bash
git add src/AgenticTools/Agent.Review/IReviewerAgent.cs src/AgenticTools/Agent.Review/SubmitReviewTool.cs
git commit -m "feat(agent-review): move IReviewerAgent + SubmitReviewTool into library"
```

---

### Task 5: Move `ReviewerAgent` into `Agent.Review` (decoupled)

**Files:**
- Create: `src/AgenticTools/Agent.Review/ReviewerAgent.cs`

Three changes vs. the host original: depend on `IGitHubProjectsClient` (not `IGitHubProjectService`); read `Model` from `ReviewerOptions` (drop the `AgentOptions` ctor param); drive `MissingSections` from `ReviewerOptions.RequiredPrBodySections` (not `PullRequestBodyBuilder`).

- [ ] **Step 1: Write the engine**

`src/AgenticTools/Agent.Review/ReviewerAgent.cs`:
```csharp
using Agent.GitHub;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Review;

/// <summary>
/// Reviews a pull request using Microsoft Agent Framework (the reviewer persona is the system
/// prompt) plus two deterministic pre-checks. See <see cref="IReviewerAgent"/> for the flow.
/// The chat client is built per review from <see cref="IAgentChatClientFactory"/> so tests can
/// substitute a scripted client; the persona is supplied via <c>ChatOptions.Instructions</c>.
/// </summary>
public sealed class ReviewerAgent : IReviewerAgent
{
    private const int MaxTokens = 8_000;

    private readonly IGitHubProjectsClient _gitHub;
    private readonly IAgentChatClientFactory _chatClientFactory;
    private readonly ReviewerPersonaLoader _persona;
    private readonly ReviewerOptions _reviewerOptions;
    private readonly ILogger<ReviewerAgent> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ReviewerAgent(
        IGitHubProjectsClient gitHub,
        IAgentChatClientFactory chatClientFactory,
        ReviewerPersonaLoader persona,
        IOptions<ReviewerOptions> reviewerOptions,
        ILogger<ReviewerAgent> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _gitHub = gitHub;
        _chatClientFactory = chatClientFactory;
        _persona = persona;
        _reviewerOptions = reviewerOptions.Value;
        _logger = logger;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task<ReviewResult> ReviewAsync(int pullRequestNumber, CancellationToken ct)
    {
        var context = await _gitHub.GetPullRequestForReviewAsync(pullRequestNumber, ct).ConfigureAwait(false);

        // ── Deterministic check 1: required PR-body sections ───────────────────────
        var missing = MissingSections(context.Body);
        if (missing.Count > 0)
        {
            var summary =
                "RequestChanges — the PR body is missing required section(s): " +
                string.Join(", ", missing) +
                ". Every PR body must contain " +
                string.Join(", ", _reviewerOptions.RequiredPrBodySections) +
                ", in that order, with non-empty content under each.";
            _logger.LogInformation(
                "PR #{Number}: RequestChanges (missing sections: {Missing})",
                pullRequestNumber, string.Join(", ", missing));
            return await PostAsync(pullRequestNumber, ReviewVerdict.RequestChanges, summary, usedModel: false, ct)
                .ConfigureAwait(false);
        }

        // ── Deterministic check 2: oversized diff ─────────────────────────────────
        if (context.ChangedFiles > _reviewerOptions.MaxDiffFiles ||
            context.ChangedLines > _reviewerOptions.MaxDiffLines)
        {
            var summary =
                $"RequestChanges — the diff is too large to review safely: " +
                $"{context.ChangedFiles} changed file(s) (limit {_reviewerOptions.MaxDiffFiles}) and " +
                $"{context.ChangedLines} changed line(s) (limit {_reviewerOptions.MaxDiffLines}). " +
                "Split the change into smaller, independently reviewable PRs.";
            _logger.LogInformation(
                "PR #{Number}: RequestChanges (oversized diff: {Files} files, {Lines} lines)",
                pullRequestNumber, context.ChangedFiles, context.ChangedLines);
            return await PostAsync(pullRequestNumber, ReviewVerdict.RequestChanges, summary, usedModel: false, ct)
                .ConfigureAwait(false);
        }

        // ── Model-backed persona-violation scan ───────────────────────────────────
        var (verdict, scanSummary) = await RunPersonaScanAsync(context, ct).ConfigureAwait(false);
        return await PostAsync(pullRequestNumber, verdict, scanSummary, usedModel: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the required section headers absent from <paramref name="body"/>, in order. An empty
    /// <see cref="ReviewerOptions.RequiredPrBodySections"/> means the check is skipped (no missing).
    /// </summary>
    private List<string> MissingSections(string body)
        => MarkdownSectionBuilder
            .FindMissingSections(body, _reviewerOptions.RequiredPrBodySections)
            .ToList();

    private async Task<(ReviewVerdict Verdict, string Summary)> RunPersonaScanAsync(
        PullRequestReviewContext context, CancellationToken ct)
    {
        var submitTool = new SubmitReviewTool();
        var chatClient = _chatClientFactory.Create(_reviewerOptions.Model);

        var agentOptions = new ChatClientAgentOptions
        {
            Name = "ReviewerAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = _persona.Persona,
                Tools = [submitTool],
                MaxOutputTokens = MaxTokens,
                // Temperature intentionally NOT set: newer Anthropic models reject a `temperature`
                // request field; leaving it null makes the provider omit it.
                AllowMultipleToolCalls = false,
            },
        };

        var agent = new ChatClientAgent(chatClient, agentOptions, _loggerFactory);
        var kickoff = new ChatMessage(ChatRole.User, BuildScanPrompt(context));

        try
        {
            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            await agent.RunAsync(kickoff, session, options: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persona scan failed for PR #{Number}; failing closed", context.Number);
            return (ReviewVerdict.RequestChanges,
                "RequestChanges — the reviewer could not complete its persona scan due to an " +
                $"internal error ({ex.GetType().Name}). Re-run the review once the issue is resolved.");
        }

        if (submitTool.RecordedVerdict is { } verdict)
            return (verdict, submitTool.RecordedSummary ?? string.Empty);

        _logger.LogWarning(
            "PR #{Number}: reviewer model did not call submit_review; failing closed", context.Number);
        return (ReviewVerdict.RequestChanges,
            "RequestChanges — the reviewer did not produce a verdict via submit_review. Please re-run the review.");
    }

    private async Task<ReviewResult> PostAsync(
        int pullRequestNumber, ReviewVerdict verdict, string summary, bool usedModel, CancellationToken ct)
    {
        await _gitHub.SubmitReviewAsync(pullRequestNumber, verdict, summary, ct).ConfigureAwait(false);
        return new ReviewResult(verdict, summary, usedModel);
    }

    private static string BuildScanPrompt(PullRequestReviewContext context)
        => $"Review pull request #{context.Number}.\n\n" +
           $"The PR body passed the deterministic section and diff-size pre-checks.\n" +
           $"Your job now is to scan the diff for violations (correctness, security, missing tests, " +
           $"nullable/async/DI issues, secrets in code or logs, unrelated churn). Read the whole diff " +
           $"before deciding.\n\n" +
           $"PR body:\n{context.Body}\n\n" +
           $"Unified diff ({context.ChangedFiles} files, {context.ChangedLines} lines):\n{context.UnifiedDiff}\n\n" +
           $"When done, call submit_review exactly once with your verdict and a markdown summary.";
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/AgenticTools/Agent.Review/Agent.Review.csproj`
Expected: PASS.

> If `MarkdownSectionBuilder.FindMissingSections` is not visible, confirm it is `public static` in `src/AgenticTools/Agent.GitHub/MarkdownSectionBuilder.cs` and that the namespace `Agent.GitHub` is in scope (the file already has `using Agent.GitHub;`).

- [ ] **Step 3: Commit**

```bash
git add src/AgenticTools/Agent.Review/ReviewerAgent.cs
git commit -m "feat(agent-review): move ReviewerAgent engine into library, decoupled from host"
```

---

### Task 6: Add the `AddReviewServices` DI entry point

**Files:**
- Create: `src/AgenticTools/Agent.Review/ReviewServiceCollectionExtensions.cs`

- [ ] **Step 1: Write the registration**

`src/AgenticTools/Agent.Review/ReviewServiceCollectionExtensions.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Review;

/// <summary>
/// DI registration for the agent-neutral reviewer engine. The host must additionally:
/// bind <see cref="ReviewerOptions"/> (from its <c>Reviewer</c> section); register an
/// <see cref="Agent.GitHub.IGitHubProjectsClient"/> (via <c>AddGitHubProjectServices</c>); and
/// register an <c>IAgentChatClientFactory</c> (via <c>AddAgentRuntimeServices</c>).
/// </summary>
public static class ReviewServiceCollectionExtensions
{
    public static IServiceCollection AddReviewServices(this IServiceCollection services)
    {
        services.AddSingleton<ReviewerPersonaLoader>();
        services.AddSingleton<IReviewerAgent, ReviewerAgent>();
        return services;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/ClaudeAgentsSolo.slnx`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/AgenticTools/Agent.Review/ReviewServiceCollectionExtensions.cs
git commit -m "feat(agent-review): add AddReviewServices DI entry point"
```

---

### Task 7: Create `Agent.Review.Tests` (move + adapt the reviewer unit tests)

**Files:**
- Create: `src/Tests/Agent.Review.Tests/Agent.Review.Tests.csproj`
- Create: `src/Tests/Agent.Review.Tests/GlobalUsings.cs`
- Create: `src/Tests/Agent.Review.Tests/ReviewerAgentTests.cs`
- Modify: `src/ClaudeAgentsSolo.slnx`

- [ ] **Step 1: Create the test csproj (model on `Agent.GitHub.Tests.csproj`)**

Open `src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj`, copy it to the new path, and change the `<ProjectReference>` to point at `Agent.Review` (and keep `Agent.GitHub` + `Agent.Runtime` if its package set needs them transitively). Target result:

`src/Tests/Agent.Review.Tests/Agent.Review.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <!-- Copy the EXACT PackageReference block from Agent.GitHub.Tests.csproj
       (xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, NSubstitute,
        FluentAssertions, coverlet.collector, Microsoft.Extensions.Hosting). -->

  <ItemGroup>
    <ProjectReference Include="..\..\AgenticTools\Agent.Review\Agent.Review.csproj" />
    <ProjectReference Include="..\..\AgenticTools\Agent.GitHub\Agent.GitHub.csproj" />
    <ProjectReference Include="..\..\AgenticTools\Agent.Runtime\Agent.Runtime.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create GlobalUsings (mirror the source test project)**

Open `src/Tests/Agent.GitHub.Tests/GlobalUsings.cs` (or `DeveloperAgent.Tests`'s) and reproduce the same global usings here so `Substitute`, `FluentAssertions`, and `Xunit` resolve without per-file usings.

`src/Tests/Agent.Review.Tests/GlobalUsings.cs` (expected content — verify against the source):
```csharp
global using Xunit;
global using NSubstitute;
global using FluentAssertions;
```

- [ ] **Step 3: Register in the solution**

In `src/ClaudeAgentsSolo.slnx`, inside `<Folder Name="/Tests/">`, add (keep alphabetical):
```xml
    <Project Path="Tests/Agent.Review.Tests/Agent.Review.Tests.csproj" />
```

- [ ] **Step 4: Write the adapted tests**

Changes vs. the original `DeveloperAgent.Tests/Agent/Review/ReviewerAgentTests.cs`: `IGitHubProjectService` → `IGitHubProjectsClient`; build the clean body inline (no `PullRequestBodyBuilder`); set `RequiredPrBodySections` to the four headers; drop the `AgentOptions` ctor arg (model now in `ReviewerOptions`); persona loader and chat-client stubs are otherwise identical.

`src/Tests/Agent.Review.Tests/ReviewerAgentTests.cs`:
```csharp
using Agent.GitHub;
using Agent.Review;
using Agent.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Review.Tests;

public sealed class ReviewerAgentTests
{
    private const int PrNumber = 7;

    private static readonly string[] RequiredSections =
        ["## Summary", "## User-visible behavior", "## Tests/validation run", "## Notes/assumptions"];

    // A fully-formed four-section body so the missing-section check passes.
    private static readonly string CleanBody =
        "## Summary\nAdds a widget.\n\n" +
        "## User-visible behavior\nCallers can now request a widget.\n\n" +
        "## Tests/validation run\ndotnet test → 10 passed.\n\n" +
        "## Notes/assumptions\nNone\n";

    private sealed class StubChatClientFactory : IAgentChatClientFactory
    {
        private readonly IChatClient _client;
        public StubChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string modelId) => _client;
    }

    private static ReviewerPersonaLoader MakePersonaLoader()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "reviewer-tests-" + Guid.NewGuid().ToString("N"));
        var personasDir = Path.Combine(tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "reviewer.md"), "You are a code reviewer.");
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        return new ReviewerPersonaLoader(
            Options.Create(new ReviewerOptions { PersonaPath = "personas/reviewer.md" }), env);
    }

    private static IChatClient NeverCalledChatClient()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ =>
                throw new InvalidOperationException("Model must not be called on a deterministic-check path."));
        return client;
    }

    private static IChatClient ScriptedChatClient(string verdict, string summary)
    {
        int call = 0;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                call++;
                if (call == 1)
                {
                    var fcc = new FunctionCallContent(
                        "call-1", "submit_review",
                        new Dictionary<string, object?> { ["verdict"] = verdict, ["summary"] = summary });
                    var msg = new ChatMessage(ChatRole.Assistant, [fcc]);
                    return Task.FromResult(new ChatResponse(msg) { FinishReason = ChatFinishReason.ToolCalls });
                }
                var text = new ChatMessage(ChatRole.Assistant, "Review submitted.");
                return Task.FromResult(new ChatResponse(text) { FinishReason = ChatFinishReason.Stop });
            });
        return client;
    }

    private static IChatClient CapturingChatClient(Action<ChatOptions> observe)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observe(ci.Arg<ChatOptions>());
                var text = new ChatMessage(ChatRole.Assistant, "Looks good.");
                return Task.FromResult(new ChatResponse(text) { FinishReason = ChatFinishReason.Stop });
            });
        return client;
    }

    private static IChatClient TextOnlyChatClient()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var text = new ChatMessage(ChatRole.Assistant, "Looks good.");
                return Task.FromResult(new ChatResponse(text) { FinishReason = ChatFinishReason.Stop });
            });
        return client;
    }

    private static ReviewerAgent BuildReviewer(
        IGitHubProjectsClient gitHub,
        IChatClient chatClient,
        int maxDiffFiles = 50,
        int maxDiffLines = 2_000)
        => new(
            gitHub,
            new StubChatClientFactory(chatClient),
            MakePersonaLoader(),
            Options.Create(new ReviewerOptions
            {
                MaxDiffFiles = maxDiffFiles,
                MaxDiffLines = maxDiffLines,
                RequiredPrBodySections = RequiredSections,
            }),
            NullLogger<ReviewerAgent>.Instance);

    private static IGitHubProjectsClient GitHubReturning(PullRequestReviewContext context)
    {
        var gitHub = Substitute.For<IGitHubProjectsClient>();
        gitHub.GetPullRequestForReviewAsync(PrNumber, Arg.Any<CancellationToken>()).Returns(context);
        gitHub.SubmitReviewAsync(Arg.Any<int>(), Arg.Any<ReviewVerdict>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return gitHub;
    }

    [Fact]
    public async Task Clean_PR_approved_by_model_posts_Approve()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 2, ChangedLines: 30, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);
        var reviewer = BuildReviewer(gitHub, ScriptedChatClient("approve", "Correct, tested, consistent."));

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.Verdict.Should().Be(ReviewVerdict.Approve);
        result.UsedModel.Should().BeTrue();
        await gitHub.Received(1).SubmitReviewAsync(
            PrNumber, ReviewVerdict.Approve, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Body_missing_a_section_requests_changes_without_calling_model()
    {
        var incompleteBody =
            "## Summary\nAdds a widget.\n\n" +
            "## User-visible behavior\nNone\n\n" +
            "## Tests/validation run\nRan tests.\n";
        var ctx = new PullRequestReviewContext(PrNumber, incompleteBody, ChangedFiles: 1, ChangedLines: 5, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);
        var reviewer = BuildReviewer(gitHub, NeverCalledChatClient());

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.Verdict.Should().Be(ReviewVerdict.RequestChanges);
        result.UsedModel.Should().BeFalse();
        result.Summary.Should().Contain("## Notes/assumptions");
        await gitHub.Received(1).SubmitReviewAsync(
            PrNumber, ReviewVerdict.RequestChanges, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Oversized_diff_by_lines_requests_changes_without_calling_model()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 3, ChangedLines: 5_000, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);
        var reviewer = BuildReviewer(gitHub, NeverCalledChatClient(), maxDiffFiles: 50, maxDiffLines: 2_000);

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.Verdict.Should().Be(ReviewVerdict.RequestChanges);
        result.UsedModel.Should().BeFalse();
        result.Summary.Should().Contain("too large");
    }

    [Fact]
    public async Task Oversized_diff_by_files_requests_changes_without_calling_model()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 60, ChangedLines: 100, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);
        var reviewer = BuildReviewer(gitHub, NeverCalledChatClient(), maxDiffFiles: 50, maxDiffLines: 2_000);

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.Verdict.Should().Be(ReviewVerdict.RequestChanges);
        result.UsedModel.Should().BeFalse();
    }

    [Fact]
    public async Task Model_request_changes_posts_RequestChanges()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 1, ChangedLines: 20, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);
        var reviewer = BuildReviewer(gitHub, ScriptedChatClient("request_changes", "Missing a test for the null path."));

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.Verdict.Should().Be(ReviewVerdict.RequestChanges);
        result.UsedModel.Should().BeTrue();
        result.Summary.Should().Contain("null path");
    }

    [Fact]
    public async Task Persona_scan_does_not_set_Temperature_on_ChatOptions()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 1, ChangedLines: 20, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);

        ChatOptions? observed = null;
        var reviewer = BuildReviewer(gitHub, CapturingChatClient(o => observed = o));

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.UsedModel.Should().BeTrue();
        observed.Should().NotBeNull();
        observed!.Temperature.Should().BeNull(
            because: "the model rejects a `temperature` request field; it must not be sent");
    }

    [Fact]
    public async Task Model_finishing_without_submit_review_fails_closed_to_RequestChanges()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 1, ChangedLines: 20, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);
        var reviewer = BuildReviewer(gitHub, TextOnlyChatClient());

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.Verdict.Should().Be(ReviewVerdict.RequestChanges);
        await gitHub.Received(1).SubmitReviewAsync(
            PrNumber, ReviewVerdict.RequestChanges, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 5: Run the new tests — verify they pass**

Run: `dotnet test src/Tests/Agent.Review.Tests/Agent.Review.Tests.csproj`
Expected: PASS (7 tests). If `Agent.Runtime` namespace differs for `IAgentChatClientFactory`, fix the `using`.

- [ ] **Step 6: Commit**

```bash
git add src/Tests/Agent.Review.Tests src/ClaudeAgentsSolo.slnx
git commit -m "test(agent-review): port reviewer unit tests to the new library"
```

---

### Task 8: Remove the dormant reviewer from `DeveloperAgent`

**Files:**
- Delete: `src/DeveloperAgent/Agent/Review/IReviewerAgent.cs`, `ReviewerAgent.cs`, `SubmitReviewTool.cs`
- Delete: `src/DeveloperAgent/Agent/ReviewerPersonaLoader.cs`
- Delete: `src/DeveloperAgent/Configuration/ReviewerOptions.cs`
- Delete: `src/Tests/DeveloperAgent.Tests/Agent/Review/ReviewerAgentTests.cs`
- Delete: `src/Tests/DeveloperAgent.Tests/Integration/ReviewerAgentIntegrationTests.cs`
- Modify: `src/DeveloperAgent/Program.cs` (remove the `Reviewer` options binding 123–129 and the reviewer registration 277–283)

- [ ] **Step 1: Delete the reviewer source + tests**

```bash
git rm src/DeveloperAgent/Agent/Review/IReviewerAgent.cs \
       src/DeveloperAgent/Agent/Review/ReviewerAgent.cs \
       src/DeveloperAgent/Agent/Review/SubmitReviewTool.cs \
       src/DeveloperAgent/Agent/ReviewerPersonaLoader.cs \
       src/DeveloperAgent/Configuration/ReviewerOptions.cs \
       src/Tests/DeveloperAgent.Tests/Agent/Review/ReviewerAgentTests.cs \
       src/Tests/DeveloperAgent.Tests/Integration/ReviewerAgentIntegrationTests.cs
```

- [ ] **Step 2: Remove the `Reviewer` options binding from `Program.cs`**

Delete this block (the `// Reviewer agent options (Step-28, P2-M).` binding, ~lines 123–129):
```csharp
            // Reviewer agent options (Step-28, P2-M).
            builder.Services
                .AddOptions<ReviewerOptions>()
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Validate(o => o.MaxDiffFiles > 0, "Reviewer.MaxDiffFiles must be > 0")
                .Validate(o => o.MaxDiffLines > 0, "Reviewer.MaxDiffLines must be > 0")
                .ValidateOnStart();
```

- [ ] **Step 3: Remove the reviewer registration from `Program.cs`**

Delete this block (the `// ── Reviewer agent (Step-28, P2-M) ──` section, ~lines 277–283):
```csharp
            // ── Reviewer agent (Step-28, P2-M) ────────────────────────────────────
            // ReviewerPersonaLoader throws at construction if personas/reviewer.md is
            // missing or empty. ReviewerAgent reuses IAgentChatClientFactory so the same
            // resilient Anthropic transport applies; deterministic body/diff-size checks
            // run before any model call.
            builder.Services.AddSingleton<DeveloperAgent.Agent.ReviewerPersonaLoader>();
            builder.Services.AddSingleton<DeveloperAgent.Agent.Review.IReviewerAgent, DeveloperAgent.Agent.Review.ReviewerAgent>();
```

- [ ] **Step 4: Build and find any stragglers**

Run: `dotnet build src/DeveloperAgent/DeveloperAgent.csproj`
Expected: PASS. If it fails on a leftover reference to `ReviewerOptions`/`ReviewerPersonaLoader`/`ReviewerAgent`, grep and remove it:
```bash
grep -rn "ReviewerOptions\|ReviewerPersonaLoader\|Agent.Review\|IReviewerAgent" src/DeveloperAgent --include=*.cs
```
(There should be none after the deletions above — `PullRequestBodyBuilder` stays, used by the PR-creation path.)

- [ ] **Step 5: Run the full fast suite — confirm nothing else depended on the reviewer**

Run: `dotnet test src/ClaudeAgentsSolo.slnx --filter "Category!=Integration"`
Expected: PASS. (`WaitForReviewActivity`, the workflow review-loop tests, and `RetryPolicyTests` use `GetPullRequestStatusAsync`/`WaitForReviewResult`, not `IReviewerAgent`.) If `DeveloperAgent.Tests` references a now-deleted `appsettings.json` `Reviewer` key in a config test, that key stays in appsettings until Task 11's host owns it — leave `DeveloperAgent/appsettings.json` unchanged here.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(developer-agent): drop the dormant in-host reviewer (moved to Agent.Review)"
```

---

## Phase B — New `Agent.GitHub` mechanics

### Task 9: Transport — list open PRs + review commit SHA

**Files:**
- Modify: `src/AgenticTools/Agent.GitHub/Transports.cs`

- [ ] **Step 1: Add a DTO and extend the review DTO**

In `Transports.cs`, add a new DTO near `RestPullRequest` and add `CommitId` to `RestPullRequestReview`:
```csharp
internal sealed record RestOpenPullRequest(
    int Number,
    string HeadSha,
    bool IsDraft,
    string Author,
    string HtmlUrl);
```
Change `RestPullRequestReview` to:
```csharp
internal sealed record RestPullRequestReview(
    long Id,
    string ReviewerLogin,
    string State,           // "APPROVED" | "CHANGES_REQUESTED" | "COMMENTED" | "DISMISSED"
    string CommitId,        // head SHA the review was submitted against
    DateTimeOffset SubmittedAt);
```

- [ ] **Step 2: Add the transport interface method**

In `IRestTransport`, add:
```csharp
    /// <summary>Lists the repository's open pull requests (number, head SHA, draft flag, author).</summary>
    Task<IReadOnlyList<RestOpenPullRequest>> ListOpenPullRequestsAsync(string owner, string repo, CancellationToken ct);
```

- [ ] **Step 3: Implement in `OctokitRestTransport`**

Update `GetPullRequestReviewsAsync`'s projection to include `r.CommitId`:
```csharp
        return reviews
            .Select(r => new RestPullRequestReview(r.Id, r.User.Login, r.State.StringValue, r.CommitId, r.SubmittedAt))
            .ToList();
```
And add the new method (place it next to `FindOpenPullRequestByHeadAsync`):
```csharp
    public async Task<IReadOnlyList<RestOpenPullRequest>> ListOpenPullRequestsAsync(
        string owner, string repo, CancellationToken ct)
    {
        var request = new PullRequestRequest { State = ItemStateFilter.Open };
        var prs = await GetClient().PullRequest.GetAllForRepository(owner, repo, request).ConfigureAwait(false);
        return prs
            .Select(p => new RestOpenPullRequest(p.Number, p.Head.Sha, p.Draft, p.User.Login, p.HtmlUrl))
            .ToList();
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/AgenticTools/Agent.GitHub/Agent.GitHub.csproj`
Expected: PASS. (Octokit's `PullRequestReview.CommitId`, `PullRequest.Draft`, and `PullRequest.User.Login` exist in Octokit 14.0.0.)

- [ ] **Step 5: Commit**

```bash
git add src/AgenticTools/Agent.GitHub/Transports.cs
git commit -m "feat(agent-github): transport support for open-PR listing + review commit SHA"
```

---

### Task 10: Public client — `ListOpenPullRequestsAsync` + `GetReviewedHeadShasAsync`

**Files:**
- Modify: `src/AgenticTools/Agent.GitHub/GitHubModels.cs`
- Modify: `src/AgenticTools/Agent.GitHub/IGitHubProjectsClient.cs`
- Modify: `src/AgenticTools/Agent.GitHub/GitHubProjectsClient.cs`
- Test: `src/Tests/Agent.GitHub.Tests/GitHubProjectsClientPullRequestListingTests.cs`

- [ ] **Step 1: Add the public model**

In `GitHubModels.cs`, add:
```csharp
/// <summary>An open pull request as surfaced for review scheduling.</summary>
/// <param name="Number">PR number in the repository.</param>
/// <param name="HeadSha">HEAD commit SHA of the head branch at fetch time.</param>
/// <param name="IsDraft">True when the PR is a draft (not ready for review).</param>
/// <param name="Author">Login of the user who opened the PR.</param>
/// <param name="HtmlUrl">Browser URL for the pull request.</param>
public sealed record OpenPullRequest(
    int Number,
    string HeadSha,
    bool IsDraft,
    string Author,
    string HtmlUrl);
```

- [ ] **Step 2: Add the two interface methods**

In `IGitHubProjectsClient.cs`, after `SubmitReviewAsync`:
```csharp
    /// <summary>Lists the configured repository's open pull requests (repo-centric; no project board needed).</summary>
    Task<IReadOnlyList<OpenPullRequest>> ListOpenPullRequestsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the distinct head-commit SHAs that <paramref name="reviewerLogin"/> has already
    /// submitted a review against on the given PR. A reviewer skips a PR whose current head SHA is
    /// in this set (idempotency without local state).
    /// </summary>
    Task<IReadOnlyList<string>> GetReviewedHeadShasAsync(int pullRequestNumber, string reviewerLogin, CancellationToken ct);
```

- [ ] **Step 3: Write the failing test**

`src/Tests/Agent.GitHub.Tests/GitHubProjectsClientPullRequestListingTests.cs`. Model the test setup (how to construct `GitHubProjectsClient` with faked `IGraphQLTransport`/`IRestTransport` and `IOptions<GitHubOptions>`) on the existing PR-related tests in `Agent.GitHub.Tests` — open that folder and reuse its helper/fixture pattern. The two behaviors to assert:
```csharp
using Agent.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.GitHub.Tests;

public sealed class GitHubProjectsClientPullRequestListingTests
{
    private static readonly GitHubOptions Options = new()
    {
        Owner = "acme",
        Repository = new RepositoryOptions { Name = "widgets" },
    };

    // NOTE: IGraphQLTransport / IRestTransport are internal — these tests rely on the existing
    // InternalsVisibleTo("Agent.GitHub.Tests"). If the existing tests use a shared fake/fixture
    // for these transports, reuse it instead of Substitute.For here.

    [Fact]
    public async Task ListOpenPullRequestsAsync_maps_transport_dtos()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        rest.ListOpenPullRequestsAsync("acme", "widgets", Arg.Any<CancellationToken>())
            .Returns(new List<RestOpenPullRequest>
            {
                new(11, "sha-a", IsDraft: false, Author: "dev-bot", HtmlUrl: "u1"),
                new(12, "sha-b", IsDraft: true,  Author: "human",   HtmlUrl: "u2"),
            });

        var client = new GitHubProjectsClient(graphQL, rest, Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<GitHubProjectsClient>.Instance);

        var result = await client.ListOpenPullRequestsAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Should().BeEquivalentTo(new OpenPullRequest(11, "sha-a", false, "dev-bot", "u1"));
        result[1].IsDraft.Should().BeTrue();
    }

    [Fact]
    public async Task GetReviewedHeadShasAsync_returns_distinct_shas_for_that_login_only()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestReviewsAsync("acme", "widgets", 11, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReview>
            {
                new(1, "reviewer-bot", "APPROVED",          "sha-old", DateTimeOffset.UnixEpoch),
                new(2, "reviewer-bot", "CHANGES_REQUESTED", "sha-old", DateTimeOffset.UnixEpoch.AddMinutes(1)),
                new(3, "reviewer-bot", "APPROVED",          "sha-new", DateTimeOffset.UnixEpoch.AddMinutes(2)),
                new(4, "someone-else",  "APPROVED",         "sha-x",   DateTimeOffset.UnixEpoch.AddMinutes(3)),
            });

        var client = new GitHubProjectsClient(graphQL, rest, Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<GitHubProjectsClient>.Instance);

        var shas = await client.GetReviewedHeadShasAsync(11, "reviewer-bot", CancellationToken.None);

        shas.Should().BeEquivalentTo(new[] { "sha-old", "sha-new" });
    }
}
```

- [ ] **Step 4: Run it — verify it fails to compile/fails**

Run: `dotnet test src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj --filter "FullyQualifiedName~PullRequestListing"`
Expected: FAIL (methods not implemented on `GitHubProjectsClient`).

- [ ] **Step 5: Implement on `GitHubProjectsClient`**

In `GitHubProjectsClient.cs`, after `SubmitReviewAsync`:
```csharp
    public async Task<IReadOnlyList<OpenPullRequest>> ListOpenPullRequestsAsync(CancellationToken ct)
    {
        var prs = await _rest.ListOpenPullRequestsAsync(_options.Owner, _options.Repository.Name, ct)
            .ConfigureAwait(false);
        return prs
            .Select(p => new OpenPullRequest(p.Number, p.HeadSha, p.IsDraft, p.Author, p.HtmlUrl))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetReviewedHeadShasAsync(
        int pullRequestNumber, string reviewerLogin, CancellationToken ct)
    {
        var reviews = await _rest.GetPullRequestReviewsAsync(
            _options.Owner, _options.Repository.Name, pullRequestNumber, ct).ConfigureAwait(false);
        return reviews
            .Where(r => string.Equals(r.ReviewerLogin, reviewerLogin, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.CommitId)
            .Where(sha => !string.IsNullOrEmpty(sha))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
```

- [ ] **Step 6: Run the tests — verify they pass**

Run: `dotnet test src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj --filter "FullyQualifiedName~PullRequestListing"`
Expected: PASS (2 tests).

- [ ] **Step 7: Build the whole solution**

Run: `dotnet build src/ClaudeAgentsSolo.slnx`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/AgenticTools/Agent.GitHub src/Tests/Agent.GitHub.Tests
git commit -m "feat(agent-github): list open PRs + reviewed-head-SHAs for idempotent review scheduling"
```

---

## Phase C — `ReviewerAgent` host service

### Task 11: Scaffold the host project

**Files:**
- Create: `src/ReviewerAgent/ReviewerAgent.csproj`
- Create: `src/ReviewerAgent/GlobalUsings.cs`
- Create: `src/ReviewerAgent/Configuration/AnthropicOptions.cs`
- Create: `src/ReviewerAgent/Configuration/SecretsBundle.cs`
- Create: `src/ReviewerAgent/Configuration/SecretsBundleAnthropicApiKeyProvider.cs`
- Create: `src/ReviewerAgent/Configuration/SecretsBundleGitHubTokenProvider.cs`
- Create: `src/ReviewerAgent/Configuration/ReviewPollingOptions.cs`
- Create: `src/ReviewerAgent/appsettings.json`
- Create: `src/ReviewerAgent/Program.cs`
- Modify: `src/ClaudeAgentsSolo.slnx`
- Copy: `personas/reviewer.md` is already at repo-root `personas/`; the csproj links it (see below).

- [ ] **Step 1: Create the csproj**

`src/ReviewerAgent/ReviewerAgent.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <UserSecretsId>e1885b1f-9a1f-40aa-bec9-b36a748f40d9</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Serilog" Version="4.3.1" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Serilog.Settings.Configuration" Version="10.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgenticTools\Agent.GitHub\Agent.GitHub.csproj" />
    <ProjectReference Include="..\AgenticTools\Agent.Runtime\Agent.Runtime.csproj" />
    <ProjectReference Include="..\AgenticTools\Agent.Review\Agent.Review.csproj" />
    <ProjectReference Include="..\AgenticTools\Agent.Sandbox\Agent.Sandbox.csproj" />
    <ProjectReference Include="..\Library\Library.csproj" />
    <ProjectReference Include="..\ServiceDefaults\ServiceDefaults.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="..\..\personas\**\*.md" LinkBase="personas" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```
(Shares the same `UserSecretsId` as `DeveloperAgent` so the same `anthropic-api-key` / `github-token` user-secrets are reused locally. `Agent.Sandbox` is referenced only for `HostAllowlistHandler` + `SandboxOptions`.)

- [ ] **Step 2: GlobalUsings**

`src/ReviewerAgent/GlobalUsings.cs`:
```csharp
global using Agent.GitHub;
global using Agent.Review;
global using Agent.Runtime;
global using ReviewerAgent.Configuration;
```

- [ ] **Step 3: Anthropic options (host copy)**

`src/ReviewerAgent/Configuration/AnthropicOptions.cs`:
```csharp
namespace ReviewerAgent.Configuration;

/// <summary>Anthropic settings — bound from the <c>Anthropic</c> configuration section.</summary>
public sealed record AnthropicOptions
{
    /// <summary>
    /// Name of the secret holding the Anthropic API key. In Development: user-secrets key.
    /// In production: env-var name (uppercased, hyphens → underscores).
    /// </summary>
    public string ApiKeySecretName { get; init; } = "anthropic-api-key";
}
```

- [ ] **Step 4: SecretsBundle + providers (host copies)**

`src/ReviewerAgent/Configuration/SecretsBundle.cs`:
```csharp
namespace ReviewerAgent.Configuration;

/// <summary>Resolved secrets, fetched once at startup.</summary>
public sealed record SecretsBundle(string AnthropicApiKey, string GitHubToken);
```

`src/ReviewerAgent/Configuration/SecretsBundleAnthropicApiKeyProvider.cs`:
```csharp
using Agent.Runtime;

namespace ReviewerAgent.Configuration;

/// <summary>Supplies the Anthropic API key to the runtime layer from the resolved <see cref="SecretsBundle"/>.</summary>
public sealed class SecretsBundleAnthropicApiKeyProvider : IAnthropicApiKeyProvider
{
    private readonly SecretsBundle _secrets;
    public SecretsBundleAnthropicApiKeyProvider(SecretsBundle secrets) => _secrets = secrets;
    public string GetApiKey() => _secrets.AnthropicApiKey;
}
```
> Confirm the `IAnthropicApiKeyProvider` member name by opening `src/AgenticTools/Agent.Runtime/`’s provider interface (the DeveloperAgent copy implements the same one). If the method is a property or differently named, match it exactly.

`src/ReviewerAgent/Configuration/SecretsBundleGitHubTokenProvider.cs`:
```csharp
using Agent.GitHub;

namespace ReviewerAgent.Configuration;

/// <summary>Supplies the GitHub token to the GitHub layer from the resolved <see cref="SecretsBundle"/>.</summary>
public sealed class SecretsBundleGitHubTokenProvider : IGitHubTokenProvider
{
    private readonly SecretsBundle _secrets;
    public SecretsBundleGitHubTokenProvider(SecretsBundle secrets) => _secrets = secrets;
    public string GetToken() => _secrets.GitHubToken;
}
```
> `IGitHubTokenProvider.GetToken()` is confirmed (used by the transports). Verify `IAnthropicApiKeyProvider`’s member against the source as noted above; if the DeveloperAgent copies are tiny, you may instead copy `DeveloperAgent/Configuration/SecretsBundleAnthropicApiKeyProvider.cs` verbatim and only change the namespace.

- [ ] **Step 5: Polling options**

`src/ReviewerAgent/Configuration/ReviewPollingOptions.cs`:
```csharp
namespace ReviewerAgent.Configuration;

/// <summary>
/// Host polling policy — bound from the <c>Reviewer</c> configuration section (shares the section
/// with the engine's <c>ReviewerOptions</c>; each record ignores the other's keys).
/// </summary>
public sealed record ReviewPollingOptions
{
    /// <summary>Seconds between open-PR sweeps.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>GitHub login whose prior reviews drive idempotency (the bot account the token authenticates as).</summary>
    public string ReviewerLogin { get; init; } = "";

    /// <summary>When true, draft PRs are skipped.</summary>
    public bool SkipDrafts { get; init; } = true;

    /// <summary>
    /// When non-empty, only PRs whose author login is in this list are reviewed. Empty = review all.
    /// Defaults to [] so the config-binder append-on-default gotcha cannot double-load it.
    /// </summary>
    public IReadOnlyList<string> AuthorAllowList { get; init; } = [];
}
```

- [ ] **Step 6: appsettings.json**

`src/ReviewerAgent/appsettings.json`:
```json
{
  "Reviewer": {
    "Model": "claude-opus-4-7",
    "PersonaPath": "personas/reviewer.md",
    "MaxDiffFiles": 50,
    "MaxDiffLines": 2000,
    "RequiredPrBodySections": [
      "## Summary",
      "## User-visible behavior",
      "## Tests/validation run",
      "## Notes/assumptions"
    ],
    "ReviewerLogin": "",
    "SkipDrafts": true,
    "AuthorAllowList": [],
    "PollIntervalSeconds": 60
  },
  "Anthropic": {
    "ApiKeySecretName": "anthropic-api-key"
  },
  "GitHub": {
    "Owner": "mchudinov",
    "Repository": {
      "Name": "TicTacToe2",
      "Url": "https://github.com/mchudinov/TicTacToe2",
      "DefaultBranch": "main"
    },
    "TokenSecretName": "github-token"
  },
  "Sandbox": {
    "AllowedHosts": [
      "api.anthropic.com",
      "api.github.com",
      "*.githubusercontent.com"
    ]
  },
  "HttpResilience": {
    "AttemptTimeoutSeconds": 60
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": { "Microsoft": "Warning", "System": "Warning" }
    },
    "WriteTo": [ { "Name": "Console" } ]
  },
  "Kestrel": {
    "EndPoints": {
      "Http": { "Url": "http://*:8090" }
    }
  }
}
```
> `ReviewerLogin` MUST be set (to the bot account the `github-token` authenticates as) before the service does useful idempotency; leaving it `""` means every sweep re-reviews. Set it via `appsettings.Development.json` (gitignored) locally. The `Sandbox` section carries only `AllowedHosts`; the deny lists default to `[]` and are unused because the command sandbox is not registered.

- [ ] **Step 7: Program.cs**

`src/ReviewerAgent/Program.cs`:
```csharp
using Agent.Sandbox;
using Library;
using Library.Logging;
using Library.Secrets;
using Microsoft.Extensions.Http.Resilience;
using ReviewerAgent.Configuration;
using ReviewerAgent.Lifecycle;
using ServiceDefaults.Resilience;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;

namespace ReviewerAgent;

public class Program
{
    public static void Main(string[] args)
    {
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Default", LogEventLevel.Debug)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        SelfLog.Enable(Console.Error);

        try
        {
            var applicationStartTime = DateTimeOffset.UtcNow;
            Serilog.Log.Logger.Information("ReviewerAgent is starting");

            var builder = WebApplication.CreateBuilder(args);

            builder.Configuration
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(logger);

            builder.AddServiceDefaults();
            builder.AddOpenTelemetry();

            // ── Options ───────────────────────────────────────────────────────────
            builder.Services
                .AddOptions<ReviewerOptions>()
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Validate(o => o.MaxDiffFiles > 0, "Reviewer.MaxDiffFiles must be > 0")
                .Validate(o => o.MaxDiffLines > 0, "Reviewer.MaxDiffLines must be > 0")
                .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Reviewer.Model must not be empty")
                .ValidateOnStart();

            builder.Services
                .AddOptions<ReviewPollingOptions>()
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Validate(o => o.PollIntervalSeconds > 0, "Reviewer.PollIntervalSeconds must be > 0")
                .ValidateOnStart();

            builder.Services
                .AddOptions<AnthropicOptions>()
                .Bind(builder.Configuration.GetSection("Anthropic"));

            builder.Services
                .AddOptions<GitHubOptions>()
                .Bind(builder.Configuration.GetSection("GitHub"));

            builder.Services
                .AddOptions<HttpResilienceOptions>()
                .Bind(builder.Configuration.GetSection("HttpResilience"))
                .Validate(o => o.AttemptTimeoutSeconds > 0, "HttpResilience.AttemptTimeoutSeconds must be > 0")
                .ValidateOnStart();

            // Only AllowedHosts is consumed (by HostAllowlistHandler); the deny lists default to [].
            builder.Services
                .AddOptions<SandboxOptions>()
                .Bind(builder.Configuration.GetSection("Sandbox"))
                .Validate(o => o.AllowedHosts.Count > 0, "Sandbox.AllowedHosts must not be empty")
                .ValidateOnStart();

            // ── Secrets ─────────────────────────────────────────────────────────────
            builder.Services.AddSingleton<ISecretResolver, EnvAndUserSecretsResolver>();
            builder.Services.AddSingleton<SecretsBundle>(sp =>
            {
                var resolver = sp.GetRequiredService<ISecretResolver>();
                var anthropic = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>().Value;
                var github = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>().Value;
                return new SecretsBundle(
                    AnthropicApiKey: resolver.Resolve(anthropic.ApiKeySecretName),
                    GitHubToken: resolver.Resolve(github.TokenSecretName));
            });

            // ── Egress filter + resilient HTTP clients ───────────────────────────────
            var httpResilience = new HttpResilienceOptions();
            builder.Configuration.GetSection("HttpResilience").Bind(httpResilience);
            builder.Services.AddTransient<HostAllowlistHandler>();

            builder.Services.AddSingleton<IAnthropicApiKeyProvider, SecretsBundleAnthropicApiKeyProvider>();
            builder.Services.AddAgentRuntimeServices(http => http
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler(o => HttpResilienceConfigurator.Apply(o, httpResilience)));

            builder.Services.AddSingleton<IGitHubTokenProvider, SecretsBundleGitHubTokenProvider>();
            builder.Services.AddGitHubProjectServices(http => http
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler(o => HttpResilienceConfigurator.Apply(o, httpResilience)));

            // ── Reviewer engine + polling loop ───────────────────────────────────────
            builder.Services.AddReviewServices();
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddHostedService<ReviewLifecycleService>();

            var app = builder.Build();

            app.MapDefaultEndpoints(applicationStartTime);

            app.MapGet("/info", () => Results.Json(new
            {
                service = "ReviewerAgent",
                endpoints = new[] { "/livez", "/uptime", "/info", "/review/{prNumber}", "/health", "/alive" }
            }));

            app.Run();
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "ReviewerAgent process terminated unexpectedly.");
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }
}

// Expose the implicit Program type for Aspire.Hosting.Testing / WebApplicationFactory.
public partial class Program;
```
> The exact namespaces of `HttpResilienceOptions`, `HttpResilienceConfigurator`, `ISecretResolver`/`EnvAndUserSecretsResolver`, `RecentLogBuffer`/`Library.Logging`, and `MapDefaultEndpoints`/`AddOpenTelemetry` are taken verbatim from `DeveloperAgent/Program.cs`’s `using`s. If `HttpResilienceOptions` lives in `DeveloperAgent` rather than a shared project, copy that small record into `ReviewerAgent/Configuration/` as well (grep: `grep -rn "record HttpResilienceOptions\|class HttpResilienceConfigurator" src`).

- [ ] **Step 8: Register the project in the solution**

In `src/ClaudeAgentsSolo.slnx`, add at top level (next to the other host projects):
```xml
  <Project Path="ReviewerAgent/ReviewerAgent.csproj" />
```

- [ ] **Step 9: Build** (will fail until Task 12 adds `ReviewLifecycleService`)

Run: `dotnet build src/ReviewerAgent/ReviewerAgent.csproj`
Expected: FAIL — `ReviewLifecycleService` / `ReviewerAgent.Lifecycle` not found. That is expected; Task 12 creates it. (Do not commit a non-building project; commit Task 11 + Task 12 together at the end of Task 12.)

---

### Task 12: `ReviewLifecycleService` (poll + idempotency)

**Files:**
- Create: `src/ReviewerAgent/Lifecycle/ReviewLifecycleService.cs`
- Create: `src/Tests/ReviewerAgent.Tests/ReviewerAgent.Tests.csproj`
- Create: `src/Tests/ReviewerAgent.Tests/GlobalUsings.cs`
- Create: `src/Tests/ReviewerAgent.Tests/ReviewLifecycleServiceTests.cs`
- Modify: `src/ClaudeAgentsSolo.slnx`

The loop logic is extracted into a testable `ReviewDueAsync(...)`-style method so tests don't run a real timer. Design: a public `ProcessOnceAsync(CancellationToken)` performs one sweep; `ExecuteAsync` calls it on a `PeriodicTimer`.

- [ ] **Step 1: Write the service**

`src/ReviewerAgent/Lifecycle/ReviewLifecycleService.cs`:
```csharp
using Microsoft.Extensions.Options;
using ReviewerAgent.Configuration;

namespace ReviewerAgent.Lifecycle;

/// <summary>
/// Polls the configured repository's open PRs on an interval and reviews each PR not already
/// reviewed (by the configured bot login) at its current head SHA. Stateless: GitHub is the
/// record of what was reviewed, so a restart re-derives the work from GitHub.
/// </summary>
public sealed class ReviewLifecycleService : BackgroundService
{
    private readonly IGitHubProjectsClient _gitHub;
    private readonly IReviewerAgent _reviewer;
    private readonly ReviewPollingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewLifecycleService> _logger;

    public ReviewLifecycleService(
        IGitHubProjectsClient gitHub,
        IReviewerAgent reviewer,
        IOptions<ReviewPollingOptions> options,
        TimeProvider timeProvider,
        ILogger<ReviewLifecycleService> logger)
    {
        _gitHub = gitHub;
        _reviewer = reviewer;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.PollIntervalSeconds), _timeProvider);

        // Sweep once immediately, then on every tick.
        do
        {
            try
            {
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Review sweep failed; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs one open-PR sweep: review every PR that is due. Public for tests.</summary>
    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        var open = await _gitHub.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Review sweep: {Count} open PR(s).", open.Count);

        foreach (var pr in open)
        {
            if (await IsDueAsync(pr, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Reviewing PR #{Number} (head {Sha}).", pr.Number, pr.HeadSha);
                await _reviewer.ReviewAsync(pr.Number, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True when this PR should be reviewed now (not draft-skipped, allowed author, head not yet reviewed).</summary>
    public async Task<bool> IsDueAsync(OpenPullRequest pr, CancellationToken ct)
    {
        if (_options.SkipDrafts && pr.IsDraft)
            return false;

        if (_options.AuthorAllowList.Count > 0 &&
            !_options.AuthorAllowList.Contains(pr.Author, StringComparer.OrdinalIgnoreCase))
            return false;

        var reviewedShas = await _gitHub
            .GetReviewedHeadShasAsync(pr.Number, _options.ReviewerLogin, ct).ConfigureAwait(false);

        return !reviewedShas.Contains(pr.HeadSha, StringComparer.Ordinal);
    }
}
```

- [ ] **Step 2: Create the test project (model csproj/GlobalUsings on `DeveloperAgent.Tests`)**

`src/Tests/ReviewerAgent.Tests/ReviewerAgent.Tests.csproj` — copy the package block from `src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj` (xunit, runner, Test.Sdk, NSubstitute, FluentAssertions, coverlet) and reference the host:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <!-- Copy the EXACT PackageReference block from DeveloperAgent.Tests.csproj. -->

  <ItemGroup>
    <ProjectReference Include="..\..\ReviewerAgent\ReviewerAgent.csproj" />
  </ItemGroup>

</Project>
```

`src/Tests/ReviewerAgent.Tests/GlobalUsings.cs`:
```csharp
global using Xunit;
global using NSubstitute;
global using FluentAssertions;
```

Add to `src/ClaudeAgentsSolo.slnx` under `<Folder Name="/Tests/">`:
```xml
    <Project Path="Tests/ReviewerAgent.Tests/ReviewerAgent.Tests.csproj" />
```

- [ ] **Step 3: Write the failing tests**

`src/Tests/ReviewerAgent.Tests/ReviewLifecycleServiceTests.cs`:
```csharp
using Agent.GitHub;
using Agent.Review;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewerAgent.Configuration;
using ReviewerAgent.Lifecycle;

namespace ReviewerAgent.Tests;

public sealed class ReviewLifecycleServiceTests
{
    private static ReviewLifecycleService Build(
        IGitHubProjectsClient gitHub, IReviewerAgent reviewer, ReviewPollingOptions options)
        => new(gitHub, reviewer, Options.Create(options), TimeProvider.System,
            NullLogger<ReviewLifecycleService>.Instance);

    private static IGitHubProjectsClient GitHub(
        IReadOnlyList<OpenPullRequest> open,
        Func<int, IReadOnlyList<string>>? reviewedByPr = null)
    {
        var gh = Substitute.For<IGitHubProjectsClient>();
        gh.ListOpenPullRequestsAsync(Arg.Any<CancellationToken>()).Returns(open);
        gh.GetReviewedHeadShasAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<string>)(reviewedByPr?.Invoke(ci.ArgAt<int>(0)) ?? Array.Empty<string>()));
        return gh;
    }

    [Fact]
    public async Task Reviews_an_unreviewed_open_PR()
    {
        var gh = GitHub([new OpenPullRequest(5, "sha-1", IsDraft: false, Author: "dev", HtmlUrl: "u")]);
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot" });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.Received(1).ReviewAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_a_PR_already_reviewed_at_its_current_head()
    {
        var gh = GitHub(
            [new OpenPullRequest(5, "sha-1", IsDraft: false, Author: "dev", HtmlUrl: "u")],
            reviewedByPr: _ => new[] { "sha-1" });
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot" });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.DidNotReceive().ReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_reviews_when_head_advanced_past_the_reviewed_sha()
    {
        var gh = GitHub(
            [new OpenPullRequest(5, "sha-2", IsDraft: false, Author: "dev", HtmlUrl: "u")],
            reviewedByPr: _ => new[] { "sha-1" });
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot" });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.Received(1).ReviewAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_draft_PRs_when_SkipDrafts_is_true()
    {
        var gh = GitHub([new OpenPullRequest(5, "sha-1", IsDraft: true, Author: "dev", HtmlUrl: "u")]);
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot", SkipDrafts = true });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.DidNotReceive().ReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_PRs_whose_author_is_not_in_a_non_empty_allow_list()
    {
        var gh = GitHub([new OpenPullRequest(5, "sha-1", IsDraft: false, Author: "stranger", HtmlUrl: "u")]);
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer,
            new ReviewPollingOptions { ReviewerLogin = "bot", AuthorAllowList = new[] { "dev-bot" } });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.DidNotReceive().ReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 4: Run them — verify they fail (compile error: service not yet present? no — it is). Expected red→green**

Run: `dotnet test src/Tests/ReviewerAgent.Tests/ReviewerAgent.Tests.csproj`
Expected: PASS (5 tests) once the host compiles. If red, fix the service. (The service is written in Step 1, so this task is structured as "write service + tests together"; the tests are the verification gate.)

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build src/ClaudeAgentsSolo.slnx`
Expected: PASS.

- [ ] **Step 6: Commit (Task 11 + 12 together — first green commit of the host)**

```bash
git add src/ReviewerAgent src/Tests/ReviewerAgent.Tests src/ClaudeAgentsSolo.slnx
git commit -m "feat(reviewer-agent): standalone host with polling review lifecycle"
```

---

### Task 13: Manual on-demand review endpoint

**Files:**
- Modify: `src/ReviewerAgent/Program.cs`

- [ ] **Step 1: Add the endpoint (after the `/info` map)**

```csharp
            app.MapPost("/review/{prNumber:int}", async (
                int prNumber, IReviewerAgent reviewer, CancellationToken ct) =>
            {
                var result = await reviewer.ReviewAsync(prNumber, ct);
                return Results.Json(new
                {
                    prNumber,
                    verdict = result.Verdict.ToString(),
                    usedModel = result.UsedModel,
                    summary = result.Summary
                });
            });
```

- [ ] **Step 2: Build**

Run: `dotnet build src/ReviewerAgent/ReviewerAgent.csproj`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/ReviewerAgent/Program.cs
git commit -m "feat(reviewer-agent): add POST /review/{prNumber} on-demand endpoint"
```

---

### Task 14: Register `ReviewerAgent` in the Aspire AppHost

**Files:**
- Modify: `src/AppHost/AppHost.cs`

- [ ] **Step 1: Add the project (no Dapr sidecar — the reviewer is stateless)**

In `src/AppHost/AppHost.cs`, after the `DeveloperAgent` registration (the block ending at line ~39), add:
```csharp
builder.AddProject<Projects.ReviewerAgent>("ReviewerAgent");
```

- [ ] **Step 2: Build the AppHost (regenerates the `Projects.*` metadata)**

Run: `dotnet build src/AppHost/AppHost.csproj`
Expected: PASS. (Aspire generates `Projects.ReviewerAgent` from the solution’s project reference graph; if it is not found, ensure `AppHost.csproj` has a `<ProjectReference>` to `..\ReviewerAgent\ReviewerAgent.csproj` — add it if the generator requires an explicit reference, matching how `DeveloperAgent` is referenced.)

- [ ] **Step 3: Commit**

```bash
git add src/AppHost
git commit -m "feat(apphost): orchestrate the standalone ReviewerAgent service"
```

---

### Task 15: Full-solution verification gate

- [ ] **Step 1: Restore + build Release**

Run: `dotnet build src/ClaudeAgentsSolo.slnx -c Release`
Expected: PASS.

- [ ] **Step 2: Run the full fast test suite**

Run: `dotnet test src/ClaudeAgentsSolo.slnx --filter "Category!=Integration"`
Expected: PASS — including the new `Agent.Review.Tests` (7), `ReviewerAgent.Tests` (5), and the two new `Agent.GitHub.Tests` cases, with no regressions in `DeveloperAgent.Tests`.

- [ ] **Step 3: Sanity-run the host (optional, no network needed to boot)**

Run: `dotnet run --project src/ReviewerAgent/ReviewerAgent.csproj`
Expected: it boots, logs "ReviewerAgent is starting", binds `http://*:8090`; `curl http://localhost:8090/info` returns the endpoint list. Ctrl-C to stop. (With `ReviewerLogin` unset and no GitHub creds, the first sweep logs errors but the process stays up — that is the designed fail-soft.)

- [ ] **Step 4: Final commit (if Step 3 produced any tweak)**

```bash
git add -A
git commit -m "chore(reviewer-agent): verification pass"
```

---

## Self-review notes (resolved)

- **Spec coverage:** extraction → Tasks 1–8; new Agent.GitHub mechanics → Tasks 9–10; host + polling + idempotency → Tasks 11–12; manual endpoint → 13; AppHost → 14; tests throughout. "Take over" is Task 8 (delete dormant wiring; `WaitForReviewActivity` untouched).
- **Type consistency:** `OpenPullRequest(Number, HeadSha, IsDraft, Author, HtmlUrl)`, `ReviewPollingOptions(PollIntervalSeconds, ReviewerLogin, SkipDrafts, AuthorAllowList)`, `ReviewerOptions(Model, PersonaPath, MaxDiffFiles, MaxDiffLines, RequiredPrBodySections)` are used identically across tasks. `IReviewerAgent.ReviewAsync(int, CancellationToken)` and `IGitHubProjectsClient.ListOpenPullRequestsAsync`/`GetReviewedHeadShasAsync` match between definition and call sites.
- **Naming:** engine class `Agent.Review.ReviewerAgent` vs host root namespace `ReviewerAgent` — distinct by namespace; host code references the engine via `using Agent.Review;` (in GlobalUsings).
- **Flagged for the implementer to verify against source (not guesses):** the `IAnthropicApiKeyProvider` member name; the exact `HttpResilienceOptions`/`HttpResilienceConfigurator` location (copy into the host if host-local in DeveloperAgent); test-project package blocks (copy verbatim from a sibling test csproj); `PersonaLoader`’s namespace.
```
