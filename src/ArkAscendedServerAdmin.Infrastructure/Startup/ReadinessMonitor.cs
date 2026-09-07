using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Startup;

/// <summary>Singleton holding the current <see cref="ReadinessState"/>; written only by the orchestrator.</summary>
public sealed class ReadinessMonitor(TimeProvider timeProvider, ILogger<ReadinessMonitor> logger) : IReadinessMonitor
{
    private ReadinessState _current = new(ReadinessPhase.Initializing, "Starting", null, timeProvider.GetUtcNow());

    public ReadinessState Current => Volatile.Read(ref _current);

    public event Action<ReadinessState>? Changed;

    internal void Publish(ReadinessPhase phase, string message, string? error = null)
    {
        var state = new ReadinessState(phase, message, error, timeProvider.GetUtcNow());
        Volatile.Write(ref _current, state);

        if (error is null)
        {
            logger.LogInformation("Readiness: {Phase} — {Message}", phase, message);
        }
        else
        {
            logger.LogError("Readiness: {Phase} — {Message}: {Error}", phase, message, error);
        }

        try
        {
            Changed?.Invoke(state);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A readiness subscriber threw.");
        }
    }
}
