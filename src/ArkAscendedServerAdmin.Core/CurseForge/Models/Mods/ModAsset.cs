namespace ArkAscendedServerAdmin.CurseForge.Models.Mods;

public class ModAsset
{
    public int Id { get; set; }
    public int ModId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}