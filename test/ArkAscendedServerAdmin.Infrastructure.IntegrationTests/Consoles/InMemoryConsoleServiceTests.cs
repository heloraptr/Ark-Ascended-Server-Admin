using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Consoles;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Consoles;

public class InMemoryConsoleServiceTests
{
    private static readonly DateTimeOffset _at = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvictsOldestLinesBeyondCapacity()
    {
        var console = new InMemoryConsoleService(NullLogger<InMemoryConsoleService>.Instance);
        var channel = ConsoleChannels.Instance(1);

        for (var i = 0; i < IConsoleService.Capacity + 10; i++)
        {
            console.Append(channel, new ConsoleLine(_at, $"line {i}"));
        }

        var snapshot = console.Snapshot(channel);
        Assert.Equal(IConsoleService.Capacity, snapshot.Count);
        Assert.Equal("line 10", snapshot[0].Text);
        Assert.Equal($"line {IConsoleService.Capacity + 9}", snapshot[^1].Text);
    }

    [Fact]
    public void ChannelsAreIndependent_AndSnapshotIsACopy()
    {
        var console = new InMemoryConsoleService(NullLogger<InMemoryConsoleService>.Instance);
        console.Append(ConsoleChannels.SteamCmd, new ConsoleLine(_at, "steam"));
        console.Append(ConsoleChannels.Instance(1), new ConsoleLine(_at, "one"));

        var before = console.Snapshot(ConsoleChannels.SteamCmd);
        console.Append(ConsoleChannels.SteamCmd, new ConsoleLine(_at, "more"));
        console.Clear(ConsoleChannels.Instance(1));

        Assert.Single(before);
        Assert.Equal(2, console.Snapshot(ConsoleChannels.SteamCmd).Count);
        Assert.Empty(console.Snapshot(ConsoleChannels.Instance(1)));
        Assert.Empty(console.Snapshot("never-used"));
    }

    [Fact]
    public void RaisesLineAppended_AndSurvivesAThrowingSubscriber()
    {
        var console = new InMemoryConsoleService(NullLogger<InMemoryConsoleService>.Instance);
        var received = new List<(string Channel, ConsoleLine Line)>();
        console.LineAppended += (_, _) => throw new InvalidOperationException("boom");
        console.LineAppended += (channel, line) => received.Add((channel, line));

        var line = new ConsoleLine(_at, "hello", ConsoleLineKind.Warning);
        console.Append(ConsoleChannels.SteamCmd, line);

        var (channel, appended) = Assert.Single(received);
        Assert.Equal(ConsoleChannels.SteamCmd, channel);
        Assert.Same(line, appended);
    }

    [Fact]
    public async Task ConcurrentAppendsNeverExceedCapacity()
    {
        var console = new InMemoryConsoleService(NullLogger<InMemoryConsoleService>.Instance);
        var channel = ConsoleChannels.Instance(7);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                console.Append(channel, new ConsoleLine(_at, $"{worker}:{i}"));
                _ = console.Snapshot(channel);
            }
        })));

        Assert.Equal(IConsoleService.Capacity, console.Snapshot(channel).Count);
    }
}
