
namespace ArkAscendedServerAdmin.CurseForge.Models.Mods;

public class Mod
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public ModLinks Links { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public int Status { get; set; }
    public int DownloadCount { get; set; }
    public bool IsFeatured { get; set; }
    public int PrimaryCategoryId { get; set; }
    public List<Category> Categories { get; set; } = [];
    public int ClassId { get; set; }
    public List<Author> Authors { get; set; } = [];
    public ModAsset? Logo { get; set; }
    public List<ModAsset> Screenshots { get; set; } = [];
    public int MainFileId { get; set; }
    public List<ModFile> LatestFiles { get; set; } = [];
    public List<FileIndex> LatestFilesIndexes { get; set; } = [];
    public List<FileIndex> LatestEarlyAccessFilesIndexes { get; set; } = [];
    public DateTime DateCreated { get; set; }
    public DateTime DateModified { get; set; }
    public DateTime DateReleased { get; set; }
    public bool AllowModDistribution { get; set; }
    public int GamePopularityRank { get; set; }
    public bool IsAvailable { get; set; }
    public int ThumbsUpCount { get; set; }
    public int Rating { get; set; }
}
