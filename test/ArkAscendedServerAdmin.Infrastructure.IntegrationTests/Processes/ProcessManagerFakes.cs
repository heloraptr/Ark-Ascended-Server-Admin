using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

internal sealed class StubEnumerator(IReadOnlyList<GameProcessInfo> processes) : IGameProcessEnumerator
{
    /// <summary>When set, answers every targeted read instead of the list (throw from it to simulate a WMI failure).</summary>
    public Func<int, ProcessRowRead>? ReadRowOverride { get; set; }

    /// <summary>When set, answers every snapshot instead of the list (throw from it to simulate a WMI failure).</summary>
    public Func<ProcessTableSnapshot>? SnapshotOverride { get; set; }

    public ProcessTableSnapshot Snapshot() => SnapshotOverride is { } custom ? custom() : new ProcessTableSnapshot(processes, true);

    public IReadOnlyList<GameProcessInfo> Enumerate() => processes;

    public ProcessRowRead ReadRow(int pid)
    {
        if (ReadRowOverride is { } custom)
        {
            return custom(pid);
        }

        var row = processes.FirstOrDefault(p => p.Pid == pid);
        return row is null ? ProcessRowRead.Missing : new ProcessRowRead(ProcessRowStatus.Complete, row);
    }
}

/// <summary>Succeeds or throws the configured failure; every call is recorded.</summary>
internal sealed class FakeRconClient(RconFailure? failure) : IRconClient
{
    public List<string> Commands { get; } = [];

    /// <summary>Every command with the wall-clock instant it arrived; for countdown timing assertions.</summary>
    public List<(string Command, DateTimeOffset At)> Log { get; } = [];

    /// <summary>When set, awaited before each reply (or failure) is returned; a test holds a probe with it (B4).</summary>
    public Func<string, Task>? BeforeReply { get; set; }

    public async Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Commands)
        {
            Commands.Add(command);
            Log.Add((command, DateTimeOffset.UtcNow));
        }

        if (BeforeReply is { } hold)
        {
            await hold(command);
        }

        return failure is { } kind
            ? throw new RconException(kind, $"fake {kind}")
            : command == RconCommands.ListPlayers ? RconCommands.NoPlayersReply : "ok";
    }
}

/// <summary>An output source that produces nothing and runs until cancelled; records the options it was given.</summary>
internal sealed class FakeOutputSourceFactory : IOutputSourceFactory, IOutputSource
{
    public List<string> Paths { get; } = [];

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OutputSourceOptions? LastOptions { get; private set; }

    /// <summary>The manager starts the tail on a background task; tests await this before reading <see cref="LastOptions"/>.</summary>
    public Task Started => _started.Task;

    public IOutputSource ForLogFile(string logPath)
    {
        Paths.Add(logPath);
        return this;
    }

    public async Task RunAsync(OutputSourceOptions options, Func<OutputLine, ValueTask> onLine, CancellationToken cancellationToken)
    {
        LastOptions = options;
        _started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

internal sealed class FakeConfigWriter(string? generatedGameUserSettings) : IGeneratedConfigWriter
{
    public Task<GeneratedConfig> WriteAsync(int instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(new GeneratedConfig(string.Empty, generatedGameUserSettings ?? string.Empty, [], null, []));

    public Task<string?> ReadGeneratedGameUserSettingsAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(generatedGameUserSettings);
}

internal sealed class FakeLayoutService : IInstanceLayoutService
{
    public Task EnsureAsync(string slug, CancellationToken cancellationToken) => Task.CompletedTask;

    public bool IsComplete(string slug) => true;

    public Task RemoveJunctionsAsync(string slug, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> RetireAsync(string slug, bool keepWorldData, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

internal sealed class FakeFirewall : IFirewallRules
{
    public List<(int InstanceId, int GamePort)> Ensured { get; } = [];

    /// <summary>Instance ids <see cref="InstanceRulesExist"/> answers true for; everything else answers false.</summary>
    public HashSet<int> Existing { get; } = [];

    /// <summary>Set to make <see cref="InstanceRulesExist"/> throw, the way an unreadable firewall does.</summary>
    public bool Unreadable { get; set; }

    public void EnsureInstanceRules(int instanceId, int gamePort) => Ensured.Add((instanceId, gamePort));

    public void RemoveInstanceRules(int instanceId)
    {
    }

    public bool InstanceRulesExist(int instanceId) =>
        Unreadable ? throw new InvalidOperationException("Firewall rules could not be read.") : Existing.Contains(instanceId);
}

internal sealed class RecordingConsole : IConsoleService
{
    private readonly Dictionary<string, List<ConsoleLine>> _channels = [];

    public event Action<string, ConsoleLine>? LineAppended;

    public event Action<string>? Cleared;

    public IReadOnlyList<ConsoleLine> Snapshot(string channel)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(channel, out var lines) ? [.. lines] : [];
        }
    }

    public void Append(string channel, ConsoleLine line)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(channel, out var lines))
            {
                lines = [];
                _channels[channel] = lines;
            }

            lines.Add(line);
        }

        LineAppended?.Invoke(channel, line);
    }

    public void Clear(string channel)
    {
        lock (_channels)
        {
            _channels.Remove(channel);
        }

        Cleared?.Invoke(channel);
    }
}

/// <summary>A harmless process standing in for the game: <c>cmd.exe</c> waiting on stdin until it is killed.</summary>
internal sealed class StandInProcess : IDisposable
{
    private StandInProcess(Process process) => Process = process;

    public Process Process { get; }

    public static StandInProcess Start()
    {
        var info = new ProcessStartInfo("cmd.exe", "/c pause")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        return new StandInProcess(Process.Start(info) ?? throw new InvalidOperationException("cmd.exe did not start."));
    }

    /// <summary>What the enumerator would report for this process if it were <paramref name="slug"/>'s game server.</summary>
    public GameProcessInfo As(DataRootLayout layout, string slug)
    {
        var executable = layout.InstanceExecutable(slug);
        return new GameProcessInfo(Process.Id, executable, $"\"{executable}\" TheIsland_WP?listen?AltSaveDirectoryName={slug} -port=7777 -log", new DateTimeOffset(Process.StartTime));
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }

        Process.Dispose();
    }
}

/// <summary>A process manager wired to fakes and the real locks, gate, and queue; disposing fires the host lifetime so every loop ends.</summary>
internal sealed class ProcessManagerHarness : IDisposable
{
    public const string GeneratedIni = "[ServerSettings]\r\nServerAdminPassword=secret\r\nRCONPort=27020\r\nRCONEnabled=True\r\n";

    private readonly CancellationTokenSource _stopping = new();
    private readonly IReadinessMonitor _readiness = Substitute.For<IReadinessMonitor>();

    public ProcessManagerHarness(
        TempDataRoot root,
        IReadOnlyList<GameProcessInfo> processes,
        FakeRconClient rcon,
        RecordingConsole console,
        FakeOutputSourceFactory outputs,
        string? generatedIni = GeneratedIni,
        bool ready = true,
        AppSettings? settings = null,
        IProjectionSynchronizer? synchronizer = null,
        IDbContextFactory<AppDbContext>? database = null)
    {
        var store = Substitute.For<IAppSettingsStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(settings ?? new AppSettings());
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(_stopping.Token);
        if (ready)
        {
            SetReady();
        }
        else
        {
            _readiness.Current.Returns(new ReadinessState(ReadinessPhase.Recovering, "Recovering", null, DateTimeOffset.UtcNow));
        }

        Gate = new MaintenanceGate();
        Locks = new InstanceLocks();
        Queue = new LaunchQueue(Gate, store, TimeProvider.System);
        Recovery = new RecoveryRequests();
        Enumerator = new StubEnumerator(processes);
        Synchronizer = synchronizer ?? new NoProjectionSynchronizer();
        Starter = new StandInStarter();
        Manager = new ProcessManager(
            database ?? root,
            root.Layout,
            store,
            new HostConfiguration(root.Layout.Root, ["https://localhost:5001"], [], false, true, false, "0.0.0-test"),
            _readiness,
            Locks,
            new RestoreJournalStore(root.Layout),
            Gate,
            Queue,
            Recovery,
            Synchronizer,
            rcon,
            Enumerator,
            Starter,
            new FakeFirewall(),
            new FakeLayoutService(),
            new FakeConfigWriter(generatedIni),
            outputs,
            console,
            TimeProvider.System,
            lifetime,
            NullLogger<ProcessManager>.Instance);
    }

    public ProcessManager Manager { get; }

    public MaintenanceGate Gate { get; }

    public InstanceLocks Locks { get; }

    public LaunchQueue Queue { get; }

    public RecoveryRequests Recovery { get; }

    public StubEnumerator Enumerator { get; }

    public IProjectionSynchronizer Synchronizer { get; }

    /// <summary>Launches a stand-in <c>cmd.exe</c> in place of the game; see <see cref="StandInStarter"/>.</summary>
    public StandInStarter Starter { get; }

    public void SetReady() => _readiness.Current.Returns(new ReadinessState(ReadinessPhase.Ready, "Ready", null, DateTimeOffset.UtcNow));

    public void Dispose()
    {
        _stopping.Cancel();
        Queue.Dispose();
        _stopping.Dispose();
        Starter.Dispose();
    }
}

/// <summary>
/// The launch seam's test double (B4). By default it is the real starter, which fails for want of a game install, so
/// the older tests see the launch they always saw. With <see cref="UseStandIn"/> every launch starts a stand-in
/// <c>cmd.exe</c> instead, so a relaunch can succeed. <see cref="Barrier"/> holds a launch just before the process
/// starts; <see cref="Failure"/> makes every start throw. Every stand-in started is killed on dispose.
/// </summary>
internal sealed class StandInStarter : IGameProcessStarter, IDisposable
{
    private readonly List<StandInProcess> _started = [];

    public bool UseStandIn { get; set; }

    /// <summary>When set, each start signals <see cref="Waiting"/> and blocks until this completes.</summary>
    public TaskCompletionSource? Barrier { get; set; }

    /// <summary>Completed when a start reaches <see cref="Barrier"/>.</summary>
    public TaskCompletionSource Waiting { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, every start throws it instead of starting anything.</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<StandInProcess> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public StandInProcess Last => Started[^1];

    public Process Start(ProcessStartInfo startInfo)
    {
        if (Barrier is { } barrier)
        {
            Waiting.TrySetResult();
            barrier.Task.Wait(TestContext.Current.CancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        if (!UseStandIn)
        {
            return new GameProcessStarter().Start(startInfo);
        }

        var process = StandInProcess.Start();
        lock (_started)
        {
            _started.Add(process);
        }

        return process.Process;
    }

    public void ResetWaiting() => Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        foreach (var process in Started)
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// A context factory over a <see cref="TempDataRoot"/>'s database with EF interceptors added, so a test can hold or fail
/// one particular database write (B4).
/// </summary>
internal sealed class InterceptedDatabase(TempDataRoot root, params IInterceptor[] interceptors) : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={root.Layout.DatabasePath}")
        .AddInterceptors(interceptors)
        .Options;

    public AppDbContext CreateDbContext() => new(_options);
}

/// <summary>
/// Holds or fails the database commands that match a predicate (B4). <see cref="Hold"/> arms a barrier: the next matching
/// command completes the task Hold returned and waits for <see cref="Release"/>. <see cref="FailWhen"/> makes matching commands
/// throw. Both look at the command text and its parameter values.
/// </summary>
internal sealed class CommandGate : DbCommandInterceptor
{
    private readonly object _sync = new();
    private Func<DbCommand, bool>? _hold;
    private TaskCompletionSource? _held;
    private TaskCompletionSource? _release;

    public Func<DbCommand, bool>? FailWhen { get; set; }

    /// <summary>Arms the barrier for the next command matching <paramref name="match"/>; the task completes once one is held.</summary>
    public Task Hold(Func<DbCommand, bool> match)
    {
        lock (_sync)
        {
            _hold = match;
            _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _held.Task;
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            _release?.TrySetResult();
        }
    }

    /// <summary>True when the command sets <c>State</c> to <paramref name="state"/> (an ExecuteUpdate of the instance row).</summary>
    public static bool SetsState(DbCommand command, InstanceState state) =>
        command.CommandText.Contains("UPDATE \"Instances\"", StringComparison.Ordinal)
        && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, state.ToString()));

    /// <summary>True when the command writes a non-null <c>LastPid</c> (an identity persist).</summary>
    public static bool PersistsIdentity(DbCommand command) =>
        command.CommandText.Contains("UPDATE \"Instances\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"LastPid\" = @", StringComparison.Ordinal);

    /// <summary>True when the command writes a null <c>LastPid</c> (the exit cleanup's identity clear).</summary>
    public static bool ClearsIdentity(DbCommand command) =>
        command.CommandText.Contains("UPDATE \"Instances\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"LastPid\" = NULL", StringComparison.Ordinal);

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await GateAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await GateAsync(command);
        return result;
    }

    private async Task GateAsync(DbCommand command)
    {
        Task? wait = null;
        lock (_sync)
        {
            if (_hold is { } match && match(command))
            {
                _hold = null;
                _held!.TrySetResult();
                wait = _release!.Task;
            }
        }

        if (wait is not null)
        {
            await wait;
        }

        if (FailWhen is { } fail && fail(command))
        {
            throw new InvalidOperationException("Simulated database failure.");
        }
    }
}
