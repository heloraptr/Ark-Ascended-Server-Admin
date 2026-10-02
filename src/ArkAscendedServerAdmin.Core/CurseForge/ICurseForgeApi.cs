using ArkAscendedServerAdmin.CurseForge.Models;
using ArkAscendedServerAdmin.CurseForge.Models.Mods;

namespace ArkAscendedServerAdmin.CurseForge;

public interface ICurseForgeApi
{
    Task<List<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    /// <summary>The first matches up to a fixed cap, with CurseForge's total match count so a cut-off list can be flagged.</summary>
    Task<ModSearchResult> SearchModsAsync(string searchTerm, int? categoryId = null, CancellationToken cancellationToken = default);
    Task<List<Mod>> GetModsAsync(IEnumerable<int> modIds, bool pcOnly = true, CancellationToken cancellationToken = default);
    Task<Mod> GetModAsync(int id, CancellationToken cancellationToken = default);
}
