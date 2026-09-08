using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Commands;

/// <summary>Scoped, guarded facade for the Maps page. Official maps are seeded; rows stay editable.</summary>
public interface IMapCommands
{
    Task<IReadOnlyList<Map>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Map id → number of instances using it.</summary>
    Task<IReadOnlyDictionary<int, int>> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>An id of zero inserts; the key must be unique and usable on the command line.</summary>
    Task<CommandResult<Map>> SaveAsync(Map map, CancellationToken cancellationToken = default);

    /// <summary>Refused while an instance uses the map.</summary>
    Task<CommandResult> DeleteAsync(int mapId, CancellationToken cancellationToken = default);
}
