using System.Net;
using System.Net.Http;
using DeveloperAgent.Sandbox;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Tests for <see cref="HostAllowlistHandler"/>. Each test wires the handler in
/// front of a <see cref="StubHandler"/> that returns HTTP 200 — that way an
/// allowed request reaches the stub (and we observe the response) while a denied
/// request throws <see cref="SandboxViolationException"/> from the handler.
/// </summary>
public sealed class HostAllowlistHandlerTests
{
    private static HttpClient BuildClient(HostAllowlistHandler handler, StubHandler stub)
    {
        handler.InnerHandler = stub;
        return new HttpClient(handler);
    }

    private static HostAllowlistHandler BuildHandler(params string[] hosts) =>
        new(hosts);

    // ── Allowed host: request reaches the inner handler ─────────────────────

    [Fact]
    public async Task Exact_host_match_passes_through_to_inner_handler()
    {
        var stub = new StubHandler();
        var handler = BuildHandler("api.anthropic.com");
        using var client = BuildClient(handler, stub);

        var response = await client.GetAsync("https://api.anthropic.com/v1/messages");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Wildcard_subdomain_match_passes_through()
    {
        var stub = new StubHandler();
        var handler = BuildHandler("*.githubusercontent.com");
        using var client = BuildClient(handler, stub);

        var response = await client.GetAsync("https://raw.githubusercontent.com/owner/repo/main/README.md");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Host_match_is_case_insensitive()
    {
        var stub = new StubHandler();
        var handler = BuildHandler("api.github.com");
        using var client = BuildClient(handler, stub);

        var response = await client.GetAsync("https://API.GITHUB.COM/user");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Denied host: handler throws SandboxViolationException ───────────────

    [Fact]
    public async Task Disallowed_host_throws_SandboxViolationException()
    {
        var stub = new StubHandler();
        var handler = BuildHandler("api.anthropic.com");
        using var client = BuildClient(handler, stub);

        var act = async () => await client.GetAsync("https://evil.example.com/x");

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*evil.example.com*");
        stub.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Disallowed_request_does_not_invoke_inner_handler()
    {
        var stub = new StubHandler();
        var handler = BuildHandler("api.github.com");
        using var client = BuildClient(handler, stub);

        try
        {
            await client.GetAsync("https://blocked.test/anything");
        }
        catch (SandboxViolationException) { /* expected */ }

        stub.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Wildcard_does_not_match_apex_domain()
    {
        // "*.githubusercontent.com" should NOT match "githubusercontent.com" itself —
        // the leading "*." requires at least one subdomain label.
        var stub = new StubHandler();
        var handler = BuildHandler("*.githubusercontent.com");
        using var client = BuildClient(handler, stub);

        var act = async () => await client.GetAsync("https://githubusercontent.com/");

        await act.Should().ThrowAsync<SandboxViolationException>();
    }

    [Fact]
    public async Task Wildcard_does_not_match_unrelated_suffix()
    {
        // "*.githubusercontent.com" should not match "evil-githubusercontent.com"
        // (no separator). We require the host to end with ".githubusercontent.com".
        var stub = new StubHandler();
        var handler = BuildHandler("*.githubusercontent.com");
        using var client = BuildClient(handler, stub);

        var act = async () => await client.GetAsync("https://evilgithubusercontent.com/");

        await act.Should().ThrowAsync<SandboxViolationException>();
    }

    // ── Empty allowlist: everything is denied ───────────────────────────────

    [Fact]
    public async Task Empty_allowlist_denies_all_requests()
    {
        var stub = new StubHandler();
        var handler = BuildHandler(); // no hosts
        using var client = BuildClient(handler, stub);

        var act = async () => await client.GetAsync("https://api.anthropic.com/");

        await act.Should().ThrowAsync<SandboxViolationException>();
        stub.RequestCount.Should().Be(0);
    }

    // ── Options-bound constructor mirrors the explicit-list constructor ─────

    [Fact]
    public async Task Constructor_bound_to_SandboxOptions_uses_AllowedHosts_list()
    {
        var stub = new StubHandler();
        var handler = new HostAllowlistHandler(Options.Create(new SandboxOptions
        {
            AllowedHosts = ["api.anthropic.com"],
        }));
        using var client = BuildClient(handler, stub);

        var act = async () => await client.GetAsync("https://api.github.com/");

        await act.Should().ThrowAsync<SandboxViolationException>();
    }

    [Fact]
    public async Task Constructor_bound_to_SandboxOptions_default_allows_anthropic()
    {
        var stub = new StubHandler();
        var handler = new HostAllowlistHandler(Options.Create(new SandboxOptions()));
        using var client = BuildClient(handler, stub);

        var response = await client.GetAsync("https://api.anthropic.com/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Constructor_bound_to_SandboxOptions_default_allows_github_api()
    {
        var stub = new StubHandler();
        var handler = new HostAllowlistHandler(Options.Create(new SandboxOptions()));
        using var client = BuildClient(handler, stub);

        var response = await client.GetAsync("https://api.github.com/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Constructor_bound_to_SandboxOptions_default_allows_context7()
    {
        var stub = new StubHandler();
        var handler = new HostAllowlistHandler(Options.Create(new SandboxOptions()));
        using var client = BuildClient(handler, stub);

        var response = await client.GetAsync("https://context7.com/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Constructor_bound_to_SandboxOptions_default_allows_raw_githubusercontent()
    {
        var stub = new StubHandler();
        var handler = new HostAllowlistHandler(Options.Create(new SandboxOptions()));
        using var client = BuildClient(handler, stub);

        var response = await client.GetAsync("https://raw.githubusercontent.com/o/r/main/f");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Minimal <see cref="HttpMessageHandler"/> that returns 200 OK and counts invocations.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            });
        }
    }
}
