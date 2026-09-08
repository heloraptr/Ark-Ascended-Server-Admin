using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Provisioning;

namespace ArkAscendedServerAdmin.Commands;

/// <summary>Scoped, guarded facade over the INI source store and the per-instance extra overrides (plan step 16).</summary>
public interface IConfigCommands
{
    Task<IniSourceDocument> LoadIniAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken = default);

    Task<IniSaveResult> SaveIniAsync(IniOwner owner, IniFile file, string text, string expectedSha256, CancellationToken cancellationToken = default);

    Task<IniSaveResult> RetryMirrorAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken = default);

    /// <summary>The only database → disk direction; rewrites every source file from its mirror row.</summary>
    Task<CommandResult> RestoreFromDatabaseAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExtraOverride>> GetOverridesAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>Validates with <see cref="Ini.IniOverrideValidator"/>; an id of zero inserts.</summary>
    Task<CommandResult<ExtraOverride>> SaveOverrideAsync(ExtraOverride entry, CancellationToken cancellationToken = default);

    Task<CommandResult> DeleteOverrideAsync(int overrideId, CancellationToken cancellationToken = default);
}
