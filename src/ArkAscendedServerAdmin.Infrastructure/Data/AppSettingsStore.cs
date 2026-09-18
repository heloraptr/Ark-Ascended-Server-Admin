using ArkAscendedServerAdmin.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.Data;

/// <summary>
/// Key/value-backed <see cref="IAppSettingsStore"/>. Reads are cached in memory after the first load and
/// refreshed by every write, so hot paths (the CurseForge key handler, port allocation) do not hit the
/// database on every call. Writes are serialized by one semaphore: <see cref="SaveAsync"/> checks the
/// snapshot's version against the store's, <see cref="UpdateAsync"/> runs its mutation inside the lock, and
/// both write only the rows that changed plus the bumped <c>Version</c> row.
/// </summary>
public sealed class AppSettingsStore(IDbContextFactory<AppDbContext> contextFactory) : IAppSettingsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _cached;

    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfInvalid(settings);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadLockedAsync(cancellationToken);
            if (settings.Version != current.Version)
            {
                throw new AppSettingsConflictException(settings.Version, current.Version);
            }

            if (settings != current)
            {
                await WriteLockedAsync(current, settings, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadLockedAsync(cancellationToken);
            var next = mutate(current) with { Version = current.Version };
            if (next == current)
            {
                return current;
            }

            ThrowIfInvalid(next);
            return await WriteLockedAsync(current, next, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AppSettings.AsNoTracking().ToDictionaryAsync(r => r.Key, r => r.Value, StringComparer.Ordinal, cancellationToken);
        _cached = AppSettingsCodec.Decode(rows);
        return _cached;
    }

    /// <summary>Writes the rows whose value differs from <paramref name="current"/> plus the bumped version, and caches the result.</summary>
    private async Task<AppSettings> WriteLockedAsync(AppSettings current, AppSettings next, CancellationToken cancellationToken)
    {
        var written = next with { Version = current.Version + 1 };
        var before = AppSettingsCodec.Encode(current);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AppSettings.ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);
        foreach (var (key, value) in AppSettingsCodec.Encode(written))
        {
            if (before.TryGetValue(key, out var previous) && previous == value && existing.ContainsKey(key))
            {
                continue;
            }

            if (existing.TryGetValue(key, out var row))
            {
                row.Value = value;
            }
            else
            {
                db.AppSettings.Add(new AppSettingRow { Key = key, Value = value });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _cached = written;
        return written;
    }

    private static void ThrowIfInvalid(AppSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }
    }
}
