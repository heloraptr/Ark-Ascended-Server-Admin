using System.Globalization;
using System.Threading.Channels;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Players;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Players;

/// <summary>
/// The one writer of the known players table. Subscribes to every instance console (the log tail feeds
/// it, live and backfilled lines alike), to the health probe's <c>ListPlayers</c> replies, and to runtime
/// changes; work is queued onto a single-reader channel so join and leave lines apply in the order the
/// game wrote them, and the console event never waits on the database. Timestamps come from the line's
/// own UTC stamp, or from the moment a reply was sent, so a backfill replayed after a service restart
/// records when things happened, not when they were read; evidence older than what the row already knows
/// is ignored, which makes a replay idempotent.
/// </summary>
public sealed class PlayerTracker(
    IDbContextFactory<AppDbContext> contextFactory,
    IConsoleService console,
    IProcessManager processManager,
    ILogger<PlayerTracker> logger) : IPlayerTracker, IHostedService, IDisposable
{
    private const string ChannelPrefix = "instance:";

    private readonly Channel<Func<CancellationToken, Task>> _work = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        console.LineAppended += OnLineAppended;
        processManager.RuntimeChanged += OnRuntimeChanged;
        processManager.ProbeObserved += OnProbeObserved;
        _loop = Task.Run(() => DrainAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        console.LineAppended -= OnLineAppended;
        processManager.RuntimeChanged -= OnRuntimeChanged;
        processManager.ProbeObserved -= OnProbeObserved;
        _work.Writer.TryComplete();
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _stopping.Cancel();
            }
        }
    }

    public async Task<bool> RecordListedAsync(ProbeObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var runtime = processManager.GetRuntime(observation.InstanceId);
        if (!runtime.HasLiveProcess || runtime.Pid != observation.Pid || runtime.ProcessStartTime != observation.ProcessStartTime)
        {
            logger.LogDebug(
                "Dropped a ListPlayers reply from pid {Pid} on instance {InstanceId}: it is no longer the live session.",
                observation.Pid,
                observation.InstanceId);
            return false;
        }

        var listed = ListPlayersParser.Parse(observation.Reply);
        var sentAt = observation.SentAt;
        var changed = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = listed.Select(p => p.EosId.ToLowerInvariant()).ToList();
            var rows = await db.KnownPlayers
                .Where(p => ids.Contains(p.EosId.ToLower()) || (p.LastInstanceId == observation.InstanceId && p.IsOnline))
                .ToListAsync(cancellationToken);

            var changes = PresenceSnapshot.Decide(observation.InstanceId, sentAt, listed, rows);
            foreach (var newcomer in changes.Newcomers)
            {
                db.KnownPlayers.Add(new KnownPlayer
                {
                    Name = newcomer.Name,
                    EosId = newcomer.EosId,
                    FirstSeenAt = sentAt,
                    LastSeenAt = sentAt,
                    IsOnline = true,
                    LastInstanceId = observation.InstanceId,
                });
            }

            foreach (var (row, player) in changes.Online)
            {
                row.Name = player.Name;
                row.LastSeenAt = sentAt;
                row.IsOnline = true;
                row.LastInstanceId = observation.InstanceId;
            }

            foreach (var row in changes.Offline)
            {
                // When they left is unknown, so LastLeftAt stays put; LastSeenAt moves because the reply is
                // newer evidence about the player than whatever the row held.
                row.LastSeenAt = sentAt;
                row.IsOnline = false;
            }

            changed = !changes.IsEmpty && await db.SaveChangesAsync(cancellationToken) > 0;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            RaiseChanged();
        }

        return true;
    }

    /// <summary>Applies one join or leave event; false when it was older than the row's latest evidence and skipped.</summary>
    public async Task<bool> ApplyAsync(int instanceId, PlayerLogEvent logEvent, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var at = logEvent.At ?? observedAt;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var id = logEvent.EosId.ToLowerInvariant();
            var row = await db.KnownPlayers.SingleOrDefaultAsync(p => p.EosId.ToLower() == id, cancellationToken);
            if (row is null)
            {
                row = new KnownPlayer { Name = logEvent.Name, EosId = logEvent.EosId, FirstSeenAt = at, LastSeenAt = at };
                db.KnownPlayers.Add(row);
            }
            else if (at < row.LastSeenAt)
            {
                return false;
            }

            row.Name = logEvent.Name;
            row.Platform = logEvent.Platform;
            row.LastInstanceId = instanceId;
            row.LastSeenAt = at;
            if (logEvent.Presence == PlayerPresence.Joined)
            {
                row.LastJoinedAt = at;
                row.IsOnline = true;
            }
            else
            {
                row.LastLeftAt = at;
                row.IsOnline = false;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>The instance's process is gone, so nobody is on it; when they left is unknown, so only the flag changes.</summary>
    public async Task<int> MarkOfflineAsync(int instanceId, CancellationToken cancellationToken)
    {
        int changed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            changed = await db.KnownPlayers
                .Where(p => p.LastInstanceId == instanceId && p.IsOnline)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.IsOnline, false), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        if (changed > 0)
        {
            RaiseChanged();
        }

        return changed;
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _gate.Dispose();
    }

    private void OnLineAppended(string channel, ConsoleLine line)
    {
        if (line.Kind is not (ConsoleLineKind.Output or ConsoleLineKind.Backfill) || TryInstanceId(channel) is not { } instanceId)
        {
            return;
        }

        if (PlayerLogLines.TryParse(line.Text) is not { } logEvent)
        {
            return;
        }

        _work.Writer.TryWrite(token => ApplyAsync(instanceId, logEvent, line.At, token));
    }

    private void OnProbeObserved(ProbeObservation observation) =>
        _work.Writer.TryWrite(token => RecordListedAsync(observation, token));

    private void OnRuntimeChanged(InstanceRuntime runtime)
    {
        if (!runtime.HasLiveProcess)
        {
            _work.Writer.TryWrite(token => MarkOfflineAsync(runtime.InstanceId, token));
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in _work.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await job(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "A player tracking update failed; the table may lag until the next join or leave.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service stop.
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A player tracker subscriber threw.");
        }
    }

    private static int? TryInstanceId(string channel) =>
        channel.StartsWith(ChannelPrefix, StringComparison.Ordinal)
        && int.TryParse(channel.AsSpan(ChannelPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
