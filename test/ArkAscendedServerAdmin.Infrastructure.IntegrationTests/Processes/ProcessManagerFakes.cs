using System.ComponentModel;
using System.Diagnostics;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Startup;
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

    public Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Commands)
        {
            Commands.Add(command);
            Log.Add((command, DateTimeOffset.UtcNow));
        }

        return failure is { } kind
            ? throw new RconException(kind, $"fake {kind}")
            : Task.FromResult(command == RconCommands.ListPlayers ? RconCommands.NoPlayersReply : "ok");
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

    public void EnsureInstanceRules(int instanceId, int gamePort) => Ensured.Add((instanceId, gamePort));

    public void RemoveInstanceRules(int instanceId)
    {
    }
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
        IProjectionSynchronizer? synchronizer = null)
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
        Manager = new ProcessManager(
            root,
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

    public void SetReady() => _readiness.Current.Returns(new ReadinessState(ReadinessPhase.Ready, "Ready", null, DateTimeOffset.UtcNow));

    public void Dispose()
    {
        _stopping.Cancel();
        Queue.Dispose();
        _stopping.Dispose();
    }
}
