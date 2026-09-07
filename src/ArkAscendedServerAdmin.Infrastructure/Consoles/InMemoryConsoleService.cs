using ArkAscendedServerAdmin.Consoles;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Consoles;

/// <summary>
/// The console buffer (plan step 22): one ring of <see cref="IConsoleService.Capacity"/> lines per
/// channel, guarded by a single lock. <see cref="LineAppended"/> is raised on the appending thread
/// outside the lock; a subscriber that throws is logged and does not affect the others.
/// </summary>
public sealed class InMemoryConsoleService(ILogger<InMemoryConsoleService> logger) : IConsoleService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<ConsoleLine>> _channels = new(StringComparer.Ordinal);

    public event Action<string, ConsoleLine>? LineAppended;

    public IReadOnlyList<ConsoleLine> Snapshot(string channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        lock (_gate)
        {
            return _channels.TryGetValue(channel, out var lines) ? [.. lines] : [];
        }
    }

    public void Append(string channel, ConsoleLine line)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(line);

        lock (_gate)
        {
            if (!_channels.TryGetValue(channel, out var lines))
            {
                lines = new Queue<ConsoleLine>(IConsoleService.Capacity);
                _channels[channel] = lines;
            }

            while (lines.Count >= IConsoleService.Capacity)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        var handlers = LineAppended;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string, ConsoleLine>)handler)(channel, line);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A console subscriber threw on channel {Channel}.", channel);
            }
        }
    }

    public void Clear(string channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        lock (_gate)
        {
            _channels.Remove(channel);
        }
    }
}
