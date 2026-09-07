using System.Collections.Concurrent;
using System.Text;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Consoles;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Consoles;

public class LogTailOutputSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan _poll = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public async Task WaitsForTheFile_ThenDeliversAppendedLines_AndStripsTheBom()
    {
        using var root = new TempDataRoot();
        var path = root.Layout.InstanceLogPath("alpha");
        using var tail = new Harness(path);
        tail.Start(new OutputSourceOptions(StartAtEnd: false, BackfillLines: 0));

        await Task.Delay(_poll * 3, Ct);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            await stream.WriteAsync(_bom, Ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("[2026.09.07-19.38.50:123][  0]Log file open, 09/07/26 19:38:50\r\nsecond\r\n"), Ct);
        }

        var lines = await tail.WaitForAsync(2);
        Assert.Equal("[2026.09.07-19.38.50:123][  0]Log file open, 09/07/26 19:38:50", lines[0].Text);
        Assert.Equal("second", lines[1].Text);
        Assert.All(lines, line => Assert.False(line.IsBackfill));
    }

    [Fact]
    public async Task CompletesAPartialLineOnTheNextWrite()
    {
        using var root = new TempDataRoot();
        var path = CreateLog(root, "");
        await using var writer = OpenAppend(path);
        using var tail = new Harness(path);
        tail.Start(new OutputSourceOptions(StartAtEnd: false, BackfillLines: 0));

        await writer.WriteAsync(Encoding.UTF8.GetBytes("first\r\nsecond part"), Ct);
        await writer.FlushAsync(Ct);
        Assert.Equal("first", (await tail.WaitForAsync(1))[0].Text);
        await Task.Delay(_poll * 4, Ct);
        Assert.Single(tail.Lines);

        await writer.WriteAsync(Encoding.UTF8.GetBytes(" done\r\n"), Ct);
        await writer.FlushAsync(Ct);
        var lines = await tail.WaitForAsync(2);
        Assert.Equal("second part done", lines[1].Text);
    }

    [Fact]
    public async Task StartAtEnd_BackfillsTheLastLines_ThenFollows()
    {
        using var root = new TempDataRoot();
        var path = CreateLog(root, string.Concat(Enumerable.Range(1, 10).Select(i => $"old {i}\r\n")), withBom: true);
        using var tail = new Harness(path);
        tail.Start(new OutputSourceOptions(StartAtEnd: true, BackfillLines: 3));

        var backfill = await tail.WaitForAsync(3);
        Assert.Equal(["old 8", "old 9", "old 10"], backfill.Select(line => line.Text));
        Assert.All(backfill, line => Assert.True(line.IsBackfill));

        await using (var writer = OpenAppend(path))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("live 1\r\n"), Ct);
        }

        var lines = await tail.WaitForAsync(4);
        Assert.Equal("live 1", lines[3].Text);
        Assert.False(lines[3].IsBackfill);
    }

    [Fact]
    public async Task StartAtEnd_WithNoBackfill_SkipsHistory()
    {
        using var root = new TempDataRoot();
        var path = CreateLog(root, "old 1\r\nold 2\r\n");
        using var tail = new Harness(path);
        tail.Start(new OutputSourceOptions(StartAtEnd: true, BackfillLines: 0));

        await Task.Delay(_poll * 4, Ct);
        await using (var writer = OpenAppend(path))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("live\r\n"), Ct);
        }

        var lines = await tail.WaitForAsync(1);
        Assert.Equal("live", lines[0].Text);
        Assert.False(lines[0].IsBackfill);
    }

    [Fact]
    public async Task FollowsRotation_LikeTheGameDoesIt_WithoutLosingTheNewFilesFirstLines()
    {
        using var root = new TempDataRoot();
        var path = CreateLog(root, "");
        using var tail = new Harness(path);
        tail.Start(new OutputSourceOptions(StartAtEnd: false, BackfillLines: 0));

        // A long first session so the new (shorter) file's length stays below the old offset.
        await using (var writer = OpenAppend(path))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 50).Select(i => $"session one line {i} with some padding to make it long\r\n"))), Ct);
        }

        await tail.WaitForAsync(50);

        // Relaunch: rename ShooterGame.log -> ShooterGame-backup-<ts>.log, create a fresh ShooterGame.log (BOM first).
        var backup = Path.Combine(Path.GetDirectoryName(path)!, "ShooterGame-backup-2026.09.07-19.40.00.log");
        File.Move(path, backup);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            await stream.WriteAsync(_bom, Ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("[ts][  0]Log file open, 09/07/26 19:40:00\r\nnew 2\r\n"), Ct);
        }

        var lines = await tail.WaitForAsync(52);
        Assert.Equal("[ts][  0]Log file open, 09/07/26 19:40:00", lines[50].Text);
        Assert.Equal("new 2", lines[51].Text);
    }

    [Fact]
    public async Task DetectsRotationByFileId_WhenTheNewFileIsAlreadyLonger()
    {
        using var root = new TempDataRoot();
        var path = CreateLog(root, "short\r\n");
        using var tail = new Harness(path);
        tail.Start(new OutputSourceOptions(StartAtEnd: false, BackfillLines: 0));
        await tail.WaitForAsync(1);

        // Same name, new file, longer than the old offset: only the NTFS id tells them apart.
        var backup = Path.Combine(Path.GetDirectoryName(path)!, "ShooterGame-backup-2026.09.07-19.41.00.log");
        File.Move(path, backup);
        File.WriteAllText(path, "a much longer first line in the replacement file\r\nsecond\r\n");

        var lines = await tail.WaitForAsync(3);
        Assert.Equal("a much longer first line in the replacement file", lines[1].Text);
        Assert.Equal("second", lines[2].Text);
    }

    [Fact]
    public async Task StopsPromptlyOnCancellation()
    {
        using var root = new TempDataRoot();
        var path = CreateLog(root, "x\r\n");
        var source = new LogTailOutputSource(path, TimeProvider.System, NullLogger.Instance, _poll);
        using var cts = new CancellationTokenSource();
        var run = source.RunAsync(new OutputSourceOptions(false, 0), _ => ValueTask.CompletedTask, cts.Token);

        await Task.Delay(_poll * 3, Ct);
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(2), Ct);
    }

    private static string CreateLog(TempDataRoot root, string content, bool withBom = false)
    {
        var path = root.Layout.InstanceLogPath("alpha");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, withBom ? [.. _bom, .. bytes] : bytes);
        return path;
    }

    private static FileStream OpenAppend(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private sealed class Harness(string path) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _run;

        public ConcurrentQueue<OutputLine> Lines { get; } = new();

        public void Start(OutputSourceOptions options)
        {
            var source = new LogTailOutputSource(path, TimeProvider.System, NullLogger.Instance, _poll);
            _run = source.RunAsync(options, line =>
            {
                Lines.Enqueue(line);
                return ValueTask.CompletedTask;
            }, _cts.Token);
        }

        public async Task<List<OutputLine>> WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow + _timeout;
            while (Lines.Count < count)
            {
                if (_run is { IsCompleted: true })
                {
                    await _run; // surfaces a faulted tail
                    Assert.Fail("The tail stopped before delivering the expected lines.");
                }

                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"Expected {count} lines but saw {Lines.Count}: {string.Join(" | ", Lines.Select(line => line.Text))}");
                }

                await Task.Delay(_poll, Ct);
            }

            return [.. Lines];
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _run?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Surfaced by the test body already.
            }

            _cts.Dispose();
        }
    }
}
