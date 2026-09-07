namespace ArkAscendedServerAdmin.CurseForge.Models.Mods;

public class FileIndex
{
    public string GameVersion { get; set; } = string.Empty;
    public int FileId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public int ReleaseType { get; set; }
    public int GameVersionTypeId { get; set; }
    public int ModLoader { get; set; }
}
