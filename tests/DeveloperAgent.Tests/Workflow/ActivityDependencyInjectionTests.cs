using Dapr.Workflow;
using DeveloperAgent.Agent;
using DeveloperAgent.AgentMemory;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Observability;
using DeveloperAgent.Workspace;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Verifies that each activity class has a constructor whose parameter types are all
/// known service interfaces. Uses reflection to catch DI registration mismatches early —
/// without needing a live DI container.
/// </summary>
public sealed class ActivityDependencyInjectionTests
{
    /// <summary>
    /// The set of interface/class types that the DI container can provide to activity
    /// constructors.  Every constructor parameter type must appear in this set.
    /// </summary>
    private static readonly HashSet<Type> KnownServiceTypes =
    [
        typeof(ILogger<AcquireTaskActivity>),
        typeof(ILogger<CreateBranchActivity>),
        typeof(ILogger<PlanActivity>),
        typeof(ILogger<ModifyCodeActivity>),
        typeof(ILogger<BuildActivity>),
        typeof(ILogger<TestActivity>),
        typeof(ILogger<CreatePullRequestActivity>),
        typeof(ILogger<WaitForReviewActivity>),
        typeof(ILogger<DoneActivity>),
        typeof(ILogger<CompactMemoryActivity>),
        typeof(ILogger<LoadAgentSessionActivity>),
        typeof(ILogger<SaveAgentSessionActivity>),
        typeof(ILogger<DeleteAgentSessionActivity>),
        typeof(IGitHubProjectService),
        typeof(IWorkspaceManager),
        typeof(IGitClient),
        typeof(IAgentRunner),
        typeof(ITaskStateStore),
        typeof(IAgentSessionStore),
        typeof(IAgentMemoryStore),
        typeof(AgentMetrics),
        typeof(TimeProvider),
        typeof(IDaprWorkflowClient),
    ];

    [Theory]
    [InlineData(typeof(AcquireTaskActivity))]
    [InlineData(typeof(CreateBranchActivity))]
    [InlineData(typeof(PlanActivity))]
    [InlineData(typeof(ModifyCodeActivity))]
    [InlineData(typeof(BuildActivity))]
    [InlineData(typeof(TestActivity))]
    [InlineData(typeof(CreatePullRequestActivity))]
    [InlineData(typeof(WaitForReviewActivity))]
    [InlineData(typeof(DoneActivity))]
    [InlineData(typeof(CompactMemoryActivity))]
    [InlineData(typeof(LoadAgentSessionActivity))]
    [InlineData(typeof(SaveAgentSessionActivity))]
    [InlineData(typeof(DeleteAgentSessionActivity))]
    public void Activity_has_exactly_one_public_constructor(Type activityType)
    {
        var ctors = activityType.GetConstructors();
        ctors.Should().HaveCount(1,
            because: $"{activityType.Name} must have exactly one public constructor for DI resolution");
    }

    [Theory]
    [InlineData(typeof(AcquireTaskActivity))]
    [InlineData(typeof(CreateBranchActivity))]
    [InlineData(typeof(PlanActivity))]
    [InlineData(typeof(ModifyCodeActivity))]
    [InlineData(typeof(BuildActivity))]
    [InlineData(typeof(TestActivity))]
    [InlineData(typeof(CreatePullRequestActivity))]
    [InlineData(typeof(WaitForReviewActivity))]
    [InlineData(typeof(DoneActivity))]
    [InlineData(typeof(CompactMemoryActivity))]
    [InlineData(typeof(LoadAgentSessionActivity))]
    [InlineData(typeof(SaveAgentSessionActivity))]
    [InlineData(typeof(DeleteAgentSessionActivity))]
    public void Activity_constructor_parameters_are_all_known_DI_service_types(Type activityType)
    {
        var ctor = activityType.GetConstructors().Single();
        var parameters = ctor.GetParameters();

        foreach (var param in parameters)
        {
            var paramType = param.ParameterType;

            // Check for exact match or open generic (ILogger<T> where T is the activity type)
            var isKnown = KnownServiceTypes.Contains(paramType) ||
                          (paramType.IsGenericType &&
                           paramType.GetGenericTypeDefinition() == typeof(ILogger<>) &&
                           paramType.GenericTypeArguments[0].Assembly == typeof(AcquireTaskActivity).Assembly);

            isKnown.Should().BeTrue(
                because: $"{activityType.Name} constructor parameter '{param.Name}' of type '{paramType.Name}' " +
                         "must be a known DI service type. Add it to KnownServiceTypes if it was intentionally added.");
        }
    }
}
