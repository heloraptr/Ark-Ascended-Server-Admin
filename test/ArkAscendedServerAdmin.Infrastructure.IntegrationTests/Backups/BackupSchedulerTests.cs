using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Backups;

/// <summary>The backup timer's due/skip rules (plan steps 22, 28) against the real record table.</summary>
public class BackupSchedulerTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunningInstanceWithoutRecords_IsDue()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Instance.Id, InstanceState.Running);

        await f.Scheduler.RunDueBackupsAsync(ct);
        await f.Backups.Completion;

        Assert.Equal([(f.Instance.Id, false)], f.Backups.Calls);
    }

    [Theory]
    [InlineData(InstanceState.Stopped)]
    [InlineData(InstanceState.Starting)]
    [InlineData(InstanceState.Stopping)]
    [InlineData(InstanceState.Unknown)]
    public async Task InstanceWithoutRcon_IsNotDue(InstanceState state)
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Instance.Id, state);

        await f.Scheduler.RunDueBackupsAsync(ct);
        await f.Backups.Completion;

        Assert.Empty(f.Backups.Calls);
    }

    [Theory]
    [InlineData(InstanceState.Unreachable)]
    [InlineData(InstanceState.StartingUnconfirmed)]
    public async Task UnreachableInstance_StillRunsSoTheSkipIsRecorded(InstanceState state)
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Instance.Id, state);

        await f.Scheduler.RunDueBackupsAsync(ct);
        await f.Backups.Completion;

        Assert.Equal([(f.Instance.Id, false)], f.Backups.Calls);
    }

    [Fact]
    public async Task RecentRecordOfAnyOutcome_DefersTheNextBackupUntilTheIntervalElapses()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, i => i.BackupIntervalMinutes = 15);
        f.Processes.Set(f.Instance.Id, InstanceState.Running);
        await f.RecordAsync(BackupOutcome.Skipped, _start.AddMinutes(-10), ct);

        await f.Scheduler.RunDueBackupsAsync(ct);
        await f.Backups.Completion;
        Assert.Empty(f.Backups.Calls);

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.Scheduler.RunDueBackupsAsync(ct);
        await f.Backups.Completion;

        Assert.Single(f.Backups.Calls);
    }

    [Fact]
    public async Task DefaultInterval_ComesFromAppSettings()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Instance.Id, InstanceState.Running);
        await f.RecordAsync(BackupOutcome.Success, _start.AddMinutes(-29), ct); // default interval is 30

        await f.Scheduler.RunDueBackupsAsync(ct);
        await f.Backups.Completion;

        Assert.Empty(f.Backups.Calls);
    }

    [Fact]
    public async Task InFlightBackup_IsNeverOverlapped()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Instance.Id, InstanceState.Running);
        f.Backups.Barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await f.Scheduler.RunDueBackupsAsync(ct);
        await f.Scheduler.RunDueBackupsAsync(ct);
        Assert.Single(f.Backups.Calls);

        f.Backups.Barrier.SetResult();
        await f.Backups.Completion;
    }

    // ---- hosted loop -------------------------------------------------------------------------------

    /// <summary>
    /// After a failed migration the tables are missing, so the timer must not tick before the pipeline is Ready:
    /// a clock that passes a tick while the scheduler waits on readiness reaches no pass at all.
    /// </summary>
    [Fact]
    public async Task Loop_DoesNotTickUntilReadinessReportsReady()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var clock = new ManualTimeProvider(_start);
        var readiness = new FakeReadinessMonitor();
        var attempts = new AttemptLog();
        var scheduler = new BackupScheduler(root, CountingSettings(attempts, failFirst: false), new FakeProcessManager(), new RecordingBackupService(), readiness, clock, NullLogger<BackupScheduler>.Instance);

        await scheduler.StartAsync(ct);
        await readiness.Subscribed.WaitAsync(_wait, ct);
        clock.Advance(BackupScheduler.Tick);
        Assert.Equal(0, clock.PendingTimers);
        Assert.Equal(0, attempts.Count);

        readiness.Report(ReadinessPhase.Ready);
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        Assert.Equal(0, attempts.Count);
        clock.Advance(BackupScheduler.Tick);
        await attempts.WhenAttempt(1).WaitAsync(_wait, ct);

        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        await scheduler.StopAsync(ct);
        Assert.True(scheduler.ExecuteTask!.IsCompletedSuccessfully);
    }

    /// <summary>
    /// An exception type the loop never expected (here from the settings read) is logged and the next tick tries
    /// again; it must not escape <c>ExecuteAsync</c>, where it would stop the whole service.
    /// </summary>
    [Fact]
    public async Task Loop_TickThatThrows_IsLoggedAndTriedAgainOneTickLater()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var clock = new ManualTimeProvider(_start);
        var attempts = new AttemptLog();
        var log = new RecordingLogger<BackupScheduler>();
        var scheduler = new BackupScheduler(root, CountingSettings(attempts, failFirst: true), new FakeProcessManager(), new RecordingBackupService(), new FakeReadinessMonitor(ReadinessPhase.Ready), clock, log);

        await scheduler.StartAsync(ct);
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        clock.Advance(BackupScheduler.Tick);
        await attempts.WhenAttempt(1).WaitAsync(_wait, ct);

        // Parked on the next delay, so the failure has been handled and nothing else can run until the clock moves.
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        var error = Assert.Single(log.Errors);
        Assert.IsType<NotSupportedException>(error.Exception);
        Assert.Equal("Backup scheduler tick failed; it will try again next minute.", error.Message);

        clock.Advance(BackupScheduler.Tick - TimeSpan.FromSeconds(1));
        Assert.Equal(1, attempts.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await attempts.WhenAttempt(2).WaitAsync(_wait, ct);

        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        await scheduler.StopAsync(ct);
        Assert.True(scheduler.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(log.Errors);
    }

    /// <summary>Default settings on every read, counted; the first read throws when <paramref name="failFirst"/> is set.</summary>
    private static IAppSettingsStore CountingSettings(AttemptLog attempts, bool failFirst)
    {
        var settings = Substitute.For<IAppSettingsStore>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            attempts.Record() == 1 && failFirst
                ? Task.FromException<AppSettings>(new NotSupportedException("The first tick fails."))
                : Task.FromResult(new AppSettings()));
        return settings;
    }

    private sealed class RecordingBackupService : IBackupService
    {
        private readonly object _sync = new();
        private Task _completion = Task.CompletedTask;

        public event Action<BackupRecord>? Recorded { add { } remove { } }

        public event Action<RestoreRecord>? Restored { add { } remove { } }

        public Task<RestoreInspection> InspectRestoreAsync(int instanceId, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationOutcome> RestoreAsync(int instanceId, string fileName, bool includeCluster, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationOutcome> RecoverAsync(string operationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationOutcome> DiscardJournalAsync(string operationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public List<(int InstanceId, bool IsManual)> Calls { get; } = [];

        public TaskCompletionSource? Barrier { get; set; }

        public Task Completion
        {
            get
            {
                lock (_sync)
                {
                    return _completion;
                }
            }
        }

        public async Task<BackupRecord> BackupNowAsync(int instanceId, bool isManual, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();
            lock (_sync)
            {
                Calls.Add((instanceId, isManual));
                _completion = tcs.Task;
            }

            try
            {
                if (Barrier is { } barrier)
                {
                    await barrier.Task.WaitAsync(cancellationToken);
                }

                return new BackupRecord { InstanceId = instanceId, Outcome = BackupOutcome.Success, IsManual = isManual };
            }
            finally
            {
                tcs.SetResult();
            }
        }
    }

    private sealed class Fixture
    {
        public required TempDataRoot Root { get; init; }

        public required BackupScheduler Scheduler { get; init; }

        public required RecordingBackupService Backups { get; init; }

        public required FakeProcessManager Processes { get; init; }

        public required FastTimeProvider Clock { get; init; }

        public required Instance Instance { get; init; }

        public async Task RecordAsync(BackupOutcome outcome, DateTimeOffset createdAt, CancellationToken ct)
        {
            await using var db = Root.CreateDbContext();
            db.BackupRecords.Add(new BackupRecord { InstanceId = Instance.Id, Outcome = outcome, CreatedAt = createdAt, Reason = outcome == BackupOutcome.Success ? null : "test" });
            await db.SaveChangesAsync(ct);
        }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, CancellationToken ct, Action<Instance>? configure = null)
        {
            await root.InitializeAsync(ct);
            var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct, configure);
            var clock = new FastTimeProvider(_start);
            var processes = new FakeProcessManager();
            var backups = new RecordingBackupService();
            var scheduler = new BackupScheduler(root, new AppSettingsStore(root), processes, backups, new FakeReadinessMonitor(ReadinessPhase.Ready), clock, NullLogger<BackupScheduler>.Instance);
            return new Fixture { Root = root, Scheduler = scheduler, Backups = backups, Processes = processes, Clock = clock, Instance = instance };
        }
    }
}
