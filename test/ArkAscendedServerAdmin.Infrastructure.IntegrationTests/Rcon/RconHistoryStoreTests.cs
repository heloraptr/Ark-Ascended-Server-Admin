using System.Text;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Maintenance;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using ArkAscendedServerAdmin.Infrastructure.Rcon;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Rcon;

/// <summary>The console history file in the instance folder, on the real filesystem.</summary>
public class RconHistoryStoreTests
{
    private const int InstanceId = 7;
    private const string Slug = "alpha";

    [Fact]
    public async Task A_missing_file_is_an_empty_history()
    {
        using var root = new TempDataRoot();
        var store = Create(root, new InstanceLocks());

        Assert.Empty(await store.LoadAsync(Slug, TestContext.Current.CancellationToken));
        Assert.Empty(await store.LoadAsync("never-created", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Append_writes_one_command_per_line_newest_last_and_skips_repeats()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root.Layout.InstanceDirectory(Slug));
        var store = Create(root, new InstanceLocks());

        await store.AppendAsync(InstanceId, Slug, "ListPlayers", ct);
        await store.AppendAsync(InstanceId, Slug, "SaveWorld", ct);
        var history = await store.AppendAsync(InstanceId, Slug, "SaveWorld", ct);

        Assert.Equal(["ListPlayers", "SaveWorld"], history);
        Assert.Equal("ListPlayers\r\nSaveWorld\r\n", await File.ReadAllTextAsync(root.Layout.InstanceRconHistoryPath(Slug), ct));
        Assert.Equal(["ListPlayers", "SaveWorld"], await store.LoadAsync(Slug, ct));
        Assert.Single(Directory.GetFiles(root.Layout.InstanceDirectory(Slug))); // no temp file left behind
    }

    [Fact]
    public async Task The_file_is_capped_at_the_newest_entries()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root.Layout.InstanceDirectory(Slug));
        var store = Create(root, new InstanceLocks());

        for (var i = 0; i < RconHistory.Capacity + 10; i++)
        {
            await store.AppendAsync(InstanceId, Slug, $"Broadcast {i}", ct);
        }

        var lines = await File.ReadAllLinesAsync(root.Layout.InstanceRconHistoryPath(Slug), ct);
        Assert.Equal(RconHistory.Capacity, lines.Length);
        Assert.Equal("Broadcast 10", lines[0]);
        Assert.Equal($"Broadcast {RconHistory.Capacity + 9}", lines[^1]);
    }

    [Fact]
    public async Task A_hand_edited_file_with_blank_lines_and_a_bom_is_read_and_rewritten_clean()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root.Layout.InstanceDirectory(Slug));
        var path = root.Layout.InstanceRconHistoryPath(Slug);
        await File.WriteAllTextAsync(path, "\r\nListPlayers\n\n   \nListPlayers\r\n  SaveWorld  \n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
        var store = Create(root, new InstanceLocks());

        Assert.Equal(["ListPlayers", "SaveWorld"], await store.LoadAsync(Slug, ct));

        await store.AppendAsync(InstanceId, Slug, "DoExit", ct);
        Assert.Equal("ListPlayers\r\nSaveWorld\r\nDoExit\r\n", await File.ReadAllTextAsync(path, ct));
    }

    [Fact]
    public async Task Multi_line_and_overlong_commands_are_not_stored()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root.Layout.InstanceDirectory(Slug));
        var store = Create(root, new InstanceLocks());

        await store.AppendAsync(InstanceId, Slug, "Broadcast one\ntwo", ct);
        await store.AppendAsync(InstanceId, Slug, "Broadcast " + new string('x', RconHistory.MaxCommandLength), ct);

        Assert.False(File.Exists(root.Layout.InstanceRconHistoryPath(Slug)));
    }

    [Fact]
    public async Task Only_the_tail_of_a_huge_file_is_read()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root.Layout.InstanceDirectory(Slug));
        var path = root.Layout.InstanceRconHistoryPath(Slug);
        var builder = new StringBuilder();
        for (var i = 0; i < 100_000; i++)
        {
            builder.Append("Broadcast ").Append(i).Append("\r\n");
        }

        await File.WriteAllTextAsync(path, builder.ToString(), ct);
        var store = Create(root, new InstanceLocks());

        var history = await store.LoadAsync(Slug, ct);

        Assert.Equal(RconHistory.Capacity, history.Count);
        Assert.Equal("Broadcast 99999", history[^1]);
        Assert.Equal($"Broadcast {100_000 - RconHistory.Capacity}", history[0]);
    }

    [Fact]
    public async Task A_missing_instance_folder_is_never_recreated()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var store = Create(root, new InstanceLocks());

        var history = await store.AppendAsync(InstanceId, Slug, "ListPlayers", ct);

        Assert.Empty(history!);
        Assert.False(Directory.Exists(root.Layout.InstanceDirectory(Slug)));
    }

    [Fact]
    public async Task Nothing_is_recorded_while_an_operation_holds_the_instance()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root.Layout.InstanceDirectory(Slug));
        var locks = new InstanceLocks();
        var store = Create(root, locks, TimeSpan.FromMilliseconds(50));

        using (locks.TryAcquire(InstanceId))
        {
            Assert.Null(await store.AppendAsync(InstanceId, Slug, "ListPlayers", ct));
        }

        Assert.False(File.Exists(root.Layout.InstanceRconHistoryPath(Slug)));
        Assert.False(locks.IsHeld(InstanceId));
        Assert.Equal(["ListPlayers"], await store.AppendAsync(InstanceId, Slug, "ListPlayers", ct));
    }

    [Fact]
    public async Task Concurrent_appends_from_two_stores_all_land()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root.Layout.InstanceDirectory(Slug));
        var locks = new InstanceLocks();
        var first = Create(root, locks, TimeSpan.FromSeconds(30));
        var second = Create(root, locks, TimeSpan.FromSeconds(30));

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => (i % 2 == 0 ? first : second).AppendAsync(InstanceId, Slug, $"Broadcast {i}", ct), ct)));

        var history = await first.LoadAsync(Slug, ct);
        Assert.Equal(40, history.Count);
        Assert.Equal(Enumerable.Range(0, 40).Select(i => $"Broadcast {i}").Order(StringComparer.Ordinal), history.Order(StringComparer.Ordinal));
        Assert.Single(Directory.GetFiles(root.Layout.InstanceDirectory(Slug)));
    }

    /// <summary>
    /// Appends hammer the instance while it is deleted with the real layout service. The delete holds the instance
    /// lease and the appends take it too, so none can drop a file into the folder halfway through its removal. A
    /// delete that lands in the millisecond an append holds the lease is refused like any other overlap and tried again.
    /// </summary>
    [Fact]
    public async Task Appends_racing_a_delete_never_leave_a_file_behind_or_break_the_delete()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var instance = await TestSeed.InstanceAsync(root, Slug, clustered: false, ct);
        Directory.CreateDirectory(root.Layout.InstanceSavedDirectory(Slug));
        Directory.CreateDirectory(root.Layout.InstanceConfigSourceDirectory(Slug));

        var clock = new FastTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var locks = new InstanceLocks();
        var processes = new FakeProcessManager();
        processes.Set(instance.Id, InstanceState.Stopped);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var delete = new InstanceDeleteService(
            root, root.Layout, locks, new RestoreJournalStore(root.Layout), new DetachedJobs(clock), processes, Substitute.For<IFirewallRules>(),
            new InstanceLayoutService(root.Layout, clock), new FakeConsoleService(), lifetime, clock, NullLogger<InstanceDeleteService>.Instance);
        var store = Create(root, locks, TimeSpan.FromSeconds(5));
        await store.AppendAsync(instance.Id, Slug, "ListPlayers", ct);
        Assert.True(File.Exists(root.Layout.InstanceRconHistoryPath(Slug)));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var appends = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                await store.AppendAsync(instance.Id, Slug, $"Broadcast {worker}-{i}", ct);
                await Task.Delay(1, ct); // a person typing, not a tight loop that would never let the lease go
            }
        }, ct)).ToList();

        await Task.Delay(50, ct);
        OperationOutcome outcome;
        var attempts = 0;
        do
        {
            attempts++;
            outcome = await delete.DeleteAsync(instance.Id, new InstanceDeleteOptions(KeepWorldData: false, DeleteBackups: true), ct);
            if (!outcome.Succeeded)
            {
                await Task.Delay(1, ct);
            }
        }
        while (!outcome.Succeeded && outcome.Error!.Contains("in progress", StringComparison.Ordinal) && attempts < 500);

        await Task.Delay(100, ct);
        await stop.CancelAsync();
        await Task.WhenAll(appends);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.False(Directory.Exists(root.Layout.InstanceDirectory(Slug)));
        Assert.Empty(Directory.GetFileSystemEntries(root.Layout.Instances));
    }

    private static RconHistoryStore Create(TempDataRoot root, IInstanceLocks locks, TimeSpan? leaseWait = null) =>
        new(root.Layout, locks, NullLogger<RconHistoryStore>.Instance, leaseWait);
}
