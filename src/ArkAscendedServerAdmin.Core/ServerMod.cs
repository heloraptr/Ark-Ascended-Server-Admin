namespace ArkAscendedServerAdmin;

public class ServerMod
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? ImageUrl { get; set; }
    public List<ServerModCategory> Categories { get; set; } = null!;
}
