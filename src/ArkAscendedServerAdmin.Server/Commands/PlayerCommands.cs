using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Players;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>
/// Guarded facade for the Players page and the instance Players tab. The table is fed by
/// <see cref="IPlayerTracker"/> from the game log and the health probe; this reads it, asks one instance who is on,
/// and forgets rows.
/// </summary>
public sealed class PlayerCommands(
    IAuthorizationGuard guard,
    IDbContextFactory<AppDbContext> contextFactory,
    IAppSettingsStore settings,
    IProcessManager processManager,
    IGeneratedConfigWriter generatedConfig,
    IRconClient rcon,
    IPlayerTracker tracker,
    TimeProvider timeProvider,
    ILogger<PlayerCommands> logger) : IPlayerCommands
{
    public async Task<IReadOnlyList<KnownPlayer>> ListAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.KnownPlayers.AsNoTracking().Include(p => p.LastInstance).OrderBy(p => p.Name).ToListAsync(cancellationToken);
    }

    public async Task<CommandResult<OnlinePlayers>> ListOnlineAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);

        // The session is read before the command goes out, so a reply that arrives after a restart is
        // recognized as belonging to the process that has gone and is dropped by the tracker.
        var runtime = processManager.GetRuntime(instanceId);
        if (runtime.State != InstanceState.Running)
        {
            return CommandResult<OnlinePlayers>.Fail("The instance is not running, so there is no server to ask.");
        }

        string? slug;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            slug = await db.Instances.AsNoTracking().Where(i => i.Id == instanceId).Select(i => i.Slug).SingleOrDefaultAsync(cancellationToken);
        }

        if (slug is null)
        {
            return CommandResult<OnlinePlayers>.Fail("The instance no longer exists.");
        }

        var generated = await generatedConfig.ReadGeneratedGameUserSettingsAsync(slug, cancellationToken);
        var endpoint = generated is null ? null : RconCredentials.TryRead(generated, out _);
        if (endpoint is null)
        {
            return CommandResult<OnlinePlayers>.Fail("RCON credentials could not be read from the generated GameUserSettings.ini.");
        }

        try
        {
            var timeout = TimeSpan.FromSeconds((await settings.GetAsync(cancellationToken)).RconCommandTimeoutSeconds);
            var sentAt = timeProvider.GetUtcNow();
            var reply = await rcon.ExecuteAsync(endpoint, RconCommands.ListPlayers, timeout, cancellationToken);
            var players = ListPlayersParser.Parse(reply);
            if (runtime.Pid is { } pid && runtime.ProcessStartTime is { } startTime)
            {
                await tracker.RecordListedAsync(new ProbeObservation(instanceId, pid, startTime, sentAt, reply), cancellationToken);
            }
            else
            {
                logger.LogDebug("Instance {InstanceId} is running without a full session identity, so its reply was reported but not recorded.", instanceId);
            }

            return CommandResult<OnlinePlayers>.Ok(new OnlinePlayers(players, timeProvider.GetUtcNow()));
        }
        catch (RconException ex)
        {
            logger.LogWarning(ex, "ListPlayers on instance {InstanceId} failed.", instanceId);
            return CommandResult<OnlinePlayers>.Fail($"RCON {ex.Failure.ToString().ToLowerInvariant()} failure: {ex.Message}");
        }
    }

    public async Task<CommandResult> DeleteAsync(int knownPlayerId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.KnownPlayers.SingleOrDefaultAsync(p => p.Id == knownPlayerId, cancellationToken);
        if (row is not null)
        {
            db.KnownPlayers.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        return CommandResult.Ok;
    }
}
