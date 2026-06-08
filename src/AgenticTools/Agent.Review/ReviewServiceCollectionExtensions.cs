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
