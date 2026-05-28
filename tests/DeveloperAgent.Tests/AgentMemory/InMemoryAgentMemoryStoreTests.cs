using DeveloperAgent.AgentMemory;

namespace DeveloperAgent.Tests.AgentMemory;

/// <summary>
/// Unit tests for <see cref="InMemoryAgentMemoryStore"/> — the test double used by
/// <see cref="DaprAgentMemoryContextProviderTests"/>. Verifies round-trip and isolation
/// between the repo and task namespaces.
/// </summary>
public sealed class InMemoryAgentMemoryStoreTests
{
    private readonly InMemoryAgentMemoryStore _store = new();

    [Fact]
    public async Task Repo_memories_round_trip()
    {
        var written = new List<string> { "Repository uses xUnit" };

        await _store.SaveRepoMemoriesAsync("octo/repo", written, CancellationToken.None);
        var loaded = await _store.LoadRepoMemoriesAsync("octo/repo", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Should().Equal("Repository uses xUnit");
    }

    [Fact]
    public async Task Task_memories_round_trip()
    {
        var written = new List<string> { "Avoid editing generated migrations" };

        await _store.SaveTaskMemoriesAsync("PVTI_1", written, CancellationToken.None);
        var loaded = await _store.LoadTaskMemoriesAsync("PVTI_1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Should().Equal("Avoid editing generated migrations");
    }

    [Fact]
    public async Task Load_returns_null_when_absent()
    {
        (await _store.LoadRepoMemoriesAsync("missing", CancellationToken.None)).Should().BeNull();
        (await _store.LoadTaskMemoriesAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Repo_and_task_namespaces_do_not_collide_on_same_id()
    {
        await _store.SaveRepoMemoriesAsync("same-id", new List<string> { "repo-fact" }, CancellationToken.None);
        await _store.SaveTaskMemoriesAsync("same-id", new List<string> { "task-fact" }, CancellationToken.None);

        (await _store.LoadRepoMemoriesAsync("same-id", CancellationToken.None))!.Should().Equal("repo-fact");
        (await _store.LoadTaskMemoriesAsync("same-id", CancellationToken.None))!.Should().Equal("task-fact");
    }

    [Fact]
    public async Task Save_takes_a_defensive_copy()
    {
        var written = new List<string> { "first" };
        await _store.SaveRepoMemoriesAsync("octo/repo", written, CancellationToken.None);

        written.Add("mutated-after-save");

        var loaded = await _store.LoadRepoMemoriesAsync("octo/repo", CancellationToken.None);
        loaded!.Should().Equal("first");
    }

    [Fact]
    public async Task Delete_removes_the_record()
    {
        await _store.SaveTaskMemoriesAsync("PVTI_1", new List<string> { "x" }, CancellationToken.None);

        await _store.DeleteTaskMemoriesAsync("PVTI_1", CancellationToken.None);

        (await _store.LoadTaskMemoriesAsync("PVTI_1", CancellationToken.None)).Should().BeNull();
    }
}
