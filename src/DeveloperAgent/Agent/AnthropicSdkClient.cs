using Anthropic.SDK;
using Anthropic.SDK.Common;
using Anthropic.SDK.Messaging;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Agent;

/// <summary>
/// Production implementation of <see cref="IAnthropicClient"/> over <c>Anthropic.SDK 5.10.0</c>.
/// Resolves the API key lazily on first call so an unconfigured <c>dotnet run</c> does not crash at startup.
/// </summary>
internal sealed class AnthropicSdkClient : IAnthropicClient, IDisposable
{
    // Retry configuration: 3 attempts, exponential back-off (1s, 4s, 16s)
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(16)];
    private const int MaxAttempts = 3;

    private readonly SecretsBundle _secrets;
    private readonly ILogger<AnthropicSdkClient> _logger;
    private AnthropicClient? _client;
    private bool _disposed;

    /// <summary>
    /// Effort → extended-thinking budget_tokens mapping.
    /// Values per Anthropic published guidance (confirmed via Context7 for Anthropic.SDK 5.10.0
    /// which exposes <c>ThinkingParameters.BudgetTokens</c>).
    /// </summary>
    private static int? MapEffortToBudget(string effort) => effort.ToLowerInvariant() switch
    {
        "xhigh"  => 8000,
        "high"   => 4000,
        "medium" => 2000,
        "low"    => 1000,
        _        => null  // unknown effort → no thinking budget
    };

    public AnthropicSdkClient(SecretsBundle secrets, ILogger<AnthropicSdkClient> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<AnthropicResponse> SendAsync(AnthropicRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _client ??= new AnthropicClient(new APIAuthentication(_secrets.AnthropicApiKey));

        var systemMessages = new List<SystemMessage>
        {
            new(request.SystemPrompt)
        };

        var parameters = new MessageParameters
        {
            Model = request.Model,
            Messages = request.Messages,
            System = systemMessages,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            Stream = false,
            Tools = request.Tools,
        };

        // Wire extended thinking if a budget is specified
        if (request.ThinkingBudgetTokens.HasValue)
        {
            parameters.Thinking = new ThinkingParameters
            {
                BudgetTokens = request.ThinkingBudgetTokens.Value
            };
            // Extended thinking requires temperature = 1
            parameters.Temperature = 1.0m;
        }

        Exception? lastException = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var response = await _client.Messages.GetClaudeMessageAsync(parameters, ct);

                return new AnthropicResponse(
                    StopReason: response.StopReason ?? string.Empty,
                    ContentBlocks: response.Content ?? [],
                    AssistantMessage: response.Message);
            }
            catch (OperationCanceledException)
            {
                throw; // pass cancellation through without retrying
            }
            catch (Exception ex)
            {
                lastException = ex;
                bool isLastAttempt = attempt == MaxAttempts - 1;

                if (isLastAttempt)
                    break;

                var delay = RetryDelays[attempt];
                _logger.LogWarning(ex, "Anthropic API call failed (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}s",
                    attempt + 1, MaxAttempts, delay.TotalSeconds);

                await Task.Delay(delay, ct);
            }
        }

        throw new AnthropicApiException(
            $"Anthropic API unrecoverable after {MaxAttempts} attempts: {lastException?.Message}",
            lastException);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }
}
