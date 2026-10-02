using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.CurseForge;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;
using ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Provisioning;
using ArkAscendedServerAdmin.Infrastructure.Players;
using ArkAscendedServerAdmin.Infrastructure.Mods;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using ArkAscendedServerAdmin.Infrastructure.Scheduling;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Networking;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Server.Commands;
using ArkAscendedServerAdmin.Startup;
using ArkAscendedServerAdmin.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

/// <summary>
/// The Server command facades wired over a <see cref="TempDataRoot"/>: real SQLite, real
/// <see cref="IniSourceStore"/> and <see cref="GeneratedConfigWriter"/>, and fakes for everything that
/// would touch a process, a socket, or the network. Every facade shares the same fakes so a test can
/// script one and observe another.
/// </summary>
internal sealed class CommandTestHost : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 30, 0, TimeSpan.Zero);

    public const int WebPort = 5000;

    public CommandTestHost()
    {
        Root = new TempDataRoot();
        Clock = new FixedTimeProvider(Now);
        Host = new HostConfiguration(Root.Layout.Root, [$"http://127.0.0.1:{WebPort}"], [], true, true, false, "0.0.0-test");
        Settings = new AppSettingsStore(Root);
        IniStore = new IniSourceStore(Root.Layout, Root, Clock, NullLogger<IniSourceStore>.Instance);
        GeneratedConfig = new GeneratedConfigWriter(Root.Layout, Root, IniStore, Settings, NullLogger<GeneratedConfigWriter>.Instance);
        ProcessManager = new FakeProcessManager();
        Locks = new FakeInstanceLocks();
        Journals = new RestoreJournalStore(Root.Layout);
        LayoutService = new FakeInstanceLayoutService(Root, Clock);
        Console = new FakeConsoleService();
        Rcon = new ScriptedRconClient();
        Backups = Substitute.For<IBackupService>();
        DeleteService = Substitute.For<IInstanceDeleteService>();
        CurseForge = Substitute.For<ICurseForgeApi>();
        Exporter = Substitute.For<IConfigBackupExporter>();
        StartupControl = Substitute.For<IStartupControl>();
        UpdateService = Substitute.For<IUpdateService>();
        Recovery = Substitute.For<IMaintenanceRecovery>();
        InstallChecker = Substitute.For<IGameInstallChecker>();
        Firewall = new FakeFirewall();
        HostAddresses = new FakeHostAddressProvider();
        Jobs = new DetachedJobs(Clock);

        RconOperations = new RconOperations(Root, Settings, ProcessManager, GeneratedConfig, Rcon, Console, Clock);
        Instances = new InstanceCommands(
            Guard, Root, Root.Layout, Host, Settings, ProcessManager, Locks, Journals, Backups, DeleteService, LayoutService, IniStore, new IniSeeder(IniStore),
            RconOperations, Firewall, HostAddresses, Clock, NullLogger<InstanceCommands>.Instance);
        Clusters = new ClusterCommands(Guard, Root, Root.Layout, new IniSeeder(IniStore), Locks, Journals, Clock, NullLogger<ClusterCommands>.Instance);
        Config = new ConfigCommands(Guard, Root, IniStore, NullLogger<ConfigCommands>.Instance);
        Refresher = new ModMetadataRefresher(Root, Settings, CurseForge, NullLogger<ModMetadataRefresher>.Instance);
        Mods = new ModCommands(Guard, Root, Settings, CurseForge, Refresher, Clock, NullLogger<ModCommands>.Instance);
        Tracker = new PlayerTracker(Root, Console, ProcessManager, NullLogger<PlayerTracker>.Instance);
        Players = new PlayerCommands(Guard, Root, Settings, ProcessManager, GeneratedConfig, Rcon, RconOperations, Tracker, Clock, NullLogger<PlayerCommands>.Instance);
        Maps = new MapCommands(Guard, Root, Mods);
        SettingsCommands = new SettingsCommands(Guard, Settings, Exporter, Root.Layout, Host, Clock);
        Maintenance = new MaintenanceCommands(Guard, StartupControl, UpdateService, Recovery, InstallChecker, Jobs, NullLogger<MaintenanceCommands>.Instance);
    }

    public TempDataRoot Root { get; }

    public FixedTimeProvider Clock { get; }

    public HostConfiguration Host { get; }

    public FakeAuthorizationGuard Guard { get; } = new();

    public AppSettingsStore Settings { get; }

    public IniSourceStore IniStore { get; }

    public GeneratedConfigWriter GeneratedConfig { get; }

    public FakeProcessManager ProcessManager { get; }

    public FakeInstanceLocks Locks { get; }

    public RestoreJournalStore Journals { get; }

    public FakeInstanceLayoutService LayoutService { get; }

    public FakeConsoleService Console { get; }

    public ScriptedRconClient Rcon { get; }

    public RconOperations RconOperations { get; }

    public IBackupService Backups { get; }

    public IInstanceDeleteService DeleteService { get; }

    public ICurseForgeApi CurseForge { get; }

    public IConfigBackupExporter Exporter { get; }

    public IStartupControl StartupControl { get; }

    public IUpdateService UpdateService { get; }

    public DetachedJobs Jobs { get; }

    public IMaintenanceRecovery Recovery { get; }

    public IGameInstallChecker InstallChecker { get; }

    public FakeFirewall Firewall { get; }

    public FakeHostAddressProvider HostAddresses { get; }

    public InstanceCommands Instances { get; }

    public ClusterCommands Clusters { get; }

    public ConfigCommands Config { get; }

    public ModMetadataRefresher Refresher { get; }

    public ModCommands Mods { get; }

    public PlayerTracker Tracker { get; }

    public PlayerCommands Players { get; }

    public MapCommands Maps { get; }

    public SettingsCommands SettingsCommands { get; }

    public MaintenanceCommands Maintenance { get; }

    public AppDbContext Db() => Root.CreateDbContext();

    /// <summary>Migrates and seeds the database (official maps, App Setting defaults, the maintenance row).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) => Root.InitializeAsync(cancellationToken);

    public async Task<int> MapIdAsync(CancellationToken cancellationToken, string key = "TheIsland_WP")
    {
        await using var db = Db();
        return await db.Maps.Where(m => m.Key == key).Select(m => m.Id).SingleAsync(cancellationToken);
    }

    public async Task<ModLibraryEntry> AddLibraryModAsync(int id, string name, CancellationToken cancellationToken)
    {
        await using var db = Db();
        var entry = new ModLibraryEntry { Id = id, Name = name, AddedAt = Now };
        db.ModLibrary.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task<Instance> InstanceAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = Db();
        return await db.Instances.AsNoTracking().Include(i => i.Mods).Include(i => i.Cluster).SingleAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<Cluster> ClusterAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = Db();
        return await db.Clusters.AsNoTracking().Include(c => c.Mods).SingleAsync(c => c.Id == id, cancellationToken);
    }

    /// <summary>Writes a generated <c>GameUserSettings.ini</c> the way a launch would, so RCON credentials can be read.</summary>
    public async Task WriteGeneratedSettingsAsync(string slug, string? password, int rconPort, CancellationToken cancellationToken)
    {
        var directory = Root.Layout.InstanceGeneratedConfigDirectory(slug);
        Directory.CreateDirectory(directory);
        var text = $"[ServerSettings]\r\nRCONEnabled=True\r\nRCONPort={rconPort}\r\n" + (password is null ? string.Empty : $"ServerAdminPassword={password}\r\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "GameUserSettings.ini"), text, cancellationToken);
    }

    public void Dispose() => Root.Dispose();
}

/// <summary>Allows every call until <see cref="Deny"/> is set; counts how often the facades asked.</summary>
internal sealed class FakeAuthorizationGuard : IAuthorizationGuard
{
    public bool Deny { get; set; }

    public int Checks { get; private set; }

    public ValueTask EnsureAuthorizedAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return Deny ? throw new NotAuthorizedException() : ValueTask.CompletedTask;
    }
}

/// <summary>Hands the Connection card whatever addresses a test sets; the real one reads the box's adapters.</summary>
internal sealed class FakeHostAddressProvider : IHostAddressProvider
{
    public List<string> Addresses { get; } = ["192.168.1.40"];

    public IReadOnlyList<string> GetLanAddresses() => [.. Addresses];
}

/// <summary>Replies per command (case-insensitive); an unscripted command throws a <see cref="RconException"/>.</summary>
internal sealed class ScriptedRconClient : IRconClient
{
    public Dictionary<string, string> Replies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<int, RconException> FailuresByPort { get; } = [];

    public List<(RconEndpoint Endpoint, string Command, TimeSpan Timeout)> Calls { get; } = [];

    public Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((endpoint, command, timeout));
        if (FailuresByPort.TryGetValue(endpoint.Port, out var failure))
        {
            throw failure;
        }

        return Replies.TryGetValue(command, out var reply)
            ? Task.FromResult(reply)
            : throw new RconException(RconFailure.Protocol, $"No scripted reply for '{command}'.");
    }
}
