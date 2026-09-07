using ArkAscendedServerAdmin.CurseForge.Models.Mods;

namespace ArkAscendedServerAdmin;

public static class ModExtensions
{
    public static ServerModCategory FromCurseForgeCategory(this Category category) =>
        new()
        {
            Id = category.Id,
            Name = category.Name,
            ImageUrl = category.IconUrl
        };

    public static List<ServerModCategory> FromCurseForgeCategories(this IEnumerable<Category> categories) =>
        [.. categories.Select(x => x.FromCurseForgeCategory())];

    public static ServerMod FromCurseForgeMod(this Mod mod) =>
        new()
        {
            Id = mod.Id,
            Name = mod.Name,
            ImageUrl = mod.Logo.ThumbnailUrl,
            Categories = mod.Categories.FromCurseForgeCategories()
        };

    public static List<ServerMod> FromCurseForgeMods(this IEnumerable<Mod> mods) =>
        [.. mods.Select(x => x.FromCurseForgeMod())];
}
