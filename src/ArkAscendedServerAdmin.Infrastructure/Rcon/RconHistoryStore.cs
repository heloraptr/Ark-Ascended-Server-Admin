using System.Text;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Rcon;

/// <summary>
/// The console history file of each instance (<see cref="DataRootLayout.InstanceRconHistoryPath"/>). Every append
/// rereads the file under the instance lease, so two browser tabs typing into the same console both land in it and
/// a delete never races a write. The lease is held only for the file work, never across the RCON send; when a
/// lifecycle operation holds it for longer than <c>leaseWait</c>, the command is simply not recorded. Writes go to a
/// temp file that then replaces the original, so a crash never leaves half a file.
/// </summary>
public sealed class RconHistoryStore(DataRootLayout layout, IInstanceLocks locks, ILogger<RconHistoryStore> logger, TimeSpan? leaseWait = null) : IRconHistoryStore
{
    /// <summary>How long an append waits for the instance lease; short, because the user is waiting on the page.</summary>
    public static readonly TimeSpan DefaultLeaseWait = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan LeasePoll = TimeSpan.FromMilliseconds(20);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeSpan _leaseWait = leaseWait ?? DefaultLeaseWait;

    public async Task<IReadOnlyList<string>> LoadAsync(string instanceSlug, CancellationToken cancellationToken = default) =>
        await ReadAsync(PathOf(instanceSlug), cancellationToken);

    public async Task<IReadOnlyList<string>?> AppendAsync(int instanceId, string instanceSlug, string command, CancellationToken cancellationToken = default)
    {
        var path = PathOf(instanceSlug);
        var lease = await TryLeaseAsync(instanceId, cancellationToken);
        if (lease is null)
        {
            logger.LogDebug("RCON history of instance {InstanceId} not updated: an operation holds the instance.", instanceId);
            return null;
        }

        using (lease)
        {
            var history = await ReadAsync(path, cancellationToken);

            // A deleted instance has no folder; recreating it here would leave an orphan behind the delete.
            if (!Directory.Exists(Path.GetDirectoryName(path)) || !RconHistory.Append(history, command))
            {
                return history;
            }

            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                var text = string.Concat(history.Select(line => line + "\r\n"));
                await File.WriteAllTextAsync(temp, text, Utf8NoBom, cancellationToken);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }

            return history;
        }
    }

    /// <summary>
    /// Polls <see cref="IInstanceLocks.TryAcquire"/> rather than queueing on <see cref="IInstanceLocks.AcquireAsync"/>:
    /// a queued waiter is handed the lock ahead of every non-waiting caller, and start, stop, and delete only ever try
    /// once, so history writes must never stand in line in front of them.
    /// </summary>
    private async Task<IInstanceLease?> TryLeaseAsync(int instanceId, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)_leaseWait.TotalMilliseconds;
        while (true)
        {
            if (locks.TryAcquire(instanceId) is { } lease)
            {
                return lease;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(LeasePoll, cancellationToken);
        }
    }

    /// <summary>The last <see cref="RconHistory.MaxBytesRead"/> bytes of the file, normalized; empty when it does not exist.</summary>
    private static async Task<List<string>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] buffer;
        long start;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            start = Math.Max(0, stream.Length - RconHistory.MaxBytesRead);
            stream.Seek(start, SeekOrigin.Begin);
            buffer = new byte[stream.Length - start];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }

        var text = Utf8NoBom.GetString(buffer);
        if (start == 0)
        {
            text = text.TrimStart('﻿');
        }

        IEnumerable<string> lines = text.Split('\n');
        if (start > 0)
        {
            lines = lines.Skip(1); // the read began inside a line
        }

        return RconHistory.Normalize(lines.Select(line => line.TrimEnd('\r')));
    }

    private string PathOf(string instanceSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSlug);
        if (instanceSlug is "." or ".." || !string.Equals(Path.GetFileName(instanceSlug), instanceSlug, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{instanceSlug}' is not a plain instance slug.", nameof(instanceSlug));
        }

        return layout.InstanceRconHistoryPath(instanceSlug);
    }
}
