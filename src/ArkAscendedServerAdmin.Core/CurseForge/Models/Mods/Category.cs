namespace ArkAscendedServerAdmin.CurseForge.Models.Mods;

public class Category
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public DateTime DateModified { get; set; }
    public bool IsClass { get; set; }
    public int ClassId { get; set; }
    public int ParentCategoryId { get; set; }
    public int DisplayIndex { get; set; }
}