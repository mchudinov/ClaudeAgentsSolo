using Serilog.Events;

namespace Library.Logging;

/// <summary>
/// A flattened, render-ready snapshot of a single <see cref="LogEvent"/> captured by
/// <see cref="RecentLogBuffer"/>. Immutable so a dashboard can read it without holding
/// the buffer lock.
/// </summary>
public sealed record RecentLogEntry(
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Message,
    string? Exception);
