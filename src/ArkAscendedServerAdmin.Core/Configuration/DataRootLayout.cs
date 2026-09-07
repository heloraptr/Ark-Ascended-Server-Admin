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

    public string DatabasePath => Path.Combine(Root, DatabaseFileName);

    public string SteamCmdExecutable => Path.Combine(SteamCmd, "steamcmd.exe");

    /// <summary>The manifest that proves the install is complete (see <see cref="Install.AppManifest"/>).</summary>
    public string AppManifestPath => Path.Combine(Server, "steamapps", $"appmanifest_{ServerAppId}.acf");

    public string ServerExecutable => Path.Combine(Server, "ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe");

    /// <summary>Every directory that must exist before the service is ready.</summary>
    public IReadOnlyList<string> Directories =>
        [Root, Server, Instances, Clusters, Backups, Archive, SteamCmd, Keys, Exports];

    public string InstanceDirectory(string slug) => Path.Combine(Instances, slug);

    public string ClusterDirectory(string slug) => Path.Combine(Clusters, slug);

    public void EnsureDirectories()
    {
        foreach (var directory in Directories)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
