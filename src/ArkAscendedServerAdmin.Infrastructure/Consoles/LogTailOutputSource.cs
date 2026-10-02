using System.Text;
using ArkAscendedServerAdmin.Consoles;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Consoles;

/// <summary>
/// Polling tail of one <c>ShooterGame.log</c> (plan step 14; ported from the Phase 2 spike). Waits for the
/// file, opens it with <c>FileShare.ReadWrite | FileShare.Delete</c> once per poll, reads whatever
/// appeared after the offset, and splits on <c>\n</c> (trimming <c>\r</c>, stripping a BOM, carrying a
/// partial line to the next poll). Rotation is detected when the length drops below the offset or the
/// NTFS file id changes; the offset then restarts at 0 on the new file. Creation time is never consulted.
/// </summary>
public sealed class LogTailOutputSource(string logPath, TimeProvider timeProvider, ILogger logger, TimeSpan? pollInterval = null) : IOutputSource
{
    /// <summary>The Phase 2 spike measured ~200 ms event-to-console latency at this interval.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    private const int ReadChunkSize = 64 * 1024;
    private const int BackfillWindowBytes = 512 * 1024;

    private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;

    public string LogPath { get; } = logPath ?? throw new ArgumentNullException(nameof(logPath));

    /// <summary>Runs until canceled and then returns normally (it does not throw <see cref="OperationCanceledException"/>).</summary>
    public async Task RunAsync(OutputSourceOptions options, Func<OutputLine, ValueTask> onLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onLine);

        var state = new TailState();
        var buffer = new byte[ReadChunkSize];
        var announcedWait = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!File.Exists(LogPath))
                {
                    if (!announcedWait)
                    {
                        logger.LogDebug("Waiting for {LogPath} to appear.", LogPath);
                        announcedWait = true;
                    }
                }
                else
                {
                    announcedWait = false;
                    await PollOnceAsync(state, options, onLine, buffer, cancellationToken);
                }

                await Task.Delay(_pollInterval, timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }

        logger.LogDebug("Stopped tailing {LogPath} at offset {Offset}.", LogPath, state.Offset);
    }

    private async Task PollOnceAsync(TailState state, OutputSourceOptions options, Func<OutputLine, ValueTask> onLine, byte[] buffer, CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-rotation (rename in progress) or a transient sharing violation; try again next poll.
            logger.LogDebug(ex, "Could not open {LogPath}; retrying next poll.", LogPath);
            return;
        }

        List<string>? backfill = null;
        await using (stream)
        {
            var id = NtfsFileId.FromHandle(stream.SafeFileHandle);
            var length = stream.Length;

            if (!state.Opened)
            {
                state.Opened = true;
                state.FileId = id;
                if (options.StartAtEnd)
                {
                    backfill = await ReadBackfillAsync(stream, options.BackfillLines, state, cancellationToken);
                }

                logger.LogDebug("Opened {LogPath} (length {Length}) at offset {Offset}.", LogPath, length, state.Offset);
            }
            else if (id != state.FileId || length < state.Offset)
            {
                logger.LogInformation("{LogPath} rotated (file id {Old} -> {New}, length {Length}, offset was {Offset}); following the new file.", LogPath, state.FileId, id, length, state.Offset);
                state.FileId = id;
                state.Offset = 0;
                state.Splitter.Reset();
            }

            if (backfill is not null)
            {
                foreach (var text in backfill)
                {
                    await onLine(new OutputLine(text, timeProvider.GetUtcNow(), IsBackfill: true));
                }
            }

            if (length <= state.Offset)
            {
                return;
            }

            stream.Seek(state.Offset, SeekOrigin.Begin);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                state.Offset += read;
                foreach (var text in state.Splitter.Push(buffer.AsSpan(0, read)))
                {
                    await onLine(new OutputLine(text, timeProvider.GetUtcNow(), IsBackfill: false));
                }
            }
        }
    }

    /// <summary>
    /// Reads the last <paramref name="count"/> complete lines and positions the offset after the last
    /// newline so a trailing partial line is carried rather than lost.
    /// </summary>
    private static async Task<List<string>> ReadBackfillAsync(FileStream stream, int count, TailState state, CancellationToken cancellationToken)
    {
        var length = stream.Length;
        var windowStart = Math.Max(0, length - BackfillWindowBytes);
        var window = new byte[length - windowStart];
        stream.Seek(windowStart, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(window, cancellationToken);

        var lastNewline = Array.LastIndexOf(window, (byte)'\n');
        if (lastNewline < 0)
        {
            // No complete line in the window: follow from the window start (or the top of a short file).
            state.Offset = windowStart;
            return [];
        }

        var complete = window.AsSpan(0, lastNewline + 1);
        var lines = new List<string>();
        var splitter = new LineSplitter();
        lines.AddRange(splitter.Push(complete));
        if (windowStart > 0 && lines.Count > 0)
        {
            lines.RemoveAt(0); // the window cut into the middle of this one
        }

        state.Offset = windowStart + lastNewline + 1;
        state.Splitter.Reset();
        return lines.Count > count ? lines.GetRange(lines.Count - count, count) : lines;
    }

    private sealed class TailState
    {
        public bool Opened { get; set; }

        public NtfsFileId FileId { get; set; }

        public long Offset { get; set; }

        public LineSplitter Splitter { get; } = new();
    }

    /// <summary>Byte-level line splitter that carries a partial line (and a split multi-byte character) across pushes.</summary>
    private sealed class LineSplitter
    {
        private readonly MemoryStream _carry = new();

        public void Reset() => _carry.SetLength(0);

        public List<string> Push(ReadOnlySpan<byte> bytes)
        {
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n')
                {
                    continue;
                }

                if (_carry.Length > 0)
                {
                    _carry.Write(bytes[start..i]);
                    lines.Add(Decode(_carry.GetBuffer().AsSpan(0, (int)_carry.Length)));
                    _carry.SetLength(0);
                }
                else
                {
                    lines.Add(Decode(bytes[start..i]));
                }

                start = i + 1;
            }

            if (start < bytes.Length)
            {
                _carry.Write(bytes[start..]);
            }

            return lines;
        }

        private static string Decode(ReadOnlySpan<byte> line)
        {
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
            {
                line = line[3..];
            }

            return Encoding.UTF8.GetString(line);
        }
    }
}

/// <summary>Creates a <see cref="LogTailOutputSource"/> per instance log path (plan step 14).</summary>
public sealed class LogTailOutputSourceFactory(TimeProvider timeProvider, ILoggerFactory loggerFactory) : IOutputSourceFactory
{
    public IOutputSource ForLogFile(string logPath) =>
        new LogTailOutputSource(logPath, timeProvider, loggerFactory.CreateLogger<LogTailOutputSource>());
}
