using Microsoft.Extensions.Options;

namespace Agent.Sandbox;

/// <summary>
/// <see cref="DelegatingHandler"/> that rejects outbound HTTP requests whose
/// <see cref="Uri.Host"/> is not present in <see cref="SandboxOptions.AllowedHosts"/>.
/// Throws <see cref="SandboxViolationException"/> on a miss so a denied egress
/// surfaces identically to a denied command or path.
/// </summary>
/// <remarks>
/// The handler is intentionally an <em>egress</em> control, not an authorization
/// layer — it does not inspect headers, paths, or methods. Any caller that needs
/// host-level isolation places this handler in front of its <c>HttpClient</c>.
/// </remarks>
public sealed class HostAllowlistHandler : DelegatingHandler
{
    private readonly IReadOnlyList<HostPattern> _patterns;

    /// <summary>
    /// Constructor for production DI: reads the host list from
    /// <see cref="SandboxOptions"/> and falls back to no inner handler so the
    /// runtime substitutes <see cref="HttpClientHandler"/> automatically.
    /// </summary>
    public HostAllowlistHandler(IOptions<SandboxOptions> options)
        : this(options.Value.AllowedHosts)
    {
    }

    /// <summary>
    /// Constructor for tests: takes an explicit host list and lets the caller
    /// install an inner handler via the standard <see cref="DelegatingHandler.InnerHandler"/>
    /// property after construction. Internal so the DI activator only sees the
    /// single public ctor above — having both ctors public would make
    /// <c>ServiceProvider.GetRequiredService&lt;HostAllowlistHandler&gt;</c> ambiguous
    /// when the type is resolved via <c>AddHttpMessageHandler&lt;T&gt;</c>.
    /// </summary>
    internal HostAllowlistHandler(IEnumerable<string> allowedHosts)
    {
        _patterns = (allowedHosts ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(HostPattern.Compile)
            .ToArray();
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null)
            throw new SandboxViolationException("HTTP request has no URI; egress allowlist cannot evaluate it.");

        if (!IsAllowed(uri.Host))
            throw new SandboxViolationException(
                $"outbound host '{uri.Host}' is denied by sandbox policy: not in AllowedHosts.");

        return base.SendAsync(request, cancellationToken);
    }

    internal bool IsAllowed(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var pattern in _patterns)
        {
            if (pattern.Matches(host)) return true;
        }
        return false;
    }

    private readonly record struct HostPattern(string Value, bool IsWildcard)
    {
        public static HostPattern Compile(string raw)
        {
            // Trim and lowercase for canonical comparison — DNS is case-insensitive.
            var canonical = raw.Trim().ToLowerInvariant();
            if (canonical.StartsWith("*.", StringComparison.Ordinal))
            {
                // "*.foo.com" matches anything that ends with ".foo.com"
                return new HostPattern(canonical[1..], IsWildcard: true);
            }
            return new HostPattern(canonical, IsWildcard: false);
        }

        public bool Matches(string host)
        {
            var lower = host.ToLowerInvariant();
            if (IsWildcard)
            {
                // Value here is ".foo.com" — must end with it AND be longer (i.e. have a subdomain)
                return lower.Length > Value.Length
                       && lower.EndsWith(Value, StringComparison.Ordinal);
            }
            return string.Equals(lower, Value, StringComparison.Ordinal);
        }
    }
}
