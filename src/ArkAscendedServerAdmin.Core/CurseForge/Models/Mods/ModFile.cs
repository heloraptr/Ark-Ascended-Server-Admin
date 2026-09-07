namespace ArkAscendedServerAdmin.CurseForge.Models.Mods;

public class ModFile
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int ModId { get; set; }
    public bool IsAvailable { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int ReleaseType { get; set; }
    public int FileStatus { get; set; }
    public List<FileHash> Hashes { get; set; } = [];
    public DateTime FileDate { get; set; }
    public long FileLength { get; set; }
    public long DownloadCount { get; set; }
    public long FileSizeOnDisk { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public List<string> GameVersions { get; set; } = [];
    public List<SortableGameVersion> SortableGameVersions { get; set; } = [];
    public List<FileDependency> Dependencies { get; set; } = [];
    public bool ExposeAsAlternative { get; set; }
    public int ParentProjectFileId { get; set; }
    public int AlternateFileId { get; set; }
    public bool IsServerPack { get; set; }
    public int ServerPackFileId { get; set; }
    public bool IsEarlyAccessContent { get; set; }
    public DateTime EarlyAccessEndDate { get; set; }
    public long FileFingerprint { get; set; }
    public List<Module> Modules { get; set; } = [];
}
