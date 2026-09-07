using ArkAscendedServerAdmin.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.Data;

/// <summary>
/// Key/value-backed <see cref="IAppSettingsStore"/>. Reads are cached in memory after the first load and
/// invalidated by <see cref="SaveAsync"/>, so hot paths (the CurseForge key handler, port allocation) do
/// not hit the database on every call.
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
            if (_cached is { } cachedAfterWait)
            {
                return cachedAfterWait;
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.AppSettings.AsNoTracking().ToDictionaryAsync(r => r.Key, r => r.Value, StringComparer.Ordinal, cancellationToken);
            _cached = AppSettingsCodec.Decode(rows);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.AppSettings.ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);

            foreach (var (key, value) in AppSettingsCodec.Encode(settings))
            {
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
            _cached = settings;
        }
        finally
        {
            _gate.Release();
        }
    }
}
