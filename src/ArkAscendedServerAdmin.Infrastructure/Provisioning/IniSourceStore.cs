using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Provisioning;

/// <summary>
/// The on-disk half of the INI pipeline (plan step 16): canonical source text at
/// <c>Clusters\&lt;slug&gt;\Config\</c> or <c>Instances\&lt;slug&gt;\Config\</c> plus the <c>IniDocuments</c>
/// mirror. One <see cref="SemaphoreSlim"/> per (owner, file) serializes access; saves are optimistic on the
/// SHA-256 of the text and go through <see cref="AtomicFile"/>. The file is authoritative — a mirror
/// failure is reported, never allowed to fail the save — and <see cref="RestoreFromDatabaseAsync"/> is the
/// only database → disk direction.
/// </summary>
public sealed class IniSourceStore(
    DataRootLayout layout,
    IDbContextFactory<AppDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<IniSourceStore> logger) : IIniSourceStore
{
    /// <summary>The rejection the editor shows when the file changed underneath it.</summary>
    public const string ChangedSinceOpened = "The file changed since you opened it — reload before saving.";

    private readonly ConcurrentDictionary<(bool IsCluster, int Id, IniFile File), SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<(bool IsCluster, int Id), string> _slugs = new();

    /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="text"/> (no byte-order mark).</summary>
    public static string ComputeSha256(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public async Task<IniSourceDocument> LoadAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken)
    {
        var path = await ResolvePathAsync(owner, file, cancellationToken);
        var gate = Gate(owner, file);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (text, sha256, updatedAt) = await ReadFileAsync(path, cancellationToken);
            var mirrorStale = await IsMirrorStaleAsync(owner, file, sha256, cancellationToken);
            return new IniSourceDocument(text, sha256, updatedAt, mirrorStale);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IniSaveResult> SaveAsync(IniOwner owner, IniFile file, string text, string expectedSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(expectedSha256);

        var path = await ResolvePathAsync(owner, file, cancellationToken);
        var gate = Gate(owner, file);
        await gate.WaitAsync(cancellationToken);
        try
        {
            // The slug is cached, so an editor left open across a delete would otherwise still resolve a path and
            // recreate the deleted owner's Config folder. Checked under the gate, before anything touches the disk.
            if (!await OwnerExistsAsync(owner, cancellationToken))
            {
                return IniSaveResult.Rejected($"This {(owner.IsCluster ? "cluster" : "instance")} was deleted while the editor was open.");
            }

            var (currentText, currentSha256, _) = await ReadFileAsync(path, cancellationToken);
            if (!HashEquals(currentSha256, expectedSha256))
            {
                return IniSaveResult.Rejected(ChangedSinceOpened);
            }

            // Every caller's text is written in the line endings of the file it replaces (CRLF for a new file), so a
            // browser edit, which always arrives as LF, changes only the lines that were edited. The hash and the
            // mirror are of exactly what lands on disk.
            var written = IniLineEndings.Apply(text, IniLineEndings.Detect(currentText));
            var newSha256 = ComputeSha256(written);
            await AtomicFile.WriteAllTextAsync(path, written, cancellationToken);
            var mirrored = await TryMirrorAsync(owner, file, written, newSha256, cancellationToken);
            return IniSaveResult.Saved(newSha256, mirrorFailed: !mirrored, written);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IniSaveResult> RetryMirrorAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken)
    {
        var path = await ResolvePathAsync(owner, file, cancellationToken);
        var gate = Gate(owner, file);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (text, sha256, _) = await ReadFileAsync(path, cancellationToken);
            var mirrored = await TryMirrorAsync(owner, file, text, sha256, cancellationToken);
            return IniSaveResult.Saved(sha256, mirrorFailed: !mirrored);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RestoreFromDatabaseAsync(CancellationToken cancellationToken)
    {
        List<MirrorRow> rows;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            rows = await db.IniDocuments
                .AsNoTracking()
                .Select(d => new MirrorRow(
                    d.ClusterId,
                    d.InstanceId,
                    d.File,
                    d.Text,
                    d.Cluster != null ? d.Cluster.Slug : d.Instance != null ? d.Instance.Slug : null))
                .ToListAsync(cancellationToken);
        }

        foreach (var row in rows)
        {
            var owner = row.ClusterId is { } clusterId ? IniOwner.ForCluster(clusterId) : IniOwner.ForInstance(row.InstanceId!.Value);
            var slug = row.Slug ?? throw new InvalidOperationException($"IniDocuments row for {Describe(owner)} has no owner row.");
            _slugs[(owner.IsCluster, OwnerId(owner))] = slug;

            var path = PathFor(owner, slug, row.File);
            var gate = Gate(owner, row.File);
            await gate.WaitAsync(cancellationToken);
            try
            {
                await AtomicFile.WriteAllTextAsync(path, row.Text, cancellationToken);
            }
            finally
            {
                gate.Release();
            }

            logger.LogInformation("Restored {Path} from the database mirror.", path);
        }
    }

    /// <summary>
    /// False only when the database answers that the owner row is gone. A database that cannot be read counts as
    /// the owner existing: the file is authoritative, and a save must not fail because the mirror side is down.
    /// </summary>
    private async Task<bool> OwnerExistsAsync(IniOwner owner, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            return owner.ClusterId is { } clusterId
                ? await db.Clusters.AnyAsync(c => c.Id == clusterId, cancellationToken)
                : await db.Instances.AnyAsync(i => i.Id == owner.InstanceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not confirm that {Owner} still exists; saving to the file anyway.", Describe(owner));
            return true;
        }
    }

    private async Task<bool> IsMirrorStaleAsync(IniOwner owner, IniFile file, string fileSha256, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var mirrorSha256 = await Query(db, owner, file).Select(d => d.Sha256).SingleOrDefaultAsync(cancellationToken);
            return mirrorSha256 is null || !HashEquals(mirrorSha256, fileSha256);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the INI mirror for {Owner} {File}; reporting it as stale.", Describe(owner), file);
            return true;
        }
    }

    /// <summary>Upserts the mirror row, skipping the write when it already carries this hash. Returns false (and logs) on any storage failure.</summary>
    private async Task<bool> TryMirrorAsync(IniOwner owner, IniFile file, string text, string sha256, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await Query(db, owner, file).SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                row = new IniDocument { ClusterId = owner.ClusterId, InstanceId = owner.InstanceId, File = file };
                db.IniDocuments.Add(row);
            }
            else if (HashEquals(row.Sha256, sha256))
            {
                return true;
            }

            row.Text = text;
            row.Sha256 = sha256;
            row.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The INI mirror for {Owner} {File} could not be written; the file on disk is authoritative.", Describe(owner), file);
            return false;
        }
    }

    private async Task<(string Text, string Sha256, DateTimeOffset UpdatedAt)> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (string.Empty, ComputeSha256(string.Empty), timeProvider.GetUtcNow());
        }

        // ReadAllTextAsync strips a byte-order mark, so the hash is of the text alone.
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return (text, ComputeSha256(text), new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
    }

    private async Task<string> ResolvePathAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.ClusterId is null == owner.InstanceId is null)
        {
            throw new ArgumentException("Exactly one of ClusterId and InstanceId must be set.", nameof(owner));
        }

        var key = (owner.IsCluster, OwnerId(owner));
        if (!_slugs.TryGetValue(key, out var slug))
        {
            // Slugs are immutable, so a resolved slug can be cached for the life of the service.
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            slug = owner.ClusterId is { } clusterId
                ? await db.Clusters.Where(c => c.Id == clusterId).Select(c => c.Slug).SingleOrDefaultAsync(cancellationToken)
                : await db.Instances.Where(i => i.Id == owner.InstanceId).Select(i => i.Slug).SingleOrDefaultAsync(cancellationToken);
            if (slug is null)
            {
                throw new InvalidOperationException($"{Describe(owner)} does not exist.");
            }

            _slugs[key] = slug;
        }

        return PathFor(owner, slug, file);
    }

    private string PathFor(IniOwner owner, string slug, IniFile file)
    {
        var directory = owner.IsCluster ? layout.ClusterConfigSourceDirectory(slug) : layout.InstanceConfigSourceDirectory(slug);
        return Path.Combine(directory, IniFileNames.For(file));
    }

    private SemaphoreSlim Gate(IniOwner owner, IniFile file) =>
        _gates.GetOrAdd((owner.IsCluster, OwnerId(owner), file), _ => new SemaphoreSlim(1, 1));

    private static IQueryable<IniDocument> Query(AppDbContext db, IniOwner owner, IniFile file) =>
        owner.ClusterId is { } clusterId
            ? db.IniDocuments.Where(d => d.ClusterId == clusterId && d.File == file)
            : db.IniDocuments.Where(d => d.InstanceId == owner.InstanceId && d.File == file);

    private static int OwnerId(IniOwner owner) => owner.ClusterId ?? owner.InstanceId!.Value;

    private static string Describe(IniOwner owner) => owner.IsCluster ? $"cluster {owner.ClusterId}" : $"instance {owner.InstanceId}";

    private static bool HashEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <param name="ClusterId">Owner cluster id, when the document belongs to a cluster.</param>
    /// <param name="InstanceId">Owner instance id, when the document belongs to a standalone instance.</param>
    /// <param name="File">Which of the two files the row mirrors.</param>
    /// <param name="Text">The mirrored text.</param>
    /// <param name="Slug">The owner's slug, joined so the file path can be rebuilt.</param>
    private sealed record MirrorRow(int? ClusterId, int? InstanceId, IniFile File, string Text, string? Slug);
}
