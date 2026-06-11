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
    public async Task Approved_PR_posts_body_that_leads_with_an_explicit_Approved_comment()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 2, ChangedLines: 30, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);
        var reviewer = BuildReviewer(gitHub, ScriptedChatClient("approve", "Correct, tested, consistent."));

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.Verdict.Should().Be(ReviewVerdict.Approve);
        // The posted review body must state the approval in plain words — the GitHub APPROVE event
        // alone is easy to miss in the UI — while preserving the model's summary after it.
        result.Summary.Should().StartWith("Approved");
        result.Summary.Should().Contain("Correct, tested, consistent.");
        await gitHub.Received(1).SubmitReviewAsync(
            PrNumber, ReviewVerdict.Approve,
            Arg.Is<string>(b => b.StartsWith("Approved") && b.Contains("Correct, tested, consistent.")),
            Arg.Any<CancellationToken>());
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
        result.Summary.Should().NotStartWith("Approved",
            because: "only an Approve verdict gets the explicit 'Approved' lead-in");
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
    public async Task Persona_scan_applies_configured_effort_as_RawRepresentationFactory()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 1, ChangedLines: 20, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);

        ChatOptions? observed = null;
        // BuildReviewer's ReviewerOptions leaves Effort at its default ("high") — a configured value.
        var reviewer = BuildReviewer(gitHub, CapturingChatClient(o => observed = o));

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.UsedModel.Should().BeTrue();
        observed.Should().NotBeNull();
        observed!.RawRepresentationFactory.Should().NotBeNull(
            because: "a configured Effort must be applied to the request via output_config.effort");
    }

    [Fact]
    public async Task Persona_scan_omits_effort_RawRepresentationFactory_when_blank()
    {
        var ctx = new PullRequestReviewContext(PrNumber, CleanBody, ChangedFiles: 1, ChangedLines: 20, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);

        ChatOptions? observed = null;
        var reviewer = new ReviewerAgent(
            gitHub,
            new StubChatClientFactory(CapturingChatClient(o => observed = o)),
            MakePersonaLoader(),
            Options.Create(new ReviewerOptions
            {
                MaxDiffFiles = 50,
                MaxDiffLines = 2_000,
                RequiredPrBodySections = RequiredSections,
                Effort = "",
            }),
            NullLogger<ReviewerAgent>.Instance);

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        result.UsedModel.Should().BeTrue();
        observed.Should().NotBeNull();
        observed!.RawRepresentationFactory.Should().BeNull(
            because: "a blank Effort must leave the provider default (no output_config.effort)");
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

    [Fact]
    public async Task Empty_required_sections_skips_the_section_check_and_reaches_the_model()
    {
        // A body with NO section headers — would normally fail the missing-section check.
        const string plainBody = "just a plain description, no sections";
        var ctx = new PullRequestReviewContext(PrNumber, plainBody, ChangedFiles: 1, ChangedLines: 10, UnifiedDiff: "diff");
        var gitHub = GitHubReturning(ctx);

        // Construct inline with RequiredPrBodySections = [] to exercise the skip path.
        var reviewer = new ReviewerAgent(
            gitHub,
            new StubChatClientFactory(ScriptedChatClient("approve", "ok")),
            MakePersonaLoader(),
            Options.Create(new ReviewerOptions
            {
                MaxDiffFiles = 50,
                MaxDiffLines = 2_000,
                RequiredPrBodySections = [],
            }),
            NullLogger<ReviewerAgent>.Instance);

        var result = await reviewer.ReviewAsync(PrNumber, CancellationToken.None);

        // The model was reached (section check was skipped, not failed).
        result.Verdict.Should().Be(ReviewVerdict.Approve);
        result.UsedModel.Should().BeTrue(
            because: "an empty RequiredPrBodySections must skip the deterministic section check entirely");
    }
}
