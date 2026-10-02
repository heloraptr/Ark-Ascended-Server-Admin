using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Provisioning;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>
/// Writes the two source INI files for a newly created instance or cluster. It lives apart from both
/// facades so each can seed a new owner without reaching into the other. Public only because the public
/// facade constructors take it; nothing outside the command layer uses it.
/// </summary>
public sealed class IniSeeder(IIniSourceStore iniStore)
{
    /// <summary>
    /// Writes both source files for a new owner from the chosen starting point (plan step 16, DESIGN §5).
    /// A non-null <paramref name="adminPassword"/> is set as <c>ServerAdminPassword</c> in the seeded
    /// <c>GameUserSettings.ini</c>, replacing whatever the source had.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store refused to write one of the files.</exception>
    public async Task SeedAsync(IniOwner owner, ConfigSourceKind source, int? sourceId, CancellationToken cancellationToken, string? adminPassword = null)
    {
        foreach (var file in new[] { IniFile.Game, IniFile.GameUserSettings })
        {
            var text = source switch
            {
                ConfigSourceKind.Blank => string.Empty,
                ConfigSourceKind.CopyFromInstance when sourceId is { } id => (await iniStore.LoadAsync(IniOwner.ForInstance(id), file, cancellationToken)).Text,
                ConfigSourceKind.CopyFromCluster when sourceId is { } id => (await iniStore.LoadAsync(IniOwner.ForCluster(id), file, cancellationToken)).Text,
                _ => file == IniFile.Game ? IniTemplates.DefaultGameIni : IniTemplates.DefaultGameUserSettings,
            };

            if (file == IniFile.GameUserSettings && adminPassword is not null)
            {
                var ini = IniText.Parse(text);
                ini.Set(IniGenerator.ServerSettingsSection, "ServerAdminPassword", adminPassword);
                text = ini.ToString();
            }

            var current = await iniStore.LoadAsync(owner, file, cancellationToken);
            var result = await iniStore.SaveAsync(owner, file, text, current.Sha256, cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Error ?? $"Writing {file} failed.");
            }
        }
    }
}
