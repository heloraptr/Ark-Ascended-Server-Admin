

namespace ArkAscendedServerAdmin.CurseForge.Models.Games;

public class Game
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public DateTime DateModified { get; set; }
    public GameAssets Assets { get; set; } = null!;
    public int Status { get; set; }
    public int ApiStatus { get; set; }
}
