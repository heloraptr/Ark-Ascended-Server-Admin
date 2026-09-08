using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Guarded facade for the Known Players page (plan step 31): on-demand <c>ListPlayers</c> across running instances.</summary>
public sealed class PlayerCommands(
    IAuthorizationGuard guard,
    IDbContextFactory<AppDbContext> contextFactory,
    IAppSettingsStore settings,
    IProcessManager processManager,
    IGeneratedConfigWriter generatedConfig,
    IRconClient rcon,
    TimeProvider timeProvider,
    ILogger<PlayerCommands> logger) : IPlayerCommands
{
    public async Task<IReadOnlyList<KnownPlayer>> ListAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.KnownPlayers.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
    }

    public async Task<CommandResult<PlayerRefreshResult>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);

        var running = processManager.GetAllRuntimes().Where(r => r.State == InstanceState.Running).Select(r => r.InstanceId).ToList();
        if (running.Count == 0)
        {
            return CommandResult<PlayerRefreshResult>.Fail("No instance is running. ListPlayers needs a live server to ask.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var instances = await db.Instances.AsNoTracking().Where(i => running.Contains(i.Id)).ToListAsync(cancellationToken);
        var timeout = TimeSpan.FromSeconds((await settings.GetAsync(cancellationToken)).RconCommandTimeoutSeconds);
        var now = timeProvider.GetUtcNow();
        var notes = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in instances.OrderBy(i => i.Name))
        {
            var generated = await generatedConfig.ReadGeneratedGameUserSettingsAsync(instance.Slug, cancellationToken);
            var endpoint = generated is null ? null : RconCredentials.TryRead(generated, out _);
            if (endpoint is null)
            {
                notes.Add($"{instance.Name}: RCON credentials could not be read from the generated GameUserSettings.ini.");
                continue;
            }

            try
            {
                var reply = await rcon.ExecuteAsync(endpoint, RconCommands.ListPlayers, timeout, cancellationToken);
                var players = ListPlayersParser.Parse(reply);
                foreach (var player in players)
                {
                    seen[player.EosId] = player.Name;
                }

                notes.Add(players.Count == 0
                    ? $"{instance.Name}: no players connected."
                    : $"{instance.Name}: {players.Count} player{(players.Count == 1 ? string.Empty : "s")}.");
            }
            catch (RconException ex)
            {
                logger.LogWarning(ex, "ListPlayers on {Instance} failed.", instance.Name);
                notes.Add($"{instance.Name}: RCON {ex.Failure.ToString().ToLowerInvariant()} failure: {ex.Message}");
            }
        }

        if (seen.Count > 0)
        {
            var ids = seen.Keys.ToList();
            var existing = await db.KnownPlayers.Where(p => ids.Contains(p.EosId)).ToListAsync(cancellationToken);
            foreach (var (eosId, name) in seen)
            {
                var row = existing.SingleOrDefault(p => p.EosId.Equals(eosId, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    db.KnownPlayers.Add(new KnownPlayer { Name = name, EosId = eosId, FirstSeenAt = now, LastSeenAt = now });
                }
                else
                {
                    row.Name = name;
                    row.LastSeenAt = now;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return CommandResult<PlayerRefreshResult>.Ok(new PlayerRefreshResult(seen.Count, notes));
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
