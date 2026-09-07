namespace ArkAscendedServerAdmin.CurseForge.Models.Mods;

public class SortableGameVersion
{
    public string GameVersionName { get; set; } = string.Empty;
    public string GameVersionPadded { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public DateTime GameVersionReleaseDate { get; set; }
    public int GameVersionTypeId { get; set; }
}
