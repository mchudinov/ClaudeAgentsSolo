using Serilog.Core;
using Serilog.Events;

namespace DeveloperAgent.Dashboard;

/// <summary>
/// Fixed-capacity ring buffer that doubles as a Serilog <see cref="ILogEventSink"/> and
/// the dashboard's <see cref="IRecentLogBuffer"/> read service. A <b>single instance</b>
/// must be registered for both roles: Serilog writes into it via <see cref="Emit"/> and
/// the dashboard reads it via <see cref="Recent"/>. Registering separate instances would
/// leave the dashboard reading an empty buffer.
/// </summary>
/// <remarks>
/// Serilog emits from arbitrary threads while the dashboard reads concurrently, so every
/// access is guarded by a single lock around the backing array. The buffer keeps only the
/// most recent <c>capacity</c> events; older events are overwritten.
/// </remarks>
public sealed class RecentLogBuffer : IRecentLogBuffer, ILogEventSink
{
    private readonly Lock _lock = new();
    private readonly RecentLogEntry?[] _ring;
    private readonly int _capacity;
    private int _count;
    private int _next;

    /// <summary>Creates a buffer that retains the most recent <paramref name="capacity"/> events.</summary>
    public RecentLogBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _ring = new RecentLogEntry?[capacity];
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var entry = new RecentLogEntry(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        lock (_lock)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<RecentLogEntry> Recent(int? take = null)
    {
        lock (_lock)
        {
            var ordered = new List<RecentLogEntry>(_count);
            // Oldest entry sits at (_next - _count) modulo capacity once the ring wraps.
            var start = (_next - _count + _capacity) % _capacity;
            for (var i = 0; i < _count; i++)
            {
                var entry = _ring[(start + i) % _capacity];
                if (entry is not null)
                {
                    ordered.Add(entry);
                }
            }

            if (take is int n && n >= 0 && n < ordered.Count)
            {
                return ordered.GetRange(ordered.Count - n, n);
            }

            return ordered;
        }
    }
}
