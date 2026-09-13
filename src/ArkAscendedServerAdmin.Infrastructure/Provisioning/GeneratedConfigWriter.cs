using System.Text;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Provisioning;

/// <summary>
/// Runs the INI pipeline for one instance before launch (plan step 16): source text from
/// <see cref="IIniSourceStore"/> (the cluster's for a clustered instance, its own otherwise) plus the
/// instance's typed fields and overrides through <see cref="IniGenerator"/>, written to
/// <c>Saved\Config\WindowsServer</c> with one <c>.bak</c> of the previous file, and the admin whitelist union
/// next to them. Generated files are never read as source.
/// </summary>
/// <remarks>
/// The whitelist is written to <c>ShooterGame\Saved\AllowedCheaterAccountIDs.txt</c>. Whether that is the
/// exact location the game reads when launched with <c>AltSaveDirectoryName</c> (as opposed to a path under
/// the save directory or next to the binaries) is unverified: it is on the owner-in-the-loop list in
/// HANDOVER §5 and will be confirmed once the app runs end to end.
/// </remarks>
public sealed class GeneratedConfigWriter(
    DataRootLayout layout,
    IDbContextFactory<AppDbContext> contextFactory,
    IIniSourceStore sourceStore,
    IAppSettingsStore settings,
    ILogger<GeneratedConfigWriter> logger) : IGeneratedConfigWriter
{
    public const string AdminWhitelistFileName = "AllowedCheaterAccountIDs.txt";

    public const string BackupExtension = ".bak";

    public async Task<GeneratedConfig> WriteAsync(int instanceId, CancellationToken cancellationToken)
    {
        Instance instance;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            instance = await db.Instances
                .AsNoTracking()
                .Include(i => i.Cluster)
                .Include(i => i.Map)
                .Include(i => i.ExtraOverrides)
                .SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken)
                ?? throw new InvalidOperationException($"Instance {instanceId} does not exist.");
        }

        var owner = instance.ClusterId is { } clusterId ? IniOwner.ForCluster(clusterId) : IniOwner.ForInstance(instance.Id);
        var gameSource = await sourceStore.LoadAsync(owner, IniFile.Game, cancellationToken);
        var gameUserSettingsSource = await sourceStore.LoadAsync(owner, IniFile.GameUserSettings, cancellationToken);

        var input = new GenerationInput(
            gameSource.Text,
            gameUserSettingsSource.Text,
            instance.SessionName,
            instance.GamePort,
            instance.RconPort,
            instance.MaxPlayers,
            instance.ExtraOverrides.OrderBy(o => o.Id).Select(IniOverrideSpec.From).ToList(),
            instance.Cluster?.AdminWhitelist ?? string.Empty,
            instance.AdminWhitelist,
            (await settings.GetAsync(cancellationToken)).AdminWhitelist);

        var generated = IniGenerator.Generate(input);

        var configDirectory = layout.InstanceGeneratedConfigDirectory(instance.Slug);
        await WriteWithBackupAsync(Path.Combine(configDirectory, IniFileNames.Game), generated.GameIni, cancellationToken);
        await WriteWithBackupAsync(Path.Combine(configDirectory, IniFileNames.GameUserSettings), generated.GameUserSettingsIni, cancellationToken);
        await AtomicFile.WriteAllTextAsync(
            Path.Combine(layout.InstanceSavedDirectory(instance.Slug), AdminWhitelistFileName),
            WhitelistText(generated.AdminWhitelist),
            cancellationToken);

        foreach (var warning in generated.Warnings)
        {
            logger.LogWarning("Instance {Slug} config generation: {Warning}", instance.Slug, warning);
        }

        return generated;
    }

    public async Task<string?> ReadGeneratedGameUserSettingsAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = Path.Combine(layout.InstanceGeneratedConfigDirectory(slug), IniFileNames.GameUserSettings);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken) : null;
    }

    private static Task WriteWithBackupAsync(string path, string text, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextWithBackupAsync(path, text, path + BackupExtension, cancellationToken);

    /// <summary>One id per line with Windows line endings; an empty list yields an empty file so a cleared whitelist takes effect.</summary>
    private static string WhitelistText(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? string.Empty : string.Join("\r\n", ids) + "\r\n";
}
