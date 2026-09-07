using ArkAscendedServerAdmin.CurseForge.Models.Mods;

namespace ArkAscendedServerAdmin.CurseForge.Models.Services;

public interface ICurseForgeApi
{
    Task<List<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<List<Mod>> SearchModsAsync(string searchTerm, int? categoryId = null, CancellationToken cancellationToken = default);
    Task<List<Mod>> GetModsAsync(IEnumerable<int> modIds, bool pcOnly = true, CancellationToken cancellationToken = default);
    Task<Mod> GetModAsync(int id, CancellationToken cancellationToken = default);
}
