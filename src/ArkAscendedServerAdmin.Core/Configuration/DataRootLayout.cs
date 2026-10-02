namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// Every path the manager owns, derived from the single <c>DataRoot</c> configured in
/// <c>appsettings.json</c>. Nothing else in the code base builds a data path by hand.
/// </summary>
public sealed class DataRootLayout
{
    /// <summary>Steam app id of the ARK: Survival Ascended dedicated server.</summary>
    public const int ServerAppId = 2430930;

    public const string DatabaseFileName = "ArkAscendedServerAdmin.db";

    public DataRootLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Default location when <c>appsettings.json</c> leaves <c>DataRoot</c> empty.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ArkAscendedServerAdmin");

    public string Root { get; }

    /// <summary>The real SteamCMD install target (<c>Server\</c>), shared by every instance via junctions.</summary>
    public string Server => Path.Combine(Root, "Server");

    public string Instances => Path.Combine(Root, "Instances");

    public string Clusters => Path.Combine(Root, "Clusters");

    public string Backups => Path.Combine(Root, "Backups");

    /// <summary>Retained world data from deleted instances (<c>&lt;slug&gt;-&lt;timestamp&gt;</c>).</summary>
    public string Archive => Path.Combine(Root, "Archive");

    public string SteamCmd => Path.Combine(Root, "SteamCMD");

    /// <summary>Data Protection key ring for the auth cookie.</summary>
    public string Keys => Path.Combine(Root, "keys");

    /// <summary>Destination for "export config backup" copies of the database.</summary>
    public string Exports => Path.Combine(Root, "Exports");

    /// <summary>The manager's own state: the SQLite database and its WAL side files.</summary>
    public string Data => Path.Combine(Root, "Data");

    public string DatabasePath => Path.Combine(Data, DatabaseFileName);

    /// <summary>One JSON record per restore whose destructive phase began and has not been resolved (B2); outside every directory a backup or restore touches.</summary>
    public string RestoreJournals => Path.Combine(Data, "restore-journals");

    public string SteamCmdExecutable => Path.Combine(SteamCmd, "steamcmd.exe");

    /// <summary>The manifest that proves the install is complete (see <see cref="Install.AppManifest"/>).</summary>
    public string AppManifestPath => Path.Combine(Server, "steamapps", $"appmanifest_{ServerAppId}.acf");

    public string ServerExecutable => Path.Combine(Server, "ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe");

    /// <summary>Every directory that must exist before the service is ready.</summary>
    public IReadOnlyList<string> Directories =>
        [Root, Server, Instances, Clusters, Backups, Archive, SteamCmd, Keys, Exports, Data, RestoreJournals];

    public string InstanceDirectory(string slug) => Contained(Instances, slug);

    public string ClusterDirectory(string slug) => Contained(Clusters, slug);

    /// <summary>Canonical INI source text for a standalone instance (<c>Instances\&lt;slug&gt;\Config\</c>).</summary>
    public string InstanceConfigSourceDirectory(string slug) => Path.Combine(InstanceDirectory(slug), "Config");

    /// <summary>Canonical INI source text for a cluster (<c>Clusters\&lt;slug&gt;\Config\</c>).</summary>
    public string ClusterConfigSourceDirectory(string slug) => Path.Combine(ClusterDirectory(slug), "Config");

    /// <summary>
    /// Commands typed into the instance's console. Beside <c>Config\</c> rather than under <c>Saved</c>: backups
    /// archive only the world and cluster folders, a restore replaces only those, and deleting the instance
    /// removes everything here.
    /// </summary>
    public string InstanceRconHistoryPath(string slug) => Path.Combine(InstanceDirectory(slug), "rcon-history.txt");

    /// <summary>The instance's private <c>ShooterGame\Saved</c> (a real directory next to the junctions).</summary>
    public string InstanceSavedDirectory(string slug) => Path.Combine(InstanceDirectory(slug), "ShooterGame", "Saved");

    /// <summary>Where the generated <c>Game.ini</c> / <c>GameUserSettings.ini</c> are written; never read back as source.</summary>
    public string InstanceGeneratedConfigDirectory(string slug) =>
        Path.Combine(InstanceSavedDirectory(slug), "Config", "WindowsServer");

    /// <summary>The <c>ShooterGame.log</c> the console tails.</summary>
    public string InstanceLogPath(string slug) => Path.Combine(InstanceSavedDirectory(slug), "Logs", "ShooterGame.log");

    /// <summary>World files live under <c>Saved\&lt;slug&gt;\&lt;MapKey&gt;\</c> because the slug is passed as <c>AltSaveDirectoryName</c>.</summary>
    public string InstanceWorldDirectory(string slug, string mapKey) => Contained(Contained(InstanceSavedDirectory(slug), slug), mapKey);

    /// <summary>The executable launched through the junction tree (WMI reports this path, plan step 21).</summary>
    public string InstanceExecutable(string slug) =>
        Path.Combine(InstanceDirectory(slug), "ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe");

    public string InstanceBackupDirectory(string slug) => Contained(Backups, slug);

    /// <summary>Where a restore keeps the copy of the files it replaces (B2): <c>Backups&lt;slug&gt;_restore-safety&lt;stamp&gt;-&lt;n&gt;</c>.</summary>
    public string InstanceRestoreSafetyDirectory(string slug) => Path.Combine(InstanceBackupDirectory(slug), "_restore-safety");

    /// <summary>Retained world data of a deleted instance; the slug stays reserved while this exists (plan steps 18, 30).</summary>
    public string ArchiveDirectory(string slug, DateTimeOffset deletedAt) =>
        Contained(Archive, $"{slug}-{deletedAt.ToLocalTime():yyyyMMdd-HHmmss}");

    /// <summary>
    /// Combines a caller-supplied segment (a slug or a map key) onto <paramref name="parent"/> and refuses a result
    /// that is not strictly inside that parent, and so inside <see cref="Root"/>. Checking the parent rather than
    /// only the root also catches a <c>..</c> that climbs out of one instance's folder into another's. Slugs are
    /// generated by the app and map keys are validated when saved, so this is a backstop: a rooted or <c>..</c>
    /// segment fails here instead of reaching the disk.
    /// </summary>
    private static string Contained(string parent, string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        var combined = Path.GetFullPath(Path.Combine(parent, segment));
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || combined.Length == prefix.Length)
        {
            throw new ArgumentException($"'{segment}' does not name a location inside {parent}.", nameof(segment));
        }

        return combined;
    }

    public void EnsureDirectories()
    {
        foreach (var directory in Directories)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
