namespace ArkAscendedServerAdmin.Services;

public interface IServerModService
{
    bool UseApi { get; }
    Task<List<ServerModCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<List<ServerMod>> SearchModsAsync(string searchTerm, int? categoryId = null, CancellationToken cancellationToken = default);
    Task<List<ServerMod>> GetModsAsync(IEnumerable<int> modIds, bool pcOnly = true, CancellationToken cancellationToken = default);
    Task<ServerMod> GetModAsync(int id, CancellationToken cancellationToken = default);
}