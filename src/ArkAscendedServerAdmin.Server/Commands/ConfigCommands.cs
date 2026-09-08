using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Guarded facade over the INI source store and the per-instance extra overrides.</summary>
public sealed class ConfigCommands(
    IAuthorizationGuard guard,
    IDbContextFactory<AppDbContext> contextFactory,
    IIniSourceStore iniStore,
    ILogger<ConfigCommands> logger) : IConfigCommands
{
    public async Task<IniSourceDocument> LoadIniAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await iniStore.LoadAsync(owner, file, cancellationToken);
    }

    public async Task<IniSaveResult> SaveIniAsync(IniOwner owner, IniFile file, string text, string expectedSha256, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await iniStore.SaveAsync(owner, file, text, expectedSha256, cancellationToken);
    }

    public async Task<IniSaveResult> RetryMirrorAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await iniStore.RetryMirrorAsync(owner, file, cancellationToken);
    }

    public async Task<CommandResult> RestoreFromDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        try
        {
            await iniStore.RestoreFromDatabaseAsync(cancellationToken);
            return CommandResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "Restore from database failed.");
            return CommandResult.Fail($"Restore failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ExtraOverride>> GetOverridesAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExtraOverrides.AsNoTracking()
            .Where(o => o.InstanceId == instanceId)
            .OrderBy(o => o.File).ThenBy(o => o.Section).ThenBy(o => o.Key)
            .ToListAsync(cancellationToken);
    }

    public async Task<CommandResult<ExtraOverride>> SaveOverrideAsync(ExtraOverride entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await guard.EnsureAuthorizedAsync(cancellationToken);

        var problems = IniOverrideValidator.Validate(entry.Section, entry.Key, entry.Value);
        if (problems.Count > 0)
        {
            return CommandResult<ExtraOverride>.Fail(problems);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Instances.AnyAsync(i => i.Id == entry.InstanceId, cancellationToken))
        {
            return CommandResult<ExtraOverride>.Fail("The instance no longer exists.");
        }

        var section = entry.Section.Trim();
        var key = entry.Key.Trim();
        var duplicate = await db.ExtraOverrides.AnyAsync(
            o => o.InstanceId == entry.InstanceId && o.Id != entry.Id && o.File == entry.File && o.Section == section && o.Key == key,
            cancellationToken);
        if (duplicate)
        {
            return CommandResult<ExtraOverride>.Fail($"An override for [{section}] {key} in {entry.File}.ini already exists; edit that one instead.");
        }

        ExtraOverride row;
        if (entry.Id == 0)
        {
            row = new ExtraOverride { InstanceId = entry.InstanceId, File = entry.File, Section = section, Key = key, Value = entry.Value.Trim() };
            db.ExtraOverrides.Add(row);
        }
        else
        {
            row = await db.ExtraOverrides.SingleOrDefaultAsync(o => o.Id == entry.Id, cancellationToken)
                ?? throw new InvalidOperationException("The override was deleted while you were editing it.");
            row.File = entry.File;
            row.Section = section;
            row.Key = key;
            row.Value = entry.Value.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);
        return CommandResult<ExtraOverride>.Ok(row);
    }

    public async Task<CommandResult> DeleteOverrideAsync(int overrideId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ExtraOverrides.SingleOrDefaultAsync(o => o.Id == overrideId, cancellationToken);
        if (row is not null)
        {
            db.ExtraOverrides.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        return CommandResult.Ok;
    }
}
