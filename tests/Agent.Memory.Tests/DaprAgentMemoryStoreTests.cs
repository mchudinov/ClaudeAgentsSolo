
namespace Agent.Memory.Tests;

/// <summary>
/// Unit tests for <see cref="DaprAgentMemoryStore"/> via the <see cref="IDaprStateClient"/>
/// seam. Asserts the two LLD key namespaces (<c>repo-state:{repoId}</c> and
/// <c>task-memory:{projectItemId}</c>) and that every operation targets the configured
/// state-store name. Mirrors <see cref="DaprChatHistoryStoreTests"/>.
/// </summary>
public sealed class DaprAgentMemoryStoreTests
{
    private const string StoreName = "agent-state-store";
    private const string RepoId = "octo/repo";
    private const string ProjectItemId = "PVTI_abc";

    private readonly IDaprStateClient _dapr = Substitute.For<IDaprStateClient>();
    private readonly DaprAgentMemoryStore _store;

    public DaprAgentMemoryStoreTests()
    {
        _store = new DaprAgentMemoryStore(_dapr, StoreName);
    }

    [Fact]
    public void RepoKey_uses_repo_state_namespace()
    {
        DaprAgentMemoryStore.RepoKey(RepoId).Should().Be("repo-state:octo/repo");
    }

    [Fact]
    public void TaskKey_uses_task_memory_namespace()
    {
        DaprAgentMemoryStore.TaskKey(ProjectItemId).Should().Be("task-memory:PVTI_abc");
    }

    [Fact]
    public void StateStoreName_is_agent_state_store()
    {
        DaprAgentMemoryStore.StateStoreName.Should().Be("agent-state-store");
    }

    [Fact]
    public async Task LoadRepoMemoriesAsync_uses_repo_state_key_and_configured_store_name()
    {
        await _store.LoadRepoMemoriesAsync(RepoId, CancellationToken.None);

        await _dapr.Received(1).GetStateAsync<List<string>>(
            StoreName, "repo-state:octo/repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadTaskMemoriesAsync_uses_task_memory_key_and_configured_store_name()
    {
        await _store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None);

        await _dapr.Received(1).GetStateAsync<List<string>>(
            StoreName, "task-memory:PVTI_abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadRepoMemoriesAsync_returns_value_from_state_client()
    {
        var expected = new List<string> { "Repository uses xUnit", "Main branch is protected" };
        _dapr.GetStateAsync<List<string>>(StoreName, "repo-state:octo/repo", Arg.Any<CancellationToken>())
            .Returns(expected);

        var actual = await _store.LoadRepoMemoriesAsync(RepoId, CancellationToken.None);

        actual.Should().NotBeNull();
        actual!.Should().Equal("Repository uses xUnit", "Main branch is protected");
    }

    [Fact]
    public async Task LoadTaskMemoriesAsync_returns_null_when_state_client_returns_null()
    {
        _dapr.GetStateAsync<List<string>>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((List<string>?)null);

        var actual = await _store.LoadTaskMemoriesAsync("PVTI_missing", CancellationToken.None);

        actual.Should().BeNull();
    }

    [Fact]
    public async Task SaveRepoMemoriesAsync_writes_under_the_repo_state_key()
    {
        var written = new List<string> { "a", "b" };

        await _store.SaveRepoMemoriesAsync(RepoId, written, CancellationToken.None);

        await _dapr.Received(1).SaveStateAsync(
            StoreName, "repo-state:octo/repo",
            Arg.Is<List<string>>(m => m.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveTaskMemoriesAsync_writes_under_the_task_memory_key()
    {
        var written = new List<string> { "lesson" };

        await _store.SaveTaskMemoriesAsync(ProjectItemId, written, CancellationToken.None);

        await _dapr.Received(1).SaveStateAsync(
            StoreName, "task-memory:PVTI_abc",
            Arg.Is<List<string>>(m => m.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRepoMemoriesAsync_uses_the_repo_state_key()
    {
        await _store.DeleteRepoMemoriesAsync(RepoId, CancellationToken.None);

        await _dapr.Received(1).DeleteStateAsync(
            StoreName, "repo-state:octo/repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTaskMemoriesAsync_uses_the_task_memory_key()
    {
        await _store.DeleteTaskMemoriesAsync(ProjectItemId, CancellationToken.None);

        await _dapr.Received(1).DeleteStateAsync(
            StoreName, "task-memory:PVTI_abc", Arg.Any<CancellationToken>());
    }
}
