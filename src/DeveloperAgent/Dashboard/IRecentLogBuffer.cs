namespace DeveloperAgent.Dashboard;

/// <summary>
/// Read side of the in-memory recent-log ring buffer the operator dashboard renders.
/// The concrete <see cref="RecentLogBuffer"/> also implements Serilog's
/// <c>ILogEventSink</c>; the same instance is registered for both roles so the
/// dashboard observes the events the application actually logged.
/// </summary>
public interface IRecentLogBuffer
{
    /// <summary>
    /// Returns the most recent entries in chronological (oldest-first) order. When
    /// <paramref name="take"/> is supplied, returns at most that many of the newest
    /// entries (still oldest-first). The result is a detached snapshot.
    /// </summary>
    IReadOnlyList<RecentLogEntry> Recent(int? take = null);
}
