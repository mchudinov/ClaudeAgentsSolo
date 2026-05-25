using System.Text.RegularExpressions;
using FluentAssertions;

namespace DeveloperAgent.Tests.Resilience;

/// <summary>
/// Step-26 (P2-K) is supposed to replace any ad-hoc retry constants inside
/// <c>AnthropicAgentRunner</c> with the standard resilience pipeline.
/// At the time Step-26 landed, <c>AnthropicAgentRunner</c> already carried no
/// retry-shaped constants — but this guard test exists so that a future refactor
/// re-introducing one (e.g. <c>private const int MaxRetries = 5;</c>) is caught
/// immediately. The retry layer belongs in <c>Microsoft.Extensions.Http.Resilience</c>
/// + the Dapr Resiliency CRD, not inside the agent runner.
/// </summary>
public sealed class NoAdHocRetryConstantsTests
{
    [Fact]
    public void AnthropicAgentRunner_source_does_not_define_ad_hoc_retry_constants()
    {
        var source = ReadSource("Agent", "AnthropicAgentRunner.cs");

        // Strip line and block comments before scanning so the test reacts to
        // real code, not to the explanatory doc-comments above class members.
        var stripped = StripComments(source);

        var pattern = new Regex(
            @"\b(MaxRetries?|RetryCount|RetryAttempts|RetryDelay(Ms|Seconds)?|BackoffMs|RetryBackoff(Ms|Seconds)?|BaseDelayMs|MaxBackoffMs)\b",
            RegexOptions.IgnoreCase);

        var matches = pattern.Matches(stripped).Select(m => m.Value).Distinct().ToList();

        matches.Should().BeEmpty(
            because: "Step-26 (P2-K) requires the retry behaviour live in Microsoft.Extensions.Http.Resilience " +
                     "(named-client AddStandardResilienceHandler in Program.cs) and the Dapr Resiliency CRD; " +
                     "if you see this fail, move the retry knob into either of those layers, not into a per-class constant. " +
                     "Forbidden constant names that appeared: {0}",
            string.Join(", ", matches));
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(
                new[] { dir, "src", "DeveloperAgent" }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            $"Could not locate src/DeveloperAgent/{string.Join('/', relativeParts)} by walking up from " +
            AppContext.BaseDirectory);
    }

    private static string StripComments(string source)
    {
        // Remove // ... line comments.
        var withoutLineComments = Regex.Replace(source, @"//[^\n]*", string.Empty);
        // Remove /* ... */ block comments (non-greedy, multi-line).
        var withoutBlockComments = Regex.Replace(withoutLineComments, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return withoutBlockComments;
    }
}
